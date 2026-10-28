using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Connections register once. Fast inline responses only publish and clear their held
    // output; one timer wakes for the process while any output remains held.
    internal static class Http1OutputHeartbeat
    {
        private static readonly object Sync = new();
        private static readonly List<Registration> Connections = [];
        private static TimeSpan _interval = TimeSpan.FromMilliseconds(10);
        private static Timer? _timer;
        private static int _running;
        private static int _ticking;
        private static int _tick;

        internal static int CurrentTick => Volatile.Read(ref _tick);

        // Tests adjust the interval only while no connections are registered.
        internal static TimeSpan Interval
        {
            get => _interval;
            set
            {
                if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(value));

                lock (Sync)
                {
                    if (Connections.Count != 0)
                        throw new InvalidOperationException("Output connections are registered.");

                    _interval = value;
                }
            }
        }

        internal static int MonitoredCount
        {
            get
            {
                lock (Sync) return Connections.Count;
            }
        }

        private static Timer CreateTimer()
        {
            if (ExecutionContext.IsFlowSuppressed()) return new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);

            // The shared timer must not retain the first application's ambient context.
            using var flow = ExecutionContext.SuppressFlow();
            return new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite);
        }

        private static void Start()
        {
            if (Volatile.Read(ref _running) != 0) return;

            lock (Sync)
            {
                if (_running != 0 || !HasHeldOutput()) return;

                Volatile.Write(ref _running, 1);
                (_timer ??= CreateTimer()).Change(_interval, _interval);
            }
        }

        private static bool HasHeldOutput()
        {
            foreach (var connection in Connections)
                if (connection.HasHeldOutput) return true;

            return false;
        }

        // Called under Sync. Hold publishes before checking _running, so either the
        // recheck observes it or its subsequent Start acquires Sync and wakes the timer.
        private static void StopIfIdle()
        {
            if (HasHeldOutput()) return;

            Volatile.Write(ref _running, 0);
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);

            if (HasHeldOutput())
            {
                Volatile.Write(ref _running, 1);
                _timer?.Change(_interval, _interval);
            }
        }

        private static void Tick(object? state)
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

            try
            {
                var tick = Interlocked.Increment(ref _tick);
                List<Registration>? ready = null;

                lock (Sync)
                {
                    foreach (var connection in Connections)
                        if (connection.IsReady(tick)) (ready ??= []).Add(connection);

                    StopIfIdle();
                }

                // A slow connection owns only its own output gate, never the shared lock.
                if (ready != null)
                    foreach (var connection in ready) connection.Send(tick);
            }

            finally
            {
                Volatile.Write(ref _ticking, 0);
            }
        }

        internal sealed class Registration : IDisposable
        {
            private ResponseStream? _output;
            private int _retired;
            private int _sending;
            private int _slot;

            internal Registration()
            {
                lock (Sync)
                {
                    _slot = Connections.Count;
                    Connections.Add(this);
                }
            }

            internal bool HasHeldOutput => Volatile.Read(ref _output) != null;

            internal bool IsReady(int tick) =>
                Volatile
                    .Read(ref _output)
                    ?.IsHeldBefore(tick)
                    == true;

            internal void Hold(ResponseStream output)
            {
                Volatile.Write(ref _output, output);

                if (Volatile.Read(ref _retired) != 0)
                {
                    Release(output);
                    return;
                }

                Start();
            }

            internal void Release(ResponseStream output) => Interlocked.CompareExchange(ref _output, null, output);

            internal void Send(int tick)
            {
                if (Volatile.Read(ref _retired) != 0 || Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

                _ = SendAsync(tick);
            }

            private async Task SendAsync(int tick)
            {
                try
                {
                    var output = Volatile.Read(ref _output);

                    if (output == null || Volatile.Read(ref _retired) != 0) return;

                    try
                    {
                        await output
                            .SendHeartbeatAsync(tick)
                            .ConfigureAwait(false);
                    }

                    catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                    {
                        await output
                            .AbandonAsync()
                            .ConfigureAwait(false);
                        RequestAbortRegistry
                            .Find(output)
                            ?.Abort();
                    }
                }

                finally
                {
                    Volatile.Write(ref _sending, 0);
                }
            }

            public void Dispose()
            {
                lock (Sync)
                {
                    if (Interlocked.Exchange(ref _retired, 1) != 0) return;

                    Volatile.Write(ref _output, null);
                    var last = Connections[Connections.Count - 1];
                    Connections[_slot] = last;
                    last._slot = _slot;
                    Connections.RemoveAt(Connections.Count - 1);
                    _slot = -1;

                    StopIfIdle();
                }
            }
        }
    }
}
