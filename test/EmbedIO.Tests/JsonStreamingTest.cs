using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.PlatformTests;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class JsonStreamingTest
    {
        [TestCase(false, "array", "[1,2,3]", "application/json")]
        [TestCase(true, "array", "[1,2,3]", "application/json")]
        [TestCase(false, "array-task", "[1,2,3]", "application/json")]
        [TestCase(true, "array-task", "[1,2,3]", "application/json")]
        [TestCase(false, "lines", "1\n2\n3\n", "application/x-ndjson")]
        [TestCase(true, "lines", "1\n2\n3\n", "application/x-ndjson")]
        [TestCase(false, "lines-task", "1\n2\n3\n", "application/x-ndjson")]
        [TestCase(true, "lines-task", "1\n2\n3\n", "application/x-ndjson")]
        public async Task ControllerResultsBypassTheModuleSerializer(bool generated, string route, string expected, string media)
        {
            ResponseSerializerCallback forbidden = (_, _) => throw new InvalidOperationException("The module serializer must not see an explicit stream.");
            var module = new WebApiModule("/api", forbidden);
            if (generated) module.WithController(JsonStreamingBindings.JsonStreamingController);
            else module.WithController<JsonStreamingController>();
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var response = await client.GetAsync("/api/" + route);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(media));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(expected));
            });
        }

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task EmptyNullAndUnicodeRecordsRoundTrip(bool ndjson, int count)
        {
            var values = new string?[] { null, "line\n\r\"\\", "caf\u00e9 \ud83d\ude00" };
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                var source = Strings(values, count);
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(source, JsonStreamingContext.Default.String)
                    : JsonStream.Array(source, JsonStreamingContext.Default.String);
                await result.WriteAsync(context);
            })), async client =>
            {
                var text = await client.GetStringAsync("/");
                if (!ndjson)
                    Assert.That(JsonSerializer.Deserialize<string?[]>(text), Is.EqualTo(values[..count]));
                else
                {
                    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Assert.That(lines.Length, Is.EqualTo(count));
                    for (var i = 0; i < count; i++) Assert.That(JsonSerializer.Deserialize<string>(lines[i]), Is.EqualTo(values[i]));
                }
            });
        }

        [Test]
        public void PolicyRejectsInvalidLimits()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new JsonStreamOptions(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JsonStreamOptions(64 * 1024 * 1024 + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new JsonStreamOptions(flushEvery: 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FirstRecordLimitProducesAnOrdinaryError(bool ndjson)
        {
            var disposed = 0;
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                var source = Strings(new[] { new string('x', 100) }, 1, () => Interlocked.Increment(ref disposed));
                var options = new JsonStreamOptions(maxItemBytes: 8);
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(source, JsonStreamingContext.Default.String, options)
                    : JsonStream.Array(source, JsonStreamingContext.Default.String, options);
                await result.WriteAsync(context);
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(disposed, Is.EqualTo(1));
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task FirstRecordIsReadableBeforeTheProducerFinishes(bool ndjson, int protocol)
        {
            var advance = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken token = default)
            {
                try { yield return 7; await advance.Task.WaitAsync(token); yield return 8; }
                finally { disposed.TrySetResult(); }
            }
            await Wire(async context =>
            {
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.Int32)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32);
                await result.WriteAsync(context);
            }, async client =>
            {
                try
                {
                    using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
                    await using var body = await response.Content.ReadAsStreamAsync();
                    var prefix = new byte[2];
                    await body.ReadExactlyAsync(prefix).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(System.Text.Encoding.UTF8.GetString(prefix), Is.EqualTo(ndjson ? "7\n" : "[7"));
                    Assert.That(disposed.Task.IsCompleted, Is.False);
                    advance.TrySetResult();
                    using var reader = new StreamReader(body);
                    Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(ndjson ? "8\n" : ",8]"));
                    await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { advance.TrySetResult(); }
            }, protocol);
        }

        [TestCase(false, false, 1)]
        [TestCase(true, false, 1)]
        [TestCase(false, true, 1)]
        [TestCase(true, true, 1)]
        [TestCase(false, false, 2)]
        [TestCase(true, false, 2)]
        [TestCase(false, true, 2)]
        [TestCase(true, true, 2)]
        [TestCase(false, false, 3)]
        [TestCase(true, false, 3)]
        [TestCase(false, true, 3)]
        [TestCase(true, true, 3)]
        public async Task ProducerOrCleanupFailureAbortsCommittedResponses(bool ndjson, bool cleanupFailure, int protocol)
        {
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<int> Produce()
            {
                try { yield return 1; await Task.Yield(); if (!cleanupFailure) throw new InvalidOperationException("producer failed"); }
                finally { disposed.TrySetResult(); if (cleanupFailure) throw new InvalidOperationException("cleanup failed"); }
            }
            await Wire(async context =>
            {
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.Int32)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32);
                await result.WriteAsync(context);
            }, async client =>
            {
                Assert.That(async () => { using var response = await client.GetAsync("/"); }, Throws.InstanceOf<HttpRequestException>());
                await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }, protocol);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HeadDoesNotEnumerateTheProducer(bool ndjson)
        {
            var entered = 0;
            async IAsyncEnumerable<int> Produce() { Interlocked.Increment(ref entered); await Task.Yield(); yield return 1; }
            await Wire(async context =>
            {
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.Int32)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32);
                await result.WriteAsync(context);
            }, async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, "/");
                using var response = await client.SendAsync(request);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(entered, Is.Zero);
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationReachesTheProducerAndDisposesIt(bool ndjson)
        {
            using var cancel = new CancellationTokenSource();
            var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken token = default)
            {
                try { yield return 1; await Task.Delay(Timeout.Infinite, token); }
                finally { disposed.TrySetResult(); }
            }
            await Wire(async context =>
            {
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.Int32)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32);
                await result.WriteAsync(context, cancel.Token);
            }, async client =>
            {
                using var response = await client.GetAsync("/", HttpCompletionOption.ResponseHeadersRead);
                await using var body = await response.Content.ReadAsStreamAsync();
                await body.ReadExactlyAsync(new byte[2]);
                cancel.Cancel();
                await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(async () => await body.CopyToAsync(Stream.Null), Throws.InstanceOf<IOException>());
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LaterRecordSizeFailureDoesNotCompleteTheResponse(bool ndjson)
        {
            await Wire(async context =>
            {
                var source = Strings(new[] { "ok", new string('x', 1000) }, 2);
                var options = new JsonStreamOptions(maxItemBytes: 32);
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(source, JsonStreamingContext.Default.String, options)
                    : JsonStream.Array(source, JsonStreamingContext.Default.String, options);
                await result.WriteAsync(context);
            }, async client =>
            {
                Assert.That(async () => { using var response = await client.GetAsync("/"); }, Throws.InstanceOf<HttpRequestException>());
            });
        }

        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 3)]
        public async Task CompressionAndBatchedFlushesPreserveRecords(bool ndjson, int protocol)
        {
            await Wire(async context =>
            {
                var options = new JsonStreamOptions(flushEvery: 2, preferCompression: true);
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(JsonStreamingController.Numbers(), JsonStreamingContext.Default.Int32, options)
                    : JsonStream.Array(JsonStreamingController.Numbers(), JsonStreamingContext.Default.Int32, options);
                await result.WriteAsync(context);
            }, async client =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/") { Version = client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.AcceptEncoding.ParseAdd("gzip");
                using var response = await client.SendAsync(request);
                Assert.That(response.Version, Is.EqualTo(client.DefaultRequestVersion));
                Assert.That(response.Content.Headers.ContentEncoding, Does.Contain("gzip"));
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(gzip);
                Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(ndjson ? "1\n2\n3\n" : "[1,2,3]"));
            }, protocol);
        }

        [TestCase(false, 3, 500)]
        [TestCase(true, 3, 500)]
        [TestCase(false, 4, 200)]
        [TestCase(true, 4, 200)]
        public async Task RecordBudgetUsesTheExactSerializedByteCount(bool ndjson, int limit, int status)
        {
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                var source = Strings(new[] { "ok" }, 1);
                var options = new JsonStreamOptions(maxItemBytes: limit);
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(source, JsonStreamingContext.Default.String, options)
                    : JsonStream.Array(source, JsonStreamingContext.Default.String, options);
                await result.WriteAsync(context);
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
            });
        }

        [TestCase(false, 204)]
        [TestCase(true, 204)]
        [TestCase(false, 205)]
        [TestCase(true, 205)]
        [TestCase(false, 304)]
        [TestCase(true, 304)]
        public async Task StatusWithoutAnEntityDoesNotStartTheSource(bool ndjson, int status)
        {
            var entered = 0;
            async IAsyncEnumerable<int> Produce() { entered++; await Task.Yield(); yield return 1; }
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                context.Response.StatusCode = status;
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.Int32)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32);
                await result.WriteAsync(context);
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(entered, Is.Zero);
            });
        }

        [TestCase(false, 200)]
        [TestCase(true, 500)]
        public async Task RawConverterNewlinesCannotBreakNdjsonFraming(bool ndjson, int status)
        {
            async IAsyncEnumerable<RawStreamingValue> Produce() { await Task.Yield(); yield return new RawStreamingValue(); }
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                JsonStreamResult result = ndjson ? JsonStream.Ndjson(Produce(), JsonStreamingContext.Default.RawStreamingValue)
                    : JsonStream.Array(Produce(), JsonStreamingContext.Default.RawStreamingValue);
                await result.WriteAsync(context);
            })), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                if (!ndjson)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.That(body.RootElement[0].GetArrayLength(), Is.EqualTo(2));
                }
            });
        }

        [TestCase(0)]
        [TestCase(10)]
        public async Task PredeclaredLengthsAreRejectedBeforeEnumeration(int length)
        {
            var entered = 0;
            async IAsyncEnumerable<int> Produce() { entered++; await Task.Yield(); yield return 1; }
            await Wire(async context =>
            {
                context.Response.ContentLength64 = length;
                await JsonStream.Array(Produce(), JsonStreamingContext.Default.Int32).WriteAsync(context);
            }, async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(entered, Is.Zero);
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task OwnedStreamingControllersLiveUntilEnumerationCompletes(bool generated, bool task)
        {
            var controller = new StreamingLifetimeController();
            var module = new WebApiModule("/api");
            if (generated) module.WithController(JsonStreamingBindings.StreamingLifetimeController, () => controller);
            else module.WithController(() => controller);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var response = await client.GetAsync(task ? "/api/task" : "/api/array");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("[1,2]"));
                Assert.That(controller.Disposed, Is.EqualTo(1));
                Assert.That(controller.EnumerationDisposed, Is.EqualTo(1));
                Assert.That(controller.DisposedEarly, Is.False);
            });
        }

        [Test]
        public void OpenApiDescribesRecordsInsteadOfResultWrappers()
        {
            var doc = new OpenApiDocumentBuilder("Streams", "1").AddController("/api", JsonStreamingBindings.JsonStreamingController)
                .Build(JsonStreamingContext.Default.Options);
            Assert.That(doc.Root["paths"]?["/api/array"]?["get"]?["responses"]?["200"]?["content"]?["application/json"]?["schema"]?["type"]?.GetValue<string>(), Is.EqualTo("array"));
            Assert.That(doc.Root["paths"]?["/api/lines"]?["get"]?["responses"]?["200"]?["content"]?["application/x-ndjson"]?["x-embedio-framing"]?.GetValue<string>(), Is.EqualTo("ndjson"));
        }

        internal static async IAsyncEnumerable<string?> Strings(string?[] values, int count, Action? dispose = null)
        {
            try { for (var i = 0; i < count; i++) { await Task.Yield(); yield return values[i]; } }
            finally { dispose?.Invoke(); }
        }

        private static async Task Wire(Func<IHttpContext, Task> handler, Func<HttpClient, Task> use, int protocol = 1)
        {
            if (protocol == 3 && (!QuicListener.IsSupported || !QuicConnection.IsSupported))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required by this job.");
                Assert.Ignore("The host does not provide QUIC."); return;
            }
            var url = TestObjects.Resources.GetServerAddress();
            if (protocol > 1) url = url.Replace("http://", "https://", StringComparison.Ordinal);
            using var certificate = protocol > 1 ? HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable) : null;
            using var stop = new CancellationTokenSource();
            using var server = (protocol == 1 ? new WebServer(url)
                : new WebServer(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO, certificate ?? throw new InvalidOperationException("Missing test certificate."), url))
                .WithModule(new ActionModule(context => handler(context)));
            var running = server.RunAsync(stop.Token);
            using var transport = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                { RemoteCertificateValidationCallback = (_, peer, _, _) => certificate != null && peer?.GetCertHashString() == certificate.GetCertHashString() }
            };
            try
            {
                using var client = new HttpClient(transport)
                {
                    BaseAddress = new Uri(url),
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = protocol == 3 ? HttpVersion.Version30 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                await use(client);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    [GeneratedControllerBinding(typeof(JsonStreamingController))]
    [GeneratedControllerBinding(typeof(StreamingLifetimeController))]
    internal static partial class JsonStreamingBindings { }

    public sealed class JsonStreamingController : WebApiController
    {
        [Route(HttpVerbs.Get, "/array")]
        public JsonArrayStream<int> Array() => JsonStream.Array(Numbers(), JsonStreamingContext.Default.Int32);
        [Route(HttpVerbs.Get, "/array-task")]
        public Task<JsonArrayStream<int>> ArrayTask() => Task.FromResult(Array());
        [Route(HttpVerbs.Get, "/lines")]
        public NdjsonStream<int> Lines() => JsonStream.Ndjson(Numbers(), JsonStreamingContext.Default.Int32);
        [Route(HttpVerbs.Get, "/lines-task")]
        public Task<NdjsonStream<int>> LinesTask() => Task.FromResult(Lines());
        internal static async IAsyncEnumerable<int> Numbers() { for (var i = 1; i <= 3; i++) { await Task.Yield(); yield return i; } }
    }

    public sealed class StreamingLifetimeController : WebApiController, IDisposable
    {
        public int Disposed;
        public int EnumerationDisposed;
        public bool DisposedEarly;
        [Route(HttpVerbs.Get, "/array")]
        public JsonArrayStream<int> Array() => JsonStream.Array(Values(), JsonStreamingContext.Default.Int32);
        [Route(HttpVerbs.Get, "/task")]
        public Task<JsonArrayStream<int>> TaskResult() => Task.FromResult(Array());
        private async IAsyncEnumerable<int> Values()
        {
            try { yield return 1; await Task.Yield(); DisposedEarly = Disposed != 0; yield return 2; }
            finally { EnumerationDisposed++; }
        }
        public void Dispose() { DisposedEarly |= EnumerationDisposed != 1; Disposed++; }
    }

    [JsonConverter(typeof(RawStreamingConverter))]
    internal sealed class RawStreamingValue { }
    internal sealed class RawStreamingConverter : JsonConverter<RawStreamingValue>
    {
        public override RawStreamingValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, RawStreamingValue value, JsonSerializerOptions options) => writer.WriteRawValue("[1,\n2]");
    }

    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(RawStreamingValue))]
    internal sealed partial class JsonStreamingContext : JsonSerializerContext { }
}
