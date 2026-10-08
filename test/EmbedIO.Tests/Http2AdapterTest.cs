using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private static IHttpContextImpl Adapter(object exchange)
            => (IHttpContextImpl)(Activator.CreateInstance((typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.MultiplexedContext", true) ?? throw new AssertionException("Missing application context.")), Flags, null,
                new object[] { exchange, new IPEndPoint(IPAddress.Loopback, 80), new IPEndPoint(IPAddress.Loopback, 12345), false }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));

        private static async Task WithAdapter(Func<IHttpContextImpl, Task> application, Func<HttpClient, Task> verify)
        {
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                try { await application(context); }
                finally { context.Close(); }
                await (Task)((context.GetType().GetProperty("Completion", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(context) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            }, verify);
        }

        [Test]
        public async Task CompletedOutputAllowsFinalServerFlush()
        {
            await WithAdapter(async context =>
            {
                await context.SendStringAsync("finished", "text/plain", WebServer.Utf8NoBomEncoding);
                context.Response.OutputStream.Dispose();
                await context.Response.OutputStream.FlushAsync();
                context.Response.OutputStream.Flush();
            }, async client => Assert.That(await client.GetStringAsync("flush"), Is.EqualTo("finished")));
        }

        [Test]
        public async Task ClosedContextAcceptsLateServerTokenWithoutRevivingRequest()
        {
            var delivered = new TaskCompletionSource<IHttpContextImpl>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithAdapter(context =>
            {
                context.Close();
                delivered.TrySetResult(context);
                return Task.CompletedTask;
            }, async client =>
            {
                using var response = await client.GetAsync("handoff");
                var context = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var serverStop = new CancellationTokenSource();
                Assert.DoesNotThrow(() => context.CancellationToken = serverStop.Token);
                Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
                Assert.DoesNotThrow(() => context.CancellationToken = CancellationToken.None);
                Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
                using var healthy = await client.GetAsync("healthy");
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
        }

        [Test]
        public async Task PublicContextStringResponsePreservesQueryCookiesAndEncoding()
        {
            await WithAdapter(async context =>
            {
                Assert.That(context.Request.ProtocolVersion, Is.EqualTo(HttpVersion.Version20));
                Assert.That(context.Request.QueryString.GetValues("x"), Is.EqualTo(new[] { "a b", "c" }));
                Assert.That((context.Request.Cookies["first"] ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Value, Is.EqualTo("1"));
                Assert.That((context.Request.Cookies["second"] ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Value, Is.EqualTo("2"));
                Assert.That(context.Request.IsLocal, Is.True);
                Assert.That(context.Request.IsSecureConnection, Is.False);
                Assert.That(context.Request.RawTarget, Is.EqualTo("/hello?x=a+b&x=c"));
                context.Response.Headers["Server"] = "custom-engine";
                await context.SendStringAsync("Hello, 世界", "text/plain", new UTF8Encoding(false));
            }, async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "hello?x=a+b&x=c") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.TryAddWithoutValidation("Cookie", "first=1; second=2");
                using var response = await client.SendAsync(request);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Hello, 世界"));
                Assert.That((response.Content.Headers.ContentType ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).CharSet, Is.EqualTo("utf-8"));
                Assert.That(response.Headers.Server.ToString(), Is.EqualTo("custom-engine"));
                Assert.That(response.Headers.Date, Is.Not.Null);
            });
        }

        [Test]
        public async Task PublicResponsePreservesSeparateSetCookieValues()
        {
            await WithAdapter(context =>
            {
                context.Response.SetCookie(new Cookie("one", "1", "/") { HttpOnly = true });
                context.Response.SetCookie(new Cookie("two", "2", "/") { Secure = true });
                return Task.CompletedTask;
            }, async client =>
            {
                using var response = await client.GetAsync("cookies");
                var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
                Assert.That(cookies.Length, Is.EqualTo(2));
                Assert.That(cookies.Any(value => value.StartsWith("one=1", StringComparison.Ordinal) && value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)), Is.True);
                Assert.That(cookies.Any(value => value.StartsWith("two=2", StringComparison.Ordinal) && value.Contains("Secure", StringComparison.OrdinalIgnoreCase)), Is.True);
            });
        }

        [Test]
        public async Task PublicHeadWritesPreserveMetadataWithoutWireBody()
        {
            await WithAdapter(async context =>
            {
                context.Response.ContentLength64 = 123;
                await context.Response.OutputStream.WriteAsync(new byte[123]);
            }, async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "head") { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request);
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(123));
                Assert.That((await response.Content.ReadAsByteArrayAsync()).Length, Is.Zero);
            });
        }

        [Test]
        public async Task PublicNoContentResponseSuppressesWritesAndLength()
        {
            await WithAdapter(async context =>
            {
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 999;
                await context.Response.OutputStream.WriteAsync(new byte[3]);
            }, async client =>
            {
                using var response = await client.GetAsync("no-content");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That((await response.Content.ReadAsByteArrayAsync()).Length, Is.Zero);
            });
        }

        [Test]
        public async Task ContextCloseCallbacksRunOnceInReverseRegistrationOrder()
        {
            var callbacks = new List<int>();
            await WithAdapter(async context =>
            {
                context.OnClose(_ => callbacks.Add(1)); context.OnClose(_ => callbacks.Add(2));
                context.Items["value"] = 42; context.SetHandled();
                await Task.WhenAll(Task.Run(context.Close), Task.Run(context.Close));
                Assert.That(context.IsHandled, Is.True);
                Assert.That(context.Items["value"], Is.EqualTo(42));
            }, async client => { using var response = await client.GetAsync("close"); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); });
            Assert.That(callbacks, Is.EqualTo(new[] { 2, 1 }));
        }

        [Test]
        public async Task ResponseCloseWaitsForAnActiveLargeWrite()
        {
            var bytes = new byte[262144]; new Random(7).NextBytes(bytes);
            await WithAdapter(async context =>
            {
                context.Response.ContentLength64 = bytes.Length;
                var write = context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                var close = Task.Run(context.Response.Close);
                await Task.WhenAll(write, close);
                context.Response.Close();
            }, async client => Assert.That(await client.GetByteArrayAsync("large"), Is.EqualTo(bytes)));
        }

        [Test]
        public async Task UnknownLengthUploadRetainsBodyAndCharset()
        {
            await WithAdapter(async context =>
            {
                Assert.That(context.Request.HasEntityBody, Is.True);
                Assert.That(context.Request.ContentLength64, Is.EqualTo(-1));
                Assert.That(context.Request.ContentEncoding.WebName, Is.EqualTo("iso-8859-1"));
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                context.Response.ContentLength64 = body.Length;
                await context.Response.OutputStream.WriteAsync(body.ToArray(), context.CancellationToken);
            }, async client =>
            {
                using var content = new UnknownLengthContent();
                using var response = await client.PostAsync("upload", content);
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 0xe9, 0x21 }));
            });
        }
        private sealed class UnknownLengthContent : HttpContent
        {
            internal UnknownLengthContent() { Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" }; }
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[] { 0xe9, 0x21 }, 0, 2);
        }

        [Test]
        public async Task KeepAliveFalseSendsGracefulGoawayAfterResponse()
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 5, 1, RequestBlock(), token);
                await Until(stream, 1, 1, token);
                var goaway = await Until(stream, 7, 0, token);
                Assert.That(goaway.Payload, Is.EqualTo(new byte[] { 0, 0, 0, 1, 0, 0, 0, 0 }));
                Assert.That(await stream.ReadAsync(new byte[1], token), Is.Zero);
            }, app: exchange =>
            {
                var context = Adapter(exchange);
                context.Response.KeepAlive = false;
                context.Close();
                return Task.CompletedTask;
            });
        }

        [Test]
        public async Task GracefulDrainCompletesExistingUploadAndRefusesNewStream()
        {
            await WithRawServer(Array.Empty<byte>(), async (stream, token) =>
            {
                await SendWire(stream, 1, 4, 1, RequestBlock(true, 1), token);
                await SendWire(stream, 1, 5, 3, RequestBlock(), token);
                await Until(stream, 1, 3, token);
                var goaway = await Until(stream, 7, 0, token);
                Assert.That(goaway.Payload, Is.EqualTo(new byte[] { 0, 0, 0, 3, 0, 0, 0, 0 }));
                await SendWire(stream, 1, 5, 5, RequestBlock(), token);
                Assert.That((await Until(stream, 3, 5, token)).Payload, Is.EqualTo(new byte[] { 0, 0, 0, 7 }));
                await SendWire(stream, 0, 1, 1, new byte[] { 42 }, token);
                Assert.That((await Until(stream, 0, 1, token)).Payload, Is.EqualTo(new byte[] { 42 }));
            }, app: async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    if (context.Request.HttpMethod == "GET") context.Response.KeepAlive = false;
                    else
                    {
                        using var body = new MemoryStream();
                        await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                        context.Response.ContentLength64 = body.Length;
                        await context.Response.OutputStream.WriteAsync(body.ToArray(), context.CancellationToken);
                    }
                }
                finally { context.Close(); }
            });
        }
    }
}
