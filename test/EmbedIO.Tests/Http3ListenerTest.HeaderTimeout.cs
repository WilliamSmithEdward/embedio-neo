using System;
using System.Collections.Generic;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // RequestHeadersTimeout bounds each HTTP/3 stream's header, as Kestrel's does: a stream the
    // client opens and leaves short of its header is reset once the deadline passes, while its
    // siblings and the connection's own critical streams carry on.
    public partial class Http3ListenerTest
    {
        private static readonly TimeSpan ShortHeaderTimeout = TimeSpan.FromSeconds(1);

        // A HEADERS frame announcing ten payload bytes and sending two; the first byte of a
        // two-byte integer; the WebTransport signal followed by the first byte of a session.
        private static byte[] PartialHeader(string shape) =>
            shape switch
            {
                "request-headers" => [0x01, 0x0a, 0x00, 0x00],
                "first-integer" => [0x40],
                "session" => [0x40, 0x41, 0x40],
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };

        [TestCase(false, "request-headers")]
        [TestCase(false, "first-integer")]
        [TestCase(true, "request-headers")]
        [TestCase(true, "first-integer")]
        [TestCase(true, "session")]
        public async Task BidirectionalStreamsMissingTheirHeaderAreRejected(bool webTransport, string shape)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = HeaderTimeoutServer(prefix, certificate, ShortHeaderTimeout, webTransport);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);
                await using var stalled = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                await stalled.WriteAsync(PartialHeader(shape), stop.Token);

                // H3_REQUEST_REJECTED: the request was not processed.
                Assert.That(await ObserveRequestAsync(stalled, stop.Token), Is.EqualTo(Reset(0x10b)));
                await HealthySibling(connection, uri.Authority, stop.Token);
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        [TestCase(false, "first-integer")]
        [TestCase(true, "first-integer")]
        [TestCase(true, "session")]
        public async Task UnidirectionalStreamsMissingTheirHeaderAreRefused(bool webTransport, string shape)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = HeaderTimeoutServer(prefix, certificate, ShortHeaderTimeout, webTransport);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);
                await using var stalled = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);

                // A unidirectional stream's header is its type, then for WebTransport (0x54) its session.
                byte[] header = shape == "session" ? [0x40, 0x54, 0x40] : PartialHeader(shape);
                await stalled.WriteAsync(header, stop.Token);

                // H3_STREAM_CREATION_ERROR, sent as STOP_SENDING.
                Assert.That(await ObserveStopSendingAsync(stalled, stop.Token), Is.EqualTo(Reset(0x103)));
                await HealthySibling(connection, uri.Authority, stop.Token);
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        // Headers completed before the deadline are served, and the deadline never reaches the
        // connection's control and QPACK streams, which stay open long after their stream type.
        [Test]
        public async Task HeadersInsideTheDeadlineAndIdleConnectionsAreServed()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = HeaderTimeoutServer(prefix, certificate, TimeSpan.FromSeconds(3), false);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);

                await using (var slow = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token))
                {
                    // The first byte makes the server accept the stream and start its deadline.
                    var fields = HeaderFrame(uri.Authority);
                    await slow.WriteAsync(fields.AsMemory(0, 1), stop.Token);
                    await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    await slow.WriteAsync(fields.AsMemory(1), true, stop.Token);
                    Assert.That(await ObserveRequestAsync(slow, stop.Token), Is.EqualTo("200 healthy"));
                }

                // Idle past the deadline and the heartbeat that enforces it.
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                await HealthySibling(connection, uri.Authority, stop.Token);
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        // RequestHeadersTimeout applies to HTTP/3, so an HTTP/3-only server accepts it; the other
        // connection limits still need a TCP listener.
        [Test]
        public void Http3OnlyServersAcceptRequestHeadersTimeout()
        {
            var prefix = Prefix();
            using var certificate = Certificate();

            using (new WebServer(
                       new WebServerOptions()
                           .WithUrlPrefix(prefix)
                           .WithMode(HttpListenerMode.EmbedIOHttp3)
                           .WithCertificate(certificate)
                           .WithConnectionLimits(null, TimeSpan.FromSeconds(5))
                   )) { }

            var keepAlive = new WebServerOptions()
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)
                .WithConnectionLimits(null, keepAliveTimeout: TimeSpan.FromSeconds(5));
            Assert.That(() => new WebServer(keepAlive), Throws.InstanceOf<NotSupportedException>());
        }

        private static WebServer HeaderTimeoutServer(
            string prefix,
            X509Certificate2 certificate,
            TimeSpan timeout,
            bool webTransport
        ) => new WebServer(options =>
            {
                options
                    .WithUrlPrefix(prefix)
                    .WithMode(HttpListenerMode.EmbedIOHttp3)
                    .WithCertificate(certificate)
                    .WithConnectionLimits(null, timeout);

                if (webTransport) options.WithWebTransport(_ => { });
            }).WithAction(
                "/",
                HttpVerbs.Any,
                context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)
            );

        // A complete GET request's HEADERS frame, with literal fields.
        private static byte[] HeaderFrame(string authority)
        {
            var section = LiteralFields([(":method", "GET"), (":scheme", "https"), (":authority", authority), (":path", "/healthy")]);
            return [.. Integers(0x01, section.Length), .. section];
        }

        // How the server ended a unidirectional stream it reads: STOP_SENDING with a code.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<string> ObserveStopSendingAsync(QuicStream stream, CancellationToken token)
        {
            try
            {
                await stream.WritesClosed.WaitAsync(token);
                return "closed";
            }

            catch (QuicException error) when (error.QuicError == QuicError.StreamAborted)
            {
                return Reset(error.ApplicationErrorCode ?? -1);
            }
        }
    }
}
