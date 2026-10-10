using System;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReservedHttp3TrailersFollowCoalescedContentLengthBody(bool coalesced)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The netstandard asset does not provide direct QUIC transport."); return; }
            await RunResponseTrailers(coalesced);
        }

        [Test]
        public async Task AdapterHttp3TrailersCompleteAfterACoalescedBody()
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The netstandard asset does not provide direct QUIC transport."); return; }
            await RunResponseTrailers(true, true);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task RunResponseTrailers(bool coalesced, bool adapter = false)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var certificate = HttpsSmoke.CreateCertificate();
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
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = certificate }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchangeType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true)
                ?? throw new AssertionException("Missing exchange.");
            var connectionType = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true)
                ?? throw new AssertionException("Missing connection.");
            var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)
                ?? throw new AssertionException("Missing field type.");
            Array Fields(params string[] pairs)
            {
                var fields = Array.CreateInstance(fieldType, pairs.Length / 2);
                for (var i = 0; i < pairs.Length; i += 2)
                    fields.SetValue(Activator.CreateInstance(fieldType, Flags, null,
                        new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
                return fields;
            }
            Task Call(object exchange, string method, params object[] arguments)
                => (Task)((exchangeType.GetMethod(method, Flags) ?? throw new AssertionException("Missing writer."))
                    .Invoke(exchange, arguments) ?? throw new AssertionException("Missing task."));
            Func<object, Task> application = async exchange =>
            {
                if (adapter)
                {
                    var contextType = assembly.GetType("EmbedIO.Net.Internal.MultiplexedContext", true)
                        ?? throw new AssertionException("Missing adapter context.");
                    var context = (IHttpContextImpl)(Activator.CreateInstance(contextType, Flags, null,
                        new object[] { exchange, new IPEndPoint(IPAddress.Loopback, 80), new IPEndPoint(IPAddress.Loopback, 12345), true }, null)
                        ?? throw new AssertionException("Missing context."));
                    try
                    {
                        context.Response.ContentLength64 = 3;
                        var interim = context.Response.GetType().GetMethod("SendInformationalAsync", Flags)
                            ?? throw new AssertionException("Missing interim adapter writer.");
                        await (Task)(interim.Invoke(context.Response, new object[]
                        { 103, new WebHeaderCollection { ["Link"] = "</asset>; rel=preload" }, context.CancellationToken })
                            ?? throw new AssertionException("Missing interim task."));
                        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
                        (context.Response.GetType().GetMethod("PrepareTrailers", Flags) ?? throw new AssertionException("Missing declaration."))
                            .Invoke(context.Response, new object[] { new[] { "x-verified" } });
                        await context.Response.OutputStream.WriteAsync(new byte[] { 7, 8, 9 }, context.CancellationToken);
                        var trailers = new WebHeaderCollection { ["x-verified"] = "yes" };
                        (context.Response.GetType().GetMethod("SetTrailers", Flags) ?? throw new AssertionException("Missing snapshot."))
                            .Invoke(context.Response, new object[] { trailers });
                        trailers["x-verified"] = "changed-after-set";
                    }
                    finally { context.Close(); }
                    return;
                }
                (exchangeType.GetMethod("ExpectTrailers", Flags) ?? throw new AssertionException("Missing reservation."))
                    .Invoke(exchange, null);
                var token = (CancellationToken)(exchangeType.GetProperty("CancellationToken")?.GetValue(exchange)
                    ?? throw new AssertionException("Missing cancellation."));
                var headers = Fields(":status", "200", "content-length", "3");
                var body = new byte[] { 7, 8, 9 };
                if (coalesced) await Call(exchange, "SendHeadersAndWriteAsync", headers, body, 0, 3, token);
                else
                {
                    await Call(exchange, "SendHeadersAsync", headers, false, token);
                    await Call(exchange, "WriteAsync", body, 0, 3, false, token);
                }
                await Assert.ThatAsync(async () => await Call(exchange, "SendTrailersAsync", Fields("te", "trailers"), token),
                    Throws.InstanceOf<InvalidDataException>());
                await Call(exchange, "SendTrailersAsync", Fields("x-verified", "yes"), token);
            };
            var parameter = Expression.Parameter(exchangeType);
            var handler = Expression.Lambda(typeof(Func<,>).MakeGenericType(exchangeType, typeof(Task)),
                Expression.Invoke(Expression.Constant(application), Expression.Convert(parameter, typeof(object))), parameter).Compile();
            var server = Task.Run(async () =>
            {
                var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                await (Task)((connectionType.GetMethod("RunAsync", Flags) ?? throw new AssertionException("Missing runner."))
                    .Invoke(null, new object[] { accepted, handler, deadline.Token }) ?? throw new AssertionException("Missing server task."));
            });
            using var transport = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() }
            };
            using var client = new HttpClient(transport);
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{listener.LocalEndPoint.Port}/trailers")
                    { Version = HttpVersion.Version30, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    request.Headers.Host = "localhost";
                    using var response = await client.SendAsync(request, deadline.Token);
                    Assert.That(await response.Content.ReadAsByteArrayAsync(deadline.Token), Is.EqualTo(new byte[] { 7, 8, 9 }));
                    Assert.That(response.TrailingHeaders.GetValues("x-verified"), Is.EqualTo(new[] { "yes" }));
                    Assert.That(response.Headers.Contains("x-verified"), Is.False);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Headers.Contains("link"), Is.False);
                }
            }
            finally
            {
                deadline.Cancel();
                try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
