using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Serialization;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Behavior checks for code that no test executed (coverage union of CI 38449577382).
    [TestFixture]
    public class CoverageGapTest
    {
        [Test]
        public async Task ThePurgeRemovesExpiredSessionsThatHoldData()
        {
            var manager = new LocalSessionManager
            {
                SessionDuration = TimeSpan.FromMilliseconds(500),
                PurgeInterval = TimeSpan.FromMilliseconds(50),
            };
            using var server = new TestWebServer()
                .WithSessionManager(manager)
                .WithAction("/set", HttpVerbs.Get, context =>
                {
                    context.Session["user"] = "alice";
                    return context.SendStringAsync("set", MimeType.PlainText, WebServer.DefaultEncoding);
                });
            server.Start();
            for (var i = 0; i < 3; i++)
            {
                using var client = TestHttpClient.Create(server);
                Assert.That(await client.GetStringAsync("/set"), Is.EqualTo("set"));
            }

            // Sessions holding data outlive their requests; only the purge removes them once expired.
            var sessions = typeof(LocalSessionManager).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(manager) as ICollection
                ?? throw new AssertionException("Missing session table.");
            Assert.That(sessions.Count, Is.EqualTo(3));
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (sessions.Count > 0 && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.That(sessions.Count, Is.Zero);
        }

        [Test]
        public async Task AChunkedRequestBodyReadsIntoMemoryThatIsNotArrayBacked()
        {
            var payload = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 31)).ToArray();
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[]? received = null;
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Post, async context =>
            {
                using var buffer = new OpaqueMemory(1000);
                using var copy = new MemoryStream();
                int read;
                while ((read = await context.Request.InputStream.ReadAsync(buffer.Memory, stop.Token)) > 0)
                    copy.Write(buffer.Memory.Span[..read]);
                received = copy.ToArray();
                await context.SendStringAsync("ok", MimeType.PlainText, WebServer.DefaultEncoding);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(payload) };
                request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(received, Is.EqualTo(payload));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        // Memory whose backing array MemoryMarshal.TryGetArray cannot expose.
        private sealed class OpaqueMemory : MemoryManager<byte>
        {
            private readonly byte[] _bytes;

            public OpaqueMemory(int length) => _bytes = new byte[length];

            public override Span<byte> GetSpan() => _bytes;

            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

            public override void Unpin()
            {
            }

            protected override void Dispose(bool disposing)
            {
            }
        }

        [Test]
        public async Task ZipEntryStreamsReadIntoSpansAndDisposeAsynchronouslyOnce()
        {
            var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 500).Select(i => i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture))));
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            using (var entry = zip.CreateEntry("data.txt").Open())
                entry.Write(payload);

            archive.Position = 0;
            using var provider = new ZipFileProvider(archive, true);
            var stream = provider.OpenFile("data.txt");
            var head = new byte[100];
            var filled = 0;
            while (filled < head.Length)
            {
                var read = stream.Read(head.AsSpan(filled));
                Assert.That(read, Is.GreaterThan(0));
                filled += read;
            }

            using var rest = new MemoryStream();
            await stream.CopyToAsync(rest);
            Assert.That(head.Concat(rest.ToArray()), Is.EqualTo(payload));
            await stream.DisposeAsync();
            await stream.DisposeAsync();

            // Balanced gate references keep the archive usable for later entry streams.
            await using var again = provider.OpenFile("data.txt");
            using var copy = new MemoryStream();
            await again.CopyToAsync(copy);
            Assert.That(copy.ToArray(), Is.EqualTo(payload));
        }

        [Test]
        public void RouteMatchLooksUpParametersByExactName()
        {
            var match = RouteMatcher.Parse("/users/{id}/{name}", false).Match("/users/7/bob") ?? throw new AssertionException("No match.");
            Assert.That(match.TryGetValue("id", out var id), Is.True);
            Assert.That(id, Is.EqualTo("7"));
            Assert.That(match.TryGetValue("name", out var name), Is.True);
            Assert.That(name, Is.EqualTo("bob"));
            Assert.That(match.TryGetValue("missing", out var missing), Is.False);
            Assert.That(missing, Is.Null);

            // The indexer and ContainsKey compare names exactly; TryGetValue agrees with them.
            Assert.That(match.TryGetValue("ID", out _), Is.False);
            Assert.That(match.ContainsKey("ID"), Is.False);
            Assert.Throws<KeyNotFoundException>(() => _ = match["ID"]);
        }

        [Test]
        public async Task FullDataResponseSendsTheExceptionMessageAndData()
        {
            using var server = new TestWebServer();
            server
                .WithAction("/", HttpVerbs.Any, _ => throw HttpException.BadRequest("Invalid order.", new { Field = "quantity" }))
                .HandleHttpException(HttpExceptionHandler.FullDataResponse(ResponseSerializer.Json));
            server.Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.That(json.RootElement.GetProperty("message").GetString(), Is.EqualTo("Invalid order."));
            Assert.That(json.RootElement.GetProperty("data").GetProperty("Field").GetString(), Is.EqualTo("quantity"));
        }

        private sealed class ProtocolModule : WebSocketModule
        {
            public ProtocolModule()
                : base("/ws", true)
            {
            }

            public void Add(IEnumerable<string> protocols) => AddProtocols(protocols);

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
        }

        [Test]
        public void AddProtocolsFromAnEnumerationRejectsDuplicatesAndInvalidNames()
        {
            using var module = new ProtocolModule();
            module.Add(new List<string> { "chat", "superchat" });
            Assert.Throws<ArgumentException>(() => module.Add(new List<string> { "other", "chat" }));

            // As documented, names before the failing one stay added.
            Assert.Throws<ArgumentException>(() => module.Add(new List<string> { "other" }));
            Assert.Throws<ArgumentException>(() => module.Add(new List<string> { "bad name" }));
            Assert.Throws<ArgumentNullException>(() => InvalidInput.Invoke((Action<IEnumerable<string>>)module.Add, new object?[] { null }));
        }

        [Test]
        public void UntypedJsonValuesDeserializeToDictionariesListsAndDecimals()
        {
            var value = Json.Deserialize("{\"a\":[1,2.5,\"x\",true,false,null,{\"b\":-3e2}],\"c\":{}}") as IDictionary<string, object?>
                ?? throw new AssertionException("Expected an object.");
            var list = value["a"] as IList<object?> ?? throw new AssertionException("Expected an array.");
            Assert.That(list, Is.EqualTo(new object?[] { 1m, 2.5m, "x", true, false, null, new Dictionary<string, object?> { ["b"] = -300m } }));
            Assert.That(value["c"], Is.Empty);
        }

        [Test]
        public async Task TheServerRefusesContextsBeforeItStartsAndAfterItStops()
        {
            using var server = new TestWebServer().WithAction("/", HttpVerbs.Any, context => context.SendStringAsync("ok", MimeType.PlainText, WebServer.DefaultEncoding));
            using var client = TestHttpClient.Create(server);
            var early = await Assert.CatchAsync<InvalidOperationException>(async () => await client.GetAsync("/"));
            Assert.That(early?.Message, Does.Contain("not been started"));

            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            Assert.That(await client.GetStringAsync("/"), Is.EqualTo("ok"));
            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            var late = await Assert.CatchAsync<InvalidOperationException>(async () => await client.GetAsync("/"));
            Assert.That(late?.Message, Does.Contain("already been stopped"));
        }

        private static async Task WithHttp2Server(RequestHandlerCallback handler, Func<HttpClient, string, Task> verify)
        {
            var url = EmbedIO.PlatformTests.HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new EmbedIO.Actions.ActionModule("/", HttpVerbs.Any, handler));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false })
                {
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = TimeSpan.FromSeconds(15),
                };
                await verify(client, url);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        // HTTP/2 forbids connection-specific fields; a client fails the stream if one is sent.
        [Test]
        public Task Http2ResponsesSendEachCookieAndValueAndDropConnectionFields() => WithHttp2Server(context =>
        {
            context.Response.SetCookie(new Cookie("a", "1"));
            context.Response.SetCookie(new Cookie("b", "2"));
            context.Response.Headers.Add("Connection", "close");
            context.Response.Headers.Add("Keep-Alive", "timeout=5");
            context.Response.Headers.Add("X-Custom", "one");
            context.Response.Headers.Add("X-Custom", "two");
            return context.SendStringAsync("ok", MimeType.PlainText, WebServer.DefaultEncoding);
        }, async (client, url) =>
        {
            using var response = await client.GetAsync(url);
            Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
            Assert.That(response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]), Is.EquivalentTo(new[] { "a=1", "b=2" }));
            Assert.That(response.Headers.GetValues("X-Custom"), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(response.Headers.Contains("Connection"), Is.False);
        });

        [Test]
        public Task Http2HeadAndNoContentResponsesFrameTheirLengthCorrectly() => WithHttp2Server(async context =>
        {
            if (context.Request.Url.AbsolutePath == "/empty")
            {
                context.Response.StatusCode = 204;
                return;
            }

            var body = System.Text.Encoding.ASCII.GetBytes("hello");
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
        }, async (client, url) =>
        {
            using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url + "file")))
            {
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(5));
                Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
            }

            using (var get = await client.GetAsync(url + "file"))
                Assert.That(await get.Content.ReadAsStringAsync(), Is.EqualTo("hello"));

            using var empty = await client.GetAsync(url + "empty");
            Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            Assert.That(empty.Content.Headers.Contains("Content-Length"), Is.False);
            Assert.That(await empty.Content.ReadAsByteArrayAsync(), Is.Empty);
        });

        public sealed class FactoryProbeController : WebApiController
        {
            [Route(HttpVerbs.Get, "/probe")]
            public string Probe() => "probe";
        }

        public sealed class OtherFactoryController : WebApiController
        {
            [Route(HttpVerbs.Get, "/other")]
            public string Other() => "other";
        }

        private static OtherFactoryController CreateOtherController() => new();

        // A lambda converted to Func<WebApiController> is compiled with that return type; it used to be rejected.
        [Test]
        public async Task ALambdaFactoryRegistersAControllerByType()
        {
            using var server = new TestWebServer().WithWebApi("/api", module => module.WithController(typeof(FactoryProbeController), () => new FactoryProbeController()));
            server.Start();
            Assert.That(await server.Client.GetStringAsync("/api/probe"), Does.Contain("probe"));
        }

        [Test]
        public async Task ATypedFactoryStillRegistersItsController()
        {
            using var server = new TestWebServer().WithWebApi("/api", module => module.WithController(() => new FactoryProbeController()));
            server.Start();
            Assert.That(await server.Client.GetStringAsync("/api/probe"), Does.Contain("probe"));
        }

        [Test]
        public async Task AFactoryThatCreatesTheWrongControllerNamesBothTypes()
        {
            Exception? failure = null;
            using var server = new TestWebServer();
            server
                .WithWebApi("/api", module => module.WithController(typeof(FactoryProbeController), () => new OtherFactoryController()))
                .HandleUnhandledException((context, exception) =>
                {
                    failure = exception;
                    context.Response.StatusCode = 500;
                    return Task.CompletedTask;
                });
            server.Start();
            using var response = await server.Client.GetAsync("/api/probe");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(failure, Is.InstanceOf<InvalidOperationException>());
            Assert.That(failure?.Message, Does.Contain(nameof(FactoryProbeController)).And.Contain(nameof(OtherFactoryController)));
        }

        [Test]
        public void AFactoryDeclaredWithAnUnrelatedControllerTypeIsStillRejected()
        {
            var module = new WebApiModule("/api");
            var error = Assert.Throws<ArgumentException>(() => module.RegisterController(typeof(FactoryProbeController), CreateOtherController));
            Assert.That(error?.ParamName, Is.EqualTo("factory"));
        }
    }
}
