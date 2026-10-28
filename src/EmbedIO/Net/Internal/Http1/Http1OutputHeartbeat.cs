using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Each connection owns one registration. Only busy registrations enter the shared
    // watchlist; fast responses publish and clear output within the same busy epoch.
    internal static class Http1OutputHeartbeat
    {
        private static readonly object Sync = new();
        private static readonly List<Registration> Connections = [];
        private static TimeSpan _interval = TimeSpan.FromMilliseconds(10);
        private static Timer? _timer;
        private static int _registrations;
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
                    if (_registrations != 0)
                        throw new InvalidOperationException("Output connections are registered.");

                    _interval = value;
                }
            }
        }

        internal static int MonitoredCount
        {
            get
            {
                lock (Sync) return _registrations;
            }
        }

        internal static int ActiveCount
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

        // Called under Sync after adding a busy registration. An idle connection is
        // never scanned just because another connection has held output.
        private static void Start()
        {
            if (_running != 0) return;

            Volatile.Write(ref _running, 1);
            (_timer ??= CreateTimer()).Change(_interval, _interval);
        }

        // Called under Sync. Pruning first marks each removed registration inactive,
        // then rechecks its output, so a concurrent Hold cannot lose a timer wakeup.
        private static void StopIfIdle()
        {
            if (Connections.Count != 0) return;

            Volatile.Write(ref _running, 0);
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
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
                    // Descending traversal visits a swapped last entry at most once and
                    // never loops over an entry reactivated during its own pruning.
                    for (
                        var slot = Connections.Count - 1;
                        slot >= 0;
                        slot--
                    )
                    {
                        var connection = Connections[slot];

                        if (!connection.HasHeldOutput) connection.PruneIfIdle();

                        else if (connection.IsReady(tick)) (ready ??= []).Add(connection);
                    }

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
            private int _slot = -1;

            internal Registration()
            {
                lock (Sync) _registrations++;
            }

            internal bool HasHeldOutput => Volatile.Read(ref _output) != null;

            internal bool IsReady(int tick) =>
                Volatile
                    .Read(ref _output)
                    ?.IsHeldBefore(tick)
                    == true;

            internal void Hold(ResponseStream output)
            {
                // The full publication fence pairs with pruning's inactive transition.
                // Either its recheck sees this output or this thread sees an inactive slot.
                Interlocked.Exchange(ref _output, output);

                if (Volatile.Read(ref _retired) != 0)
                {
                    Release(output);
                    return;
                }

                if (Volatile.Read(ref _slot) >= 0) return;

                lock (Sync)
                {
                    if (_retired != 0) return;

                    AddActive();
                    Start();
                }
            }

            internal void Release(ResponseStream output) => Interlocked.CompareExchange(ref _output, null, output);

            // All list mutations run under Sync. Retaining an entry until a tick keeps
            // successive fast responses from taking the shared lock for each response.
            private void AddActive()
            {
                if (_slot >= 0) return;

                var slot = Connections.Count;
                Connections.Add(this);
                Volatile.Write(ref _slot, slot);
            }

            private void RemoveActive()
            {
                var slot = _slot;

                if (slot < 0) return;

                var last = Connections[Connections.Count - 1];
                Connections[slot] = last;
                Volatile.Write(ref last._slot, slot);
                Connections.RemoveAt(Connections.Count - 1);
                Interlocked.Exchange(ref _slot, -1);
            }

            // Called under Sync. Hold publishes before checking the slot. If it saw the
            // old active slot, this post-removal recheck sees its publication and restores
            // the entry. Otherwise Hold takes Sync and activates it after pruning exits.
            internal void PruneIfIdle()
            {
                if (HasHeldOutput) return;

                RemoveActive();

                if (_retired == 0 && HasHeldOutput) AddActive();
            }

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
                    RemoveActive();
                    _registrations--;

                    StopIfIdle();
                }
            }
        }
    }
}
