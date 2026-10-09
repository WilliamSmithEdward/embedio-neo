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
    // MsQuic can keep a UDP port bound briefly after QuicListener disposal returns: on
    // macOS its kqueue datapath closes the socket later on a worker thread, and on every
    // platform an accepted connection keeps the listener's binding until MsQuic frees
    // the connection. Binding the same endpoint at once can then fail with
    // AddressAlreadyInUse even though the server has stopped. Bridge only that window:
    // retry a rebind of an endpoint this process released moments ago. Remove this once
    // the required MsQuic releases the port before disposal completes; see
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

        // Retries only AddressAlreadyInUse, and only while the endpoint's recorded release
        // is recent; anything else propagates. A genuine conflict fails once the window ends.
        internal T Bind<T>(IPEndPoint endpoint, Func<T> bind)
        {
            while (true)
            {
                try { return bind(); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse && IsRecent(endpoint))
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
