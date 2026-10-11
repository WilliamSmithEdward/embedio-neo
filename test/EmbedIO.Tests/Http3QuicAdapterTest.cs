using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [TestCase("metadata")]
        [TestCase("fixed-body")]
        [TestCase("streaming-body")]
        [TestCase("empty-body")]
        [TestCase("head")]
        [TestCase("204")]
        [TestCase("205")]
        [TestCase("304")]
        [TestCase("close")]
        [TestCase("declared-length")]
        [TestCase("declared-split")]
        public async Task ApplicationAdapterPreservesHttp3Semantics(string scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await RunAdapter(scenario);
        }
        private sealed class AdaptedApplication
        {
            internal required Func<IHttpContextImpl, Task> Application;
            internal required IPEndPoint Local;
            internal required IPEndPoint Remote;
            internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal async Task Handle<T>(T exchange)
            {
                try
                {
                    var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.MultiplexedContext", true)
                        ?? throw new AssertionException("Missing application context.");
                    var context = (IHttpContextImpl)(Activator.CreateInstance(type, Flags, null,
                        new object[] { exchange ?? throw new AssertionException("Missing exchange."), Local, Remote, true }, null)
                        ?? throw new AssertionException("Missing context constructor."));
                    try { await Application(context); }
                    finally { context.Close(); }
                    await (Task)(type.GetProperty("Completion", Flags)?.GetValue(context) ?? throw new AssertionException("Missing completion."));
                    // Server assignment racing with completion must not revive a closed request.
                    context.CancellationToken = CancellationToken.None;
                    Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
                    Completed.TrySetResult();
                }
                catch (Exception error) { Completed.TrySetException(error); throw; }
            }
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task RunAdapter(string scenario)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
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
            var callbacks = new List<int>();
            var payload = Encoding.UTF8.GetBytes("Hello, 世界");
            var body = scenario is "fixed-body" or "streaming-body";
            var status = int.TryParse(scenario, out var parsed) ? parsed : 200;
            var application = new AdaptedApplication
            {
                Local = listener.LocalEndPoint,
                Remote = new IPEndPoint(IPAddress.Loopback, 0),
                Application = async context =>
                {
                    Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
                    Assert.That(context.Response.ProtocolVersion, Is.EqualTo(HttpVersion.Version30));
                    Assert.That(context.Request.IsSecureConnection && context.Request.IsLocal, Is.True);
                    Assert.That(context.Request.LocalEndPoint, Is.EqualTo(listener.LocalEndPoint));
                    Assert.That(context.Request.RemoteEndPoint.Port, Is.GreaterThan(0));
                    Assert.That(context.Request.RawTarget, Is.EqualTo("/echo?x=a+b&x=c"));
                    Assert.That(context.Request.QueryString.GetValues("x"), Is.EqualTo(new[] { "a b", "c" }));
                    Assert.That(context.Request.Cookies["first"]?.Value, Is.EqualTo("1"));
                    Assert.That(context.Request.Headers["X-Application"], Is.EqualTo("test"));
                    context.CancellationToken = deadline.Token;
                    context.OnClose(_ => callbacks.Add(1)); context.OnClose(_ => callbacks.Add(2));
                    Assert.That(context.Request.HasEntityBody, Is.EqualTo(body));
                    Assert.That(context.Request.ContentLength64, Is.EqualTo(scenario == "streaming-body" ? -1 : body ? payload.Length : 0));
                    using var received = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(received, context.CancellationToken);
                    Assert.That(received.ToArray(), Is.EqualTo(body ? payload : Array.Empty<byte>()));
                    context.Response.StatusCode = status;
                    context.Response.KeepAlive = scenario != "close";
                    context.Response.Headers["Server"] = "application-engine";
                    context.Response.Headers["Connection"] = "close";
                    context.Response.Headers["Transfer-Encoding"] = "chunked";
                    context.Response.SetCookie(new Cookie("one", "1", "/"));
                    context.Response.SetCookie(new Cookie("two", "2", "/"));
                    if (scenario is "head" or "declared-length" or "declared-split") context.Response.ContentLength64 = payload.Length;
                    // A split write covers a declared length across the coalesced first write and a later one.
                    if (scenario == "declared-split")
                    {
                        await context.Response.OutputStream.WriteAsync(payload.AsMemory(0, 3), context.CancellationToken);
                        await context.Response.OutputStream.WriteAsync(payload.AsMemory(3), context.CancellationToken);
                    }
                    else if (scenario != "empty-body") await context.Response.OutputStream.WriteAsync(payload, context.CancellationToken);
                    context.SetHandled();
                    Assert.That(context.IsHandled, Is.True);
                    context.Response.OutputStream.Dispose();
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                }
            };
            var assembly = typeof(WebServer).Assembly;
            var exchangeType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing exchange.");
            var connectionType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing connection.");
            var handler = (typeof(AdaptedApplication).GetMethod("Handle", Flags) ?? throw new AssertionException("Missing handler."))
                .MakeGenericMethod(exchangeType).CreateDelegate(typeof(Func<,>).MakeGenericType(exchangeType, typeof(Task)), application);
            var server = Task.Run(async () =>
            {
                var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                application.Remote = accepted.RemoteEndPoint;
                await (Task)(connectionType.GetMethod("RunAsync", Flags)?.Invoke(null, new object[] { accepted, handler, deadline.Token }) ?? throw new AssertionException("Missing runner."));
            });
            using var transport = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString() } };
            using var client = new HttpClient(transport);
            using var contentStream = new NonSeekableBody(body ? payload : Array.Empty<byte>());
            using var message = new HttpRequestMessage(scenario == "head" ? HttpMethod.Head : HttpMethod.Post,
                new Uri($"https://127.0.0.1:{listener.LocalEndPoint.Port}/echo?x=a+b&x=c"))
            {
                Version = HttpVersion.Version30,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = scenario == "streaming-body" ? new StreamContent(contentStream) : new ByteArrayContent(body ? payload : Array.Empty<byte>())
            };
            message.Headers.Host = "localhost";
            message.Headers.TryAddWithoutValidation("Cookie", "first=1");
            message.Headers.TryAddWithoutValidation("X-Application", "test");
            try
            {
                using var response = await client.SendAsync(message, deadline.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(await response.Content.ReadAsByteArrayAsync(deadline.Token),
                    Is.EqualTo(scenario is "head" or "empty-body" || status != 200 ? Array.Empty<byte>() : payload));
                Assert.That(response.Headers.Server.ToString(), Is.EqualTo("application-engine"));
                Assert.That(response.Headers.GetValues("Set-Cookie").ToArray(), Has.Length.EqualTo(2));
                Assert.That(response.Headers.Contains("Connection"), Is.False);
                Assert.That(response.Headers.Contains("Transfer-Encoding"), Is.False);
                if (scenario is "head" or "declared-length" or "declared-split") Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(payload.Length));
                if (scenario == "empty-body") Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(0));
                await application.Completed.Task.WaitAsync(deadline.Token);
                Assert.That(callbacks, Is.EqualTo(new[] { 2, 1 }));
                if (scenario == "close") await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { deadline.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        }
        private sealed class NonSeekableBody : MemoryStream
        {
            internal NonSeekableBody(byte[] bytes) : base(bytes, false) { }
            public override bool CanSeek => false;
        }
    }
}
