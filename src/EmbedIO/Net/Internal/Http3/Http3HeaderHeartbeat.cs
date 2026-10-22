#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal.Http3
{
    // Checks the stream header deadlines of every HTTP/3 connection once per second on one
    // timer shared by the process, as RequestBodyRateHeartbeat checks body data rates.
    // Connections join when they start accepting streams and leave when they end, so the
    // per-stream work stays under each connection's own lock. The timer runs only while a
    // connection is registered.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class Http3HeaderHeartbeat
    {
        internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
        private static readonly object Sync = new();
        private static readonly List<Http3QuicConnection> Registered = [];
        private static TimeSpan _interval = DefaultInterval;
        private static Timer? _timer;
        private static int _ticking;

        // The check interval. Tests shorten it through reflection while nothing is
        // registered; the timer takes it when the first connection is added.
        internal static TimeSpan Interval
        {
            get => _interval;
            set
            {
                if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(value));

                _interval = value;
            }
        }

        // Read by tests through reflection.
        internal static int RegisteredCount
        {
            get
            {
                lock (Sync) return Registered.Count;
            }
        }

        internal static void Add(Http3QuicConnection connection)
        {
            lock (Sync)
            {
                if (connection.HeartbeatSlot >= 0) return;

                connection.HeartbeatSlot = Registered.Count;
                Registered.Add(connection);

                if (Registered.Count == 1)
                    (_timer ??= new Timer(Tick, null, Timeout.Infinite, Timeout.Infinite)).Change(_interval, _interval);
            }
        }

        internal static void Remove(Http3QuicConnection connection)
        {
            lock (Sync)
            {
                var slot = connection.HeartbeatSlot;

                if (slot < 0) return;

                var last = Registered[Registered.Count - 1];
                Registered[slot] = last;
                last.HeartbeatSlot = slot;
                Registered.RemoveAt(Registered.Count - 1);
                connection.HeartbeatSlot = -1;

                if (Registered.Count == 0) _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private static void Tick(object? state)
        {
            if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return;

            try
            {
                Http3QuicConnection[] connections;

                lock (Sync) connections = [.. Registered];

                // Each connection takes its own lock; none is taken under this one.
                var now = Stopwatch.GetTimestamp();

                foreach (var connection in connections)
                    try
                    {
                        connection.ExpireHeaders(now);
                    }

                    // An exception escaping a timer callback would end the process.
                    catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                    {
                        error.Log("HTTP/3 connection", "Exception while expiring stream header deadlines.");
                    }
            }

            finally
            {
                Volatile.Write(ref _ticking, 0);
            }
        }
    }
}
#endif
