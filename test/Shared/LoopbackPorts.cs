using System;
using System.Net;
using System.Net.Sockets;

namespace EmbedIO.PlatformTests
{
    // Test-only server ports. A port must be free for TCP and UDP on IPv4 and IPv6 loopback,
    // because a combined listener binds HTTP/3's UDP socket on the same number and "localhost"
    // binds both families. Ports come from below the OS dynamic range, so client sockets in
    // TIME_WAIT never collide with them.
    internal static class LoopbackPorts
    {
        internal const int First = 20000;
        internal const int Last = 30000;

        internal static int Free()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var port = Random.Shared.Next(First, Last);
                try
                {
                    using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    using var udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    udp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                    using var tcp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                    tcp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                    return port;
                }
                catch (SocketException)
                {
                    // Taken for one protocol or family; try another port.
                }
            }

            throw new InvalidOperationException("No port is free for TCP and UDP on both loopback addresses.");
        }
    }
}
