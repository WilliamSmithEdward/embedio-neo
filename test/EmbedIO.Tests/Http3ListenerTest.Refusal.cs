using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // An HTTP/3 endpoint that is full or overloaded refuses a new connection with an Initial
    // carrying CONNECTION_CLOSE(CONNECTION_REFUSED) (RFC 9000 5.2.2). The .NET client, on
    // MsQuic, must read that packet and fail at once rather than time out (#521).
    public partial class Http3ListenerTest
    {
        private delegate byte[]? RefusalFactory(ReadOnlySpan<byte> initial, ulong error);

        // The engine's packet builder; the dispatcher checks that send it run in the QUIC development host.
        private static RefusalFactory Refusal() =>
            (typeof(WebServer).Assembly.GetType("EmbedIO.Quic.ConnectionRefusal", throwOnError: true)
                    ?.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("ConnectionRefusal.Create"))
            .CreateDelegate<RefusalFactory>();

        [Test]
        public async Task TheDotNetClientReadsAConnectionRefusal()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            var refusal = Refusal();
            using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var endpoint = (IPEndPoint)(udp.LocalEndPoint ?? throw new AssertionException("UDP socket has no endpoint."));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var refused = 0;
            var answering = Task.Run(async () =>
            {
                var buffer = new byte[65535];

                while (true)
                {
                    SocketReceiveFromResult received;

                    try
                    {
                        received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
                    }

                    // Windows reports an ICMP port-unreachable for an earlier reply here.
                    catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        continue;
                    }

                    var reply = refusal(buffer.AsSpan(0, received.ReceivedBytes), 0x02);

                    if (reply == null) continue;

                    Interlocked.Increment(ref refused);
                    await udp.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, stop.Token);
                }
            });
            var clock = Stopwatch.StartNew();
            QuicError? error = null;

            try
            {
                await using var connection = await QuicConnection.ConnectAsync(RefusedClient(endpoint), stop.Token);
            }

            catch (QuicException failure)
            {
                error = failure.QuicError;
            }

            finally
            {
                stop.Cancel();

                try
                {
                    await answering;
                }

                catch (OperationCanceledException) { }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(refused, Is.GreaterThanOrEqualTo(1));
                Assert.That(error, Is.EqualTo(QuicError.ConnectionRefused));
                // Without the refusal the client retransmits until its handshake timeout.
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            }
        }

        // Once established and handshaking connections fill the HTTP/3 listener's 256 slots,
        // the next connection is refused before its handshake. The established connections
        // carry on, and the slot a closed connection frees admits a new one.
        [Test]
        public async Task TheListenerRefusesNewConnectionsWhileItsSlotsAreFull()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            await FullListenerWire();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task FullListenerWire()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var endpoint = new IPEndPoint(IPAddress.Loopback, new Uri(prefix).Port);
            using var server = new WebServer(o => o
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)).WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var running = server.RunAsync(stop.Token);
            var held = new List<(QuicConnection Connection, QuicStream Control)>();

            try
            {
                for (var batch = 0; batch < 8; batch++)
                {
                    var opening = new Task<(QuicConnection Connection, QuicStream Control)>[32];

                    for (var index = 0; index < opening.Length; index++) opening[index] = Hold(endpoint, certificate, stop.Token);

                    held.AddRange(await Task.WhenAll(opening));
                }

                var clock = Stopwatch.StartNew();
                QuicError? error = null;

                try
                {
                    await using var refused = await QuicConnection.ConnectAsync(FullListenerClient(endpoint, certificate), stop.Token);
                }

                catch (QuicException failure)
                {
                    error = failure.QuicError;
                }

                var refusedIn = clock.Elapsed;
                await HealthySibling(held[0].Connection, $"localhost:{endpoint.Port}", stop.Token);

                // Closing a connection frees its slot once the server has retired it.
                var (closing, closingControl) = held[^1];
                held.RemoveAt(held.Count - 1);
                await closingControl.DisposeAsync();
                await closing.CloseAsync(0x100, stop.Token);
                await closing.DisposeAsync();
                clock.Restart();

                while (true)
                {
                    try
                    {
                        held.Add(await Hold(endpoint, certificate, stop.Token));
                        break;
                    }

                    catch (QuicException failure) when (failure.QuicError == QuicError.ConnectionRefused && clock.Elapsed < TimeSpan.FromSeconds(20))
                    {
                        await Task.Delay(100, stop.Token);
                    }
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(error, Is.EqualTo(QuicError.ConnectionRefused));
                    // Without the refusal the client retransmits until its handshake timeout.
                    Assert.That(refusedIn, Is.LessThan(TimeSpan.FromSeconds(5)));
                    Assert.That(server.Listener.IsListening, Is.True);
                }
            }

            finally
            {
                foreach (var (connection, control) in held)
                {
                    await control.DisposeAsync();
                    await connection.DisposeAsync();
                }

                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        // A connection with its HTTP/3 control stream open and SETTINGS sent, as a client
        // keeps one between requests.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<(QuicConnection Connection, QuicStream Control)> Hold(IPEndPoint endpoint, X509Certificate2 certificate, CancellationToken token)
        {
            var connection = await QuicConnection.ConnectAsync(FullListenerClient(endpoint, certificate), token);

            try
            {
                var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
                await control.WriteAsync(new byte[] { 0, 4, 0 }, token);
                return (connection, control);
            }

            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static QuicClientConnectionOptions FullListenerClient(IPEndPoint endpoint, X509Certificate2 certificate) => new()
        {
            RemoteEndPoint = endpoint,
            DefaultCloseErrorCode = 0x100,
            DefaultStreamErrorCode = 0x10c,
            MaxInboundUnidirectionalStreams = 8,
            MaxInboundBidirectionalStreams = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = [new SslApplicationProtocol("h3")],
                RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString()
            }
        };

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static QuicClientConnectionOptions RefusedClient(IPEndPoint endpoint) => new()
        {
            RemoteEndPoint = endpoint,
            DefaultStreamErrorCode = 0x10c,
            DefaultCloseErrorCode = 0x100,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [new SslApplicationProtocol("h3")],
                TargetHost = "localhost"
            }
        };
    }
}
