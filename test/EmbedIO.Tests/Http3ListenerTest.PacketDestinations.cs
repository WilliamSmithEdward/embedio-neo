using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        // macOS reports no destination for a datagram queued before the listener's first
        // receive, nor for any IPv4 datagram on a dual-mode socket. Such a datagram arrived
        // on the bound address and must not stop the listener.
        [Test]
        public void DatagramsWithoutAReportedDestinationUseTheBoundEndpoint()
        {
            var binding = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.EmbedIOQuicListenerBinding")
                ?? throw new AssertionException("Missing EmbedIO.Quic listener binding.");
            var destinationOf = binding.GetMethod("DestinationOf", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing destination resolver.");
            var bound = new IPEndPoint(IPAddress.IPv6Any, 4433);
            var reported = Activator.CreateInstance(
                typeof(IPPacketInformation),
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [IPAddress.Loopback, 1],
                null
            )
                ?? throw new AssertionException("Missing packet information.");

            var missing =
                destinationOf.Invoke(
                    null,
                    [
                        default(IPPacketInformation),
                        bound
                    ]
                ) as IPEndPoint;
            var known = destinationOf.Invoke(null, [reported, bound]) as IPEndPoint;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(missing, Is.EqualTo(new IPEndPoint(IPAddress.IPv6Any, 4433)));
                Assert.That(known, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, 4433)));
            }
        }

        [Test]
        public async Task WildcardHttp3ListenerServesIpv4Clients()
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            await WildcardIpv4Wire();
        }

        // A wildcard prefix binds one dual-mode socket, so IPv4 clients reach it through
        // mapped addresses. Each fresh connection must be served and leave the listener up.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task WildcardIpv4Wire()
        {
            using var certificate = Certificate();
            var port = new Uri(Prefix()).Port;
            using var server = new WebServer(o => o
                .WithUrlPrefix($"https://*:{port}/")
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)).WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);

            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await using var connection = await QuicConnection.ConnectAsync(
                        new QuicClientConnectionOptions
                        {
                            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
                            DefaultCloseErrorCode = 0x100,
                            DefaultStreamErrorCode = 0x10c,
                            MaxInboundUnidirectionalStreams = 8,
                            MaxInboundBidirectionalStreams = 0,
                            ClientAuthenticationOptions = new SslClientAuthenticationOptions
                            {
                                TargetHost = "localhost",
                                ApplicationProtocols = [new SslApplicationProtocol("h3")],
                                RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                                    == certificate.GetCertHashString()
                            }
                        },
                        stop.Token
                    );
                    await using var control = await connection.OpenOutboundStreamAsync(
                        QuicStreamType.Unidirectional,
                        stop.Token
                    );
                    await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                    await HealthySibling(connection, $"localhost:{port}", stop.Token);
                    await connection.CloseAsync(0x100, stop.Token);
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(server.Listener.IsListening, Is.True);
                    Assert.That(running.IsCompleted, Is.False);
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
