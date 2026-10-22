using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A stopped or drained HTTP/3 server releases its UDP endpoint before stopping
    // returns, so a new server binds it at once. Restarting an idle or served listener
    // is covered by StoppedListenerRebindsItsEndpointImmediately and
    // ServedListenerRebindsItsEndpointImmediately.
    [NonParallelizable]
    public sealed class Http3ListenerRestartTest
    {
        // After a served request, DrainAsync and RunAsync complete and a new server binds
        // the drained endpoint immediately.
        [Test]
        public Task DrainedListenerRestartsImmediately() => Restart(async (certificate, token) =>
            {
                var prefix = Prefixes(1)[0];

                for (var cycle = 0; cycle < 16; ++cycle)
                {
                    token.ThrowIfCancellationRequested();
                    using var server = Server(certificate, prefix, "drained");
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var running = server.RunAsync(stop.Token);

                    try
                    {
                        using (var client = Client(certificate))
                            Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("drained"));

                        await server
                            .DrainAsync(TimeSpan.FromSeconds(15), stop.Token)
                            .WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
                        await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                        Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                    }

                    finally
                    {
                        stop.Cancel();
                        await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    }
                }
            });

        // Four servers on distinct endpoints restart concurrently while each serves a
        // request per cycle, so one endpoint's release overlaps the binds, handshakes
        // and closes of the others. Odd cycles keep the client connection open until
        // the server has stopped, so the server ends the accepted connection itself.
        [Test]
        public Task ContendedRestartsComplete() => Restart(async (certificate, token) =>
            {
                await Task.WhenAll(
                    Prefixes(4).Select(
                        (prefix, lane) => Task.Run(
                            async () =>
                            {
                                var body = "lane" + lane.ToString(System.Globalization.CultureInfo.InvariantCulture);

                                for (var cycle = 0; cycle < 16; ++cycle)
                                {
                                    token.ThrowIfCancellationRequested();
                                    using var server = Server(certificate, prefix, body);
                                    await ServeOnceAsync(server, certificate, prefix, body, cycle % 2 == 1, token);
                                }
                            },
                            token
                        )
                    )
                );
            });

        private static async Task Restart(Func<X509Certificate2, CancellationToken, Task> body)
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("The host does not provide a QUIC client.");
                return;
            }

            if (EngineInternals.Find("EmbedIO.Net.Internal.Http3.Http3Listener") == null)
            {
                Assert.Ignore("This build of the library has no HTTP/3 listener.");
                return;
            }

            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await body(certificate, deadline.Token);
        }

        private static WebServer Server(X509Certificate2 certificate, string prefix, string body) =>
            new WebServer(o => o
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)).WithAction(
                "/",
                HttpVerbs.Get,
                context => context.SendStringAsync(body, "text/plain", WebServer.Utf8NoBomEncoding)
            );

        // Run the server, serve one request, then stop it. With keepClientOpen the
        // client connection stays open until the server has stopped.
        private static async Task ServeOnceAsync(
            WebServer server,
            X509Certificate2 certificate,
            string prefix,
            string expected,
            bool keepClientOpen,
            CancellationToken token
        )
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);

            try
            {
                Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo(expected));

                if (!keepClientOpen) client.Dispose();
            }

            finally
            {
                // A failed start faults the run task; its error replaces the request's.
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        }

        // Distinct loopback ports, held together while chosen so lanes cannot collide.
        private static string[] Prefixes(int count)
        {
            var sockets = new List<Socket>(count);

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sockets.Add(socket);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                }

                return
                [
                    .. sockets.Select(
                        socket =>
                            $"https://localhost:{((IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port}/"
                    )
                ];
            }

            finally
            {
                foreach (var socket in sockets) socket.Dispose();
            }
        }

        private static HttpClient Client(X509Certificate2 certificate) => new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                    == certificate.GetCertHashString()
            }
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
