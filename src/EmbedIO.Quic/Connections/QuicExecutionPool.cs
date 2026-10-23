using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Quic
{
    // Each registration has at most one queued or executing turn. Native calls
    // and cleanup for that owner are serialized; notifications coalesce.
    internal sealed class QuicExecutionPool : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<Registration> _owners = [];
        private readonly Channel<Registration> _ready;
        private readonly Task[] _workers;
        private readonly int _capacity;
        private Task? _shutdown;
        private long _waiting;
        private long _progress;
        private long _averageWait;

        internal QuicExecutionPool(int workers, int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(workers, 64);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);
            _capacity = capacity;
            _ready = Channel.CreateBounded<Registration>(new BoundedChannelOptions(capacity)
            {
                SingleReader = workers == 1,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _workers =
            [
                .. Enumerable
                    .Range(0, workers)
                    .Select(_ => Task.Run(RunAsync))
            ];
        }

        internal int Active
        {
            get
            {
                lock (_gate) return _owners.Count;
            }
        }

        // How long a turn waits for a worker, in Stopwatch ticks: MsQuic's worker average
        // (seven parts history, one part the latest wait), reset whenever nothing waits, or,
        // while turns wait, the time since a worker last took one, whichever is longer.
        // A connection's TLS work runs in its turns, so admission refuses new connections
        // above a bound and queued handshake work stays near it.
        internal TimeSpan QueueDelay
        {
            get
            {
                var delay = Volatile.Read(ref _averageWait);

                if (Volatile.Read(ref _waiting) > 0)
                    delay = Math.Max(delay, Stopwatch.GetTimestamp() - Volatile.Read(ref _progress));

                return Stopwatch.GetElapsedTime(0, delay);
            }
        }

        internal Registration Register(Action execute, Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(execute);
            ArgumentNullException.ThrowIfNull(cleanup);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_shutdown != null, this);

                if (_owners.Count >= _capacity)
                    throw new InvalidOperationException("QUIC execution owner limit exceeded.");

                var owner = new Registration(this, execute, cleanup);
                _owners.Add(owner);
                return owner;
            }
        }

        private void Schedule(Registration owner)
        {
            // Without waiting turns no worker is late; the clock starts with this one.
            if (Interlocked.Increment(ref _waiting) == 1) Volatile.Write(ref _progress, owner.QueuedAt);

            // Capacity is the registration limit, and each registration occupies
            // at most one slot. A full or completed queue is an invariant failure.
            if (!_ready.Writer.TryWrite(owner))
                throw new InvalidOperationException("QUIC execution queue lost capacity.");
        }

        private void Started(long queuedAt)
        {
            var now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _progress, now);

            if (Interlocked.Decrement(ref _waiting) == 0)
            {
                Volatile.Write(ref _averageWait, 0);
                return;
            }

            var wait = now - queuedAt;
            long current;

            do
                current = Volatile.Read(ref _averageWait);

            while (Interlocked.CompareExchange(ref _averageWait, (current * 7 + wait) / 8, current) != current);
        }

        private void Release(Registration owner)
        {
            lock (_gate) _owners.Remove(owner);
        }

        private async Task RunAsync()
        {
            await foreach (var owner in _ready.Reader
                .ReadAllAsync()
                .ConfigureAwait(false)) owner.Run();
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _shutdown ??= StopAsync([.. _owners]);
                return new ValueTask(_shutdown);
            }
        }

        private async Task StopAsync(Registration[] owners)
        {
            foreach (var owner in owners) owner.RequestClose();

            var closing = Task.WhenAll(owners.Select(owner => owner.Completion));
            await closing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ready.Writer.TryComplete();
            var workers = Task.WhenAll(_workers);
            await workers.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            var failures = new List<Exception>();

            if (closing.Exception != null) failures.AddRange(closing.Exception.InnerExceptions);

            if (workers.Exception != null) failures.AddRange(workers.Exception.InnerExceptions);

            if (failures.Count != 0) throw new AggregateException("QUIC execution shutdown failed.", failures);
        }

        internal sealed class Registration : IAsyncDisposable
        {
            private readonly QuicExecutionPool _pool;
            private readonly Action _execute;
            private readonly Action _cleanup;

            internal Registration(QuicExecutionPool pool, Action execute, Action cleanup)
            {
                _pool = pool;
                _execute = execute;
                _cleanup = cleanup;
            }

            private readonly object _gate = new();
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _queued;
            private bool _running;
            private bool _pending;
            private bool _closing;
            private bool _done;

            internal Task Completion => _completion.Task;

            // When the waiting turn was queued, in Stopwatch ticks.
            internal long QueuedAt { get; private set; }

            internal bool Closing
            {
                get
                {
                    lock (_gate) return _closing || _done;
                }
            }

            internal bool Wake()
            {
                lock (_gate)
                {
                    if (_closing || _done) return false;

                    _pending = true;
                    Schedule();
                    return true;
                }
            }

            private void Schedule()
            {
                if (_queued || _running) return;

                _queued = true;
                QueuedAt = Stopwatch.GetTimestamp();
                _pool.Schedule(this);
            }

            public ValueTask DisposeAsync()
            {
                RequestClose();
                return new ValueTask(_completion.Task);
            }

            internal void RequestClose()
            {
                lock (_gate)
                {
                    if (!_done)
                    {
                        _closing = true;
                        Schedule();
                    }
                }
            }

            internal void Run()
            {
                bool closing;

                lock (_gate)
                {
                    if (!_queued || _running || _done)
                        throw new InvalidOperationException("QUIC owner ran concurrently.");

                    _queued = false;
                    _running = true;
                    _pending = false;
                    closing = _closing;
                }

                _pool.Started(QueuedAt);
                Exception? failure = null;

                if (!closing)
                {
                    try
                    {
                        _execute();
                    }

                    catch (Exception error) when (
                        error is not OutOfMemoryException
                            and not StackOverflowException
                            and not AccessViolationException
                    )
                    {
                        failure = error;
                        closing = true;
                    }
                }

                lock (_gate)
                {
                    closing |= _closing;

                    if (!closing)
                    {
                        _running = false;

                        if (_pending) Schedule();

                        return;
                    }

                    _closing = true;
                }

                try
                {
                    _cleanup();
                }

                catch (Exception error) when (
                    error is not OutOfMemoryException
                        and not StackOverflowException
                        and not AccessViolationException
                )
                {
                    failure = failure == null ? error : new AggregateException(failure, error);
                }

                lock (_gate)
                {
                    _running = false;
                    _done = true;
                }

                _pool.Release(this);

                if (failure == null) _completion.TrySetResult();

                else _completion.TrySetException(failure);
            }
        }
    }
}
