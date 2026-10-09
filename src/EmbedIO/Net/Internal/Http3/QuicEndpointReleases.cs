#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace EmbedIO.Net.Internal.Http3
{
    // MsQuic 2.6.2 on macOS queues each UDP socket's close to a worker thread, so
    // QuicListener disposal can return while the port is still bound, and binding the
    // same endpoint at once fails with AddressAlreadyInUse. Bridge only that window:
    // retry a rebind of an endpoint this process released moments ago. Remove this once
    // the required MsQuic closes the socket before disposal completes; see
    // docs/project/http-engine.md.
    internal sealed class QuicEndpointReleases
    {
        // Every traced deferred close completed within 230 microseconds of disposal.
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private readonly object _sync = new();
        private readonly Dictionary<IPEndPoint, long> _released = new();
        private readonly long _window;
        private readonly Func<long> _clock;

        internal QuicEndpointReleases(TimeSpan window, Func<long> clock)
        {
            _window = (long)(window.TotalSeconds * Stopwatch.Frequency);
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        internal static QuicEndpointReleases Shared { get; } = new(Window, Stopwatch.GetTimestamp);

        // Call after the listener bound to the endpoint has been disposed.
        internal void Record(IPEndPoint endpoint)
        {
            var now = _clock();
            lock (_sync)
            {
                foreach (var stale in _released.Where(entry => now - entry.Value > _window).Select(entry => entry.Key).ToList())
                    _released.Remove(stale);
                _released[new IPEndPoint(endpoint.Address, endpoint.Port)] = now;
            }
        }

        // Retries only AddressAlreadyInUse, only where the platform defers the close, and
        // only while the endpoint's recorded release is recent; anything else propagates.
        internal T Bind<T>(IPEndPoint endpoint, bool deferredClose, Func<T> bind)
        {
            while (true)
            {
                try { return bind(); }
                catch (SocketException error) when (deferredClose && error.SocketErrorCode == SocketError.AddressAlreadyInUse && IsRecent(endpoint))
                {
                    Thread.Sleep(1);
                }
            }
        }

        private bool IsRecent(IPEndPoint endpoint)
        {
            var now = _clock();
            lock (_sync) return _released.TryGetValue(endpoint, out var released) && now - released <= _window;
        }
    }
}
#endif
