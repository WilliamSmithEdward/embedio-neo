using System;
using System.Globalization;
using System.Threading;

namespace EmbedIO.Net.Internal
{
    // Connection-level limits of one TCP listener: how many connections it keeps open,
    // how long a connection may take to deliver its first request head, and how long it
    // may stay idle between requests. Endpoints apply the policy of the listener that
    // opened them, so a listener with its own policy never shares an endpoint.
    internal sealed class TcpConnectionPolicy
    {
        // 90 seconds for the first request head and 15 between requests; HTTP/2
        // connections without streams have no idle deadline.
        internal static readonly TcpConnectionPolicy Default = new(null, 90000, null);

        private int _open;

        internal TcpConnectionPolicy(int? maximumConnections, int headerTimeoutMilliseconds, int? keepAliveMilliseconds)
        {
            MaximumConnections = maximumConnections;
            HeaderTimeoutMilliseconds = headerTimeoutMilliseconds;
            ConfiguredKeepAliveMilliseconds = keepAliveMilliseconds;
            // Whole seconds, rounded up, so a client never outlives the server's deadline by a fraction.
            var seconds = (int)Math.Ceiling(Http1KeepAliveMilliseconds / 1000.0);
            Http1KeepAliveHeader = "timeout=" + seconds.ToString(CultureInfo.InvariantCulture);
        }

        internal int? MaximumConnections { get; }

        internal int HeaderTimeoutMilliseconds { get; }

        // Null keeps the existing behavior: 15 seconds on HTTP/1, none on HTTP/2.
        internal int? ConfiguredKeepAliveMilliseconds { get; }

        internal int Http1KeepAliveMilliseconds => ConfiguredKeepAliveMilliseconds ?? 15000;

        // Value of the HTTP/1 Keep-Alive response header.
        internal string Http1KeepAliveHeader { get; }

        internal bool IsDefault => ReferenceEquals(this, Default);

        internal int OpenConnections => Volatile.Read(ref _open);

        // Claims a connection slot. Every successful claim is released exactly once.
        internal bool TryAcquire()
        {
            if (MaximumConnections is not int maximum)
            {
                _ = Interlocked.Increment(ref _open);
                return true;
            }
            while (true)
            {
                var open = Volatile.Read(ref _open);
                if (open >= maximum) return false;
                if (Interlocked.CompareExchange(ref _open, open + 1, open) == open) return true;
            }
        }

        internal void Release() => _ = Interlocked.Decrement(ref _open);
    }
}
