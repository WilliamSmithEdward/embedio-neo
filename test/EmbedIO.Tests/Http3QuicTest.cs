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
    public sealed class Http3QuicTest
    {
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
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
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
                using var response = await client.SendAsync(message, deadline.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(bytes, Is.EqualTo(payload));
            });
            try { await Task.WhenAll(calls); }
            finally { deadline.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        }
        [TestCase("missing-method", 0x10e, false)]
        [TestCase("length-mismatch", 0x10e, false)]
        [TestCase("settings-on-request", 0x105, true)]
        [TestCase("closed-control", 0x104, true)]
        [TestCase("duplicate-control", 0x103, true)]
        public async Task WireErrorsUseTheCorrectScopeAndCode(string scenario, long code, bool connectionError)
        {
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
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
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
                var wire = scenario switch
                {
                    "missing-method" => Convert.FromHexString("010F0000D7C150096C6F63616C686F7374"),
                    "length-mismatch" => Convert.FromHexString("01110000D1D7C1C450096C6F63616C686F7374000161"),
                    "settings-on-request" => new byte[] { 4, 0 },
                    "duplicate-control" => new byte[] { 0, 4, 0 },
                    _ => Array.Empty<byte>()
                };
                QuicException? observed = null;
                try
                {
                    if (wire.Length != 0) await request.WriteAsync(wire, scenario != "duplicate-control", deadline.Token);
                    if (connectionError)
                    {
                        while (true) peerStreams.Add(await client.AcceptInboundStreamAsync(deadline.Token));
                    }
                    else await request.ReadExactlyAsync(new byte[1], deadline.Token);
                }
                catch (QuicException error) { observed = error; }
                Assert.That(observed, Is.Not.Null);
                Assert.That(observed?.ApplicationErrorCode, Is.EqualTo(code));
                Assert.That(observed?.QuicError, Is.EqualTo(connectionError ? QuicError.ConnectionAborted : QuicError.StreamAborted));
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
            using var output = new MemoryStream(); await body.CopyToAsync(output, token);
            await ((Task)(type.GetMethod("RespondAsync", Flags)?.Invoke(exchange, new object[] { output.ToArray(), token }) ?? throw new AssertionException("Missing HTTP/3 test target.")));
        }
    }
}

