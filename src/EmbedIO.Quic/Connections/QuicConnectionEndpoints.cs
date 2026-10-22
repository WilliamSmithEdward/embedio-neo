using System;
using System.Net;
using System.Net.Sockets;

namespace EmbedIO.Quic
{
    // Endpoint objects are mutable, including IPv6 scope IDs. Never share routing
    // dictionary keys with an application or its connection factory.
    internal sealed class QuicConnectionEndpoints
    {
        internal QuicConnectionEndpoints(IPEndPoint peer, IPEndPoint? local)
        {
            ArgumentNullException.ThrowIfNull(peer);
            Peer = Copy(peer);
            Local = local == null ? null : Copy(local);
        }

        internal IPEndPoint Peer { get; }
        internal IPEndPoint? Local { get; }

        internal static IPEndPoint Copy(IPEndPoint endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var address = endpoint.Address;
            return new IPEndPoint(
                address.AddressFamily == AddressFamily.InterNetworkV6
                    ? new IPAddress(address.GetAddressBytes(), address.ScopeId)
                    : new IPAddress(address.GetAddressBytes()),
                endpoint.Port
            );
        }
    }
}
