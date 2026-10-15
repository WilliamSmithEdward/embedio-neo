using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        public static IEnumerable<TestCaseData> UnacceptedHttp3Cases()
        {
            foreach (var protocol in new[] { "example-tunnel", "websocket" })
                foreach (var commitment in Enum.GetValues<ExtendedConnectAcceptanceTest.Commitment>())
                    yield return new TestCaseData(protocol, commitment);
        }

        [TestCaseSource(nameof(UnacceptedHttp3Cases))]
        public async Task Http3SuccessfulOutputRequiresExplicitAcceptance(string protocol,
            ExtendedConnectAcceptanceTest.Commitment commitment)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await VerifyExtendedAcceptanceAsync(protocol, 501, context =>
                ExtendedConnectAcceptanceTest.WriteUnacceptedAsync(context, commitment), Array.Empty<byte>());
        }

        [TestCase(400)]
        [TestCase(401)]
        [TestCase(403)]
        [TestCase(404)]
        [TestCase(501)]
        public async Task Http3ExplicitApplicationRejectionPreservesStatusAndBody(int status)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await VerifyExtendedAcceptanceAsync("example-tunnel", status, async context =>
            {
                context.Response.StatusCode = status;
                await context.Response.OutputStream.WriteAsync("application-rejection"u8.ToArray(), context.CancellationToken);
            }, "application-rejection"u8.ToArray());
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task VerifyExtendedAcceptanceAsync(string protocol, int status,
            Func<IHttpContext, Task> handler, byte[] expectedBody)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var calls = 0;
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/healthy")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }
                    Interlocked.Increment(ref calls);
                    context.OnClose(_ => closed.TrySetResult());
                    await handler(context);
                });
            var running = server.RunAsync(stop.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = RequestCancelled,
                    MaxInboundUnidirectionalStreams = 8,
                    MaxInboundBidirectionalStreams = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                        RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                            && peer.GetCertHashString() == certificate.GetCertHashString()
                    }
                }, stop.Token);
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                await ReadConnectSetting(connection, peerStreams, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                var fields = new List<(string, string)>
                { (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/carrier"), (":protocol", protocol) };
                if (protocol == "websocket") fields.Add(("sec-websocket-version", "13"));
                // Leave peer input open: rejection must end only this response stream.
                await SendFieldSection(request, fields.ToArray(), false, stop.Token);
                var headers = await ReadResponseFields(request, stop.Token);
                Assert.That(headers[":status"], Is.EqualTo(status.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                using var data = new ClientDataStream(request);
                using var body = new MemoryStream();
                await data.CopyToAsync(body, stop.Token);
                Assert.That(body.ToArray(), Is.EqualTo(expectedBody));
                await closed.Task.WaitAsync(stop.Token);
                await HealthySibling(connection, uri.Authority, stop.Token);
                Assert.That(calls, Is.EqualTo(1), "The application must decide the extension exactly once.");
            }
            finally
            {
                foreach (var peer in peerStreams) await peer.DisposeAsync();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
