using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [SetUp]
        public void EnforceRequiredQuicCoverage()
        {
            if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") != "1") return;
            Assert.That(QuicListener.IsSupported && QuicConnection.IsSupported, Is.True,
                "This CI job requires QUIC; missing native prerequisites must not become skipped coverage.");
            Assert.That(typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection"), Is.Not.Null,
                "Required transport coverage must use the modern asset.");
        }
        [TestCase(0, 1, false)]
        [TestCase(100003, 8, false)]
        [TestCase(1048576, 3, false)]
        [TestCase(0, 4, true)]
        public async Task HttpClientExchangesExactHttp3Bodies(int size, int concurrency, bool head)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The netstandard asset exposes framing but no direct QUIC transport."); return; }
            await Run(size, concurrency, head);
        }
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Run(int size, int concurrency, bool head)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            Assert.That(cert.Export(X509ContentType.Pkcs12).Length, Is.GreaterThan(0), "The OpenSSL QUIC backend must be able to export its synthetic TLS key.");
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 32,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = cert }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchange = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var connection = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var handler = (typeof(Http3QuicTest).GetMethod(nameof(Handle), Flags) ?? throw new AssertionException("Missing HTTP/3 test target.")).MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)));
            var server = Task.Run(async () =>
            {
                var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                await ((Task)(connection.GetMethod("RunAsync", Flags)?.Invoke(null, new object[] { accepted, handler, deadline.Token }) ?? throw new AssertionException("Missing HTTP/3 test target.")));
            });
            using var transport = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString() } };
            using var client = new HttpClient(transport);
            var uri = new Uri($"https://127.0.0.1:{listener.LocalEndPoint.Port}/echo");
            var calls = Enumerable.Range(0, concurrency).Select(async index =>
            {
                var payload = Encoding.UTF8.GetBytes(new string((char)('a' + index), size));
                using var message = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Post, uri) { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new ByteArrayContent(payload) };
                message.Headers.Host = "localhost"; // TLS server name stays DNS-based while routing to the explicit loopback address.
                using var response = await client.SendAsync(message, deadline.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(bytes, Is.EqualTo(payload));
            });
            try { await Task.WhenAll(calls); }
            finally { deadline.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        }
        [TestCase("blocked-trailer-reset", 0x10c, false)]
        [TestCase("blocked-fin", 0, false)]
        [TestCase("blocked-read-reset", 0x10c, false)]
        [TestCase("blocked-write-reset", 0x10c, false)]
        [TestCase("blocked-both-reset", 0x10c, false)]
        [TestCase("missing-method", 0x10e, false)]
        [TestCase("length-mismatch", 0x10e, false)]
        [TestCase("settings-on-request", 0x105, true)]
        [TestCase("closed-control", 0x104, true)]
        [TestCase("duplicate-control", 0x103, true)]
        public async Task WireErrorsUseTheCorrectScopeAndCode(string scenario, long code, bool connectionError)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The netstandard asset exposes framing but no direct QUIC transport."); return; }
            await Raw(scenario, code, connectionError);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Raw(string scenario, long code, bool connectionError)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            Assert.That(cert.Export(X509ContentType.Pkcs12).Length, Is.GreaterThan(0), "The OpenSSL QUIC backend must be able to export its synthetic TLS key.");
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 32,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = cert }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchange = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var connection = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var handler = (typeof(Http3QuicTest).GetMethod(nameof(Handle), Flags) ?? throw new AssertionException("Missing HTTP/3 test target.")).MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)));
            var sessionReady = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                await using var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                var session = Activator.CreateInstance(connection, Flags, null, new object[] { accepted, handler, deadline.Token }, null)
                    ?? throw new AssertionException("Missing connection owner.");
                using var owner = (IDisposable)session;
                sessionReady.SetResult(session);
                await ((Task)(connection.GetMethod("RunCoreAsync", Flags)?.Invoke(session, null) ?? throw new AssertionException("Missing connection runner.")));
            });
            await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString()
                }
            }, deadline.Token);
            await using var control = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                await control.WriteAsync(new byte[] { 0, 4, 0 }, scenario == "closed-control", deadline.Token);
                await using var request = await client.OpenOutboundStreamAsync(
                    scenario == "duplicate-control" ? QuicStreamType.Unidirectional : QuicStreamType.Bidirectional, deadline.Token);
                var blocked = scenario.StartsWith("blocked-", StringComparison.Ordinal);
                var wire = scenario switch
                {
                    "blocked-trailer-reset" => Convert.FromHexString("01100000D1D7C150096C6F63616C686F73740103020080"),
                    "missing-method" => Convert.FromHexString("010F0000D7C150096C6F63616C686F7374"),
                    "length-mismatch" => Convert.FromHexString("01110000D1D7C1C450096C6F63616C686F7374000161"),
                    "settings-on-request" => new byte[] { 4, 0 },
                    "duplicate-control" => new byte[] { 0, 4, 0 },
                    _ => blocked ? Convert.FromHexString("01110200D1D7C150096C6F63616C686F737480") : Array.Empty<byte>()
                };
                QuicException? observed = null;
                try
                {
                    if (wire.Length != 0) await request.WriteAsync(wire, scenario != "duplicate-control" && !blocked, deadline.Token);
                    if (blocked)
                    {
                        var session = await sessionReady.Task.WaitAsync(deadline.Token);
                        var gate = connection.GetField("_sync", Flags)?.GetValue(session) ?? throw new AssertionException("Missing gate.");
                        var pending = (System.Collections.IDictionary)(connection.GetField("_pending", Flags)?.GetValue(session) ?? throw new AssertionException("Missing pending sections."));
                        while (true)
                        {
                            lock (gate) { if (pending.Count == 1) break; }
                            await Task.Delay(1, deadline.Token);
                        }
                        if (scenario == "blocked-fin")
                        {
                            request.CompleteWrites();
                            var encoder = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                            peerStreams.Add(encoder);
                            await encoder.WriteAsync(Convert.FromHexString("023FE11F41780179"), deadline.Token);
                            using var completed = new MemoryStream();
                            await request.CopyToAsync(completed, deadline.Token);
                            Assert.That(completed.Length, Is.GreaterThan(2), "FIN must permit a blocked request to complete after inserts arrive.");
                        }
                        else
                            request.Abort(scenario == "blocked-read-reset" ? QuicAbortDirection.Read : scenario == "blocked-write-reset" ? QuicAbortDirection.Write : QuicAbortDirection.Both, code);
                        while (true)
                        {
                            var peer = await client.AcceptInboundStreamAsync(deadline.Token);
                            peerStreams.Add(peer);
                            var type = new byte[1]; await peer.ReadExactlyAsync(type, deadline.Token);
                            if (type[0] != 3) continue;
                            var cancellation = new byte[1];
                            await peer.ReadExactlyAsync(cancellation, deadline.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                            Assert.That(cancellation[0], Is.EqualTo(scenario == "blocked-fin" ? 128 : 64), "Stream zero must acknowledge on success or release QPACK references on reset.");
                            break;
                        }
                    }
                    if (connectionError)
                    {
                        while (true) peerStreams.Add(await client.AcceptInboundStreamAsync(deadline.Token));
                    }
                    else if (!blocked) await request.ReadExactlyAsync(new byte[1], deadline.Token);
                }
                catch (QuicException error) { observed = error; }
                if (!blocked)
                {
                    Assert.That(observed, Is.Not.Null);
                    Assert.That(observed?.ApplicationErrorCode, Is.EqualTo(code));
                    Assert.That(observed?.QuicError, Is.EqualTo(connectionError ? QuicError.ConnectionAborted : QuicError.StreamAborted));
                }
                if (!connectionError)
                {
                    await using var healthy = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                    await healthy.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                    using var response = new MemoryStream();
                    await healthy.CopyToAsync(response, deadline.Token);
                    Assert.That(response.Length, Is.GreaterThan(2), "A malformed request must not kill its sibling.");
                }
            }
            finally
            {
                deadline.Cancel();
                foreach (var peerStream in peerStreams) await peerStream.DisposeAsync();
                try { await server; }
                catch (OperationCanceledException) { }
                catch (IOException error) when (connectionError && error.GetType().Name == "Http3ProtocolException")
                { Assert.That(error.GetType().GetProperty("ErrorCode")?.GetValue(error), Is.EqualTo(code)); }
            }
        }
        static async Task Handle<T>(T value)
        {
            object exchange = value ?? throw new AssertionException("Missing HTTP/3 test target.");
            var type = exchange.GetType();
            var body = (Stream)(type.GetProperty("InputStream", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing HTTP/3 test target."));
            var token = (CancellationToken)(type.GetProperty("CancellationToken", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing HTTP/3 test target."));
            using var output = new MemoryStream(); await body.CopyToAsync(output);
            await ((Task)(type.GetMethod("RespondAsync", Flags)?.Invoke(exchange, new object[] { output.ToArray(), token }) ?? throw new AssertionException("Missing HTTP/3 test target.")));
        }
    }
}
