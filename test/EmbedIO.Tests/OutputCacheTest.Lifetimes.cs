using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Health;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class OutputCacheTest
    {
        [Test]
        public async Task CaptureConcurrencyOverflowBypassesAndDoesNotEvictActiveProducer()
        {
            var cache = new OutputCache(maxConcurrentFills: 1); var entered = Signal(); var release = Signal(); var calls = 0;
            await With(cache, Policy(), async c =>
            {
                Interlocked.Increment(ref calls);
                if (c.Request.Url.AbsolutePath == "/held") { entered.TrySetResult(); await release.Task; }
                await Write(c, c.Request.Url.AbsolutePath);
            }, async client =>
            {
                var held = Read(client, "/held");
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(await Read(client, "/overflow"), Is.EqualTo("/overflow"));
                    Assert.That(await Read(client, "/overflow"), Is.EqualTo("/overflow")); Assert.That(cache.Count, Is.Zero);
                }
                finally { release.TrySetResult(); }
                Assert.That(await held, Is.EqualTo("/held")); Assert.That(await Read(client, "/held"), Is.EqualTo("/held"));
                Assert.That(calls, Is.EqualTo(3)); Assert.That(cache.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public async Task CancellationOfProducerAllowsHealthyRefill()
        {
            var cache = new OutputCache(); var entered = Signal(); var calls = 0;
            await With(cache, Policy(), async c =>
            {
                if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, c.CancellationToken); }
                await Write(c, "healthy");
            }, async client =>
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var first = client.GetAsync("/items", cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var second = Read(client); cancel.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(async () => { using var response = await first; });
                Assert.That(await second.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("healthy")); Assert.That(await Read(client), Is.EqualTo("healthy"));
                Assert.That(calls, Is.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SlowFillCannotOutliveItsExpiry(bool invalidate)
        {
            var cache = new OutputCache(); var calls = 0;
            await With(cache, Policy(TimeSpan.FromMilliseconds(30)), async c =>
            { calls++; if (invalidate) cache.InvalidateTag("products"); await Task.Delay(60); await Write(c, "value"); }, async client =>
            { await Read(client); Assert.That(cache.Count, Is.Zero); await Read(client); Assert.That(calls, Is.EqualTo(2)); });
        }

        [Test]
        public async Task HeaderValueBoundariesCannotCollideAcrossVaryFields()
        {
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1), new[] { "X-One", "X-Two" }); var calls = 0;
            await With(new OutputCache(), policy, c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            {
                using var one = new HttpRequestMessage(HttpMethod.Get, "/items");
                one.Headers.TryAddWithoutValidation("X-One", new[] { "x", "end", "present", "y" });
                using var first = await client.SendAsync(one);
                using var two = new HttpRequestMessage(HttpMethod.Get, "/items");
                two.Headers.TryAddWithoutValidation("X-One", "x"); two.Headers.TryAddWithoutValidation("X-Two", "y");
                using var second = await client.SendAsync(two);
                Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("1")); Assert.That(await second.Content.ReadAsStringAsync(), Is.EqualTo("2"));
            });
        }

        [Test]
        public async Task NewlyUndeclaredVaryFromHookCannotReuseOldEntry()
        {
            var calls = 0; var unsupported = false;
            await With(new OutputCache(), Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            { Assert.That(await Read(client), Is.EqualTo("1")); unsupported = true; Assert.That(await Read(client), Is.EqualTo("2")); },
            c => { if (unsupported) c.Response.Headers["Vary"] = "User-Agent"; });
        }

        [Test]
        public async Task PoliciesHaveIndependentKeySpacesAndTagsAreCaseSensitive()
        {
            var cache = new OutputCache(); var first = new OutputCachePolicy(TimeSpan.FromMinutes(1), tags: new[] { "Products" });
            var second = Policy(); var useFirst = true; var calls = 0;
            await TestWebServer.UseAsync(server => server.WithModule(new ActionModule(c => cache.ExecuteAsync(c, useFirst ? first : second,
                captured => Write(captured, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture))))), async client =>
            {
                Assert.That(await Read(client), Is.EqualTo("1")); useFirst = false; Assert.That(await Read(client), Is.EqualTo("2"));
                cache.InvalidateTag("products"); useFirst = true; Assert.That(await Read(client), Is.EqualTo("1"));
                useFirst = false; Assert.That(await Read(client), Is.EqualTo("3"));
            });
        }

        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        public async Task RealHttpReplaysFramingCompressionAndHealthySubsequentRequests(int protocol)
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var cache = new OutputCache(); var calls = 0; var policy = Policy(); var body = new string('x', 4096);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(options => options.WithUrlPrefix($"http://127.0.0.1:{port}/"))
                .WithModule(new ActionModule(c => cache.ExecuteAsync(c, policy, captured => { Interlocked.Increment(ref calls); return ResponseSerializer.None(false)(captured, body); })));
            using var health = new HealthCheckService(server); var running = server.RunAsync(stop.Token);
            try
            {
                await health.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler { UseProxy = false, AutomaticDecompression = DecompressionMethods.None };
                using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                client.DefaultRequestVersion = protocol == 10 ? HttpVersion.Version10 : protocol == 20 ? HttpVersion.Version20 : HttpVersion.Version11;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "gzip");
                for (var i = 0; i < 3; i++)
                {
                    using var response = await client.GetAsync("/items", stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
                    Assert.That(response.Content.Headers.GetValues("Content-Type").Count(), Is.EqualTo(1));
                    using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(stop.Token), CompressionMode.Decompress);
                    using var text = new StreamReader(gzip); Assert.That(await text.ReadToEndAsync(stop.Token), Is.EqualTo(body));
                }
                Assert.That(calls, Is.EqualTo(1)); Assert.That(cache.Count, Is.EqualTo(1));
                client.DefaultRequestHeaders.Remove("Accept-Encoding");
                using var identity = await client.GetAsync("/items", stop.Token);
                Assert.That(await identity.Content.ReadAsStringAsync(stop.Token), Is.EqualTo(body)); Assert.That(calls, Is.EqualTo(2));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CachedToUncachedActivationRestoresLegacyNoStoreAndLiteralQueryBinding(bool generated)
        {
            var cache = new OutputCache(); var controller = new CachedController(new CachedControllerState());
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            module.ConfigureOutputCaching(cache, new Dictionary<string, OutputCachePolicy> { ["public"] = Policy() });
            if (generated) module.RegisterController(OutputCacheBindings.CachedController, () => controller);
            else module.RegisterController(() => controller);
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                await Read(client, "/api/value");
                using var uncached = await client.GetAsync("/api/uncached");
                Assert.That(uncached.Headers.CacheControl?.NoStore, Is.True);
                Assert.That(await Read(client, "/api/query?__context=literal"), Is.EqualTo("literal"));
                Assert.That(await Read(client, "/api/query?__context=literal"), Is.EqualTo("literal"));
            });
        }

        [TestCase("POST")]
        [TestCase("PUT")]
        [TestCase("DELETE")]
        public async Task UnsafeMethodsWithBodiesCannotReuseOrPopulateGetEntry(string method)
        {
            var calls = 0; var cache = new OutputCache();
            await With(cache, Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            {
                Assert.That(await Read(client), Is.EqualTo("1"));
                using var request = new HttpRequestMessage(new HttpMethod(method), "/items") { Content = new StringContent("body") };
                using var response = await client.SendAsync(request);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("2")); Assert.That(await Read(client), Is.EqualTo("1"));
                Assert.That(cache.Count, Is.EqualTo(1));
            });
        }

        [TestCase(10)]
        [TestCase(11)]
        [TestCase(20)]
        public async Task HeadMetadataIsCachedSeparatelyFromGetWithoutInventingABody(int protocol)
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
            var cache = new OutputCache(); var policy = Policy(); var calls = 0;
            using var server = new WebServer($"http://127.0.0.1:{port}/").WithModule(new ActionModule(c => cache.ExecuteAsync(c, policy, captured =>
            {
                Interlocked.Increment(ref calls);
                if (captured.Request.HttpVerb != HttpVerbs.Head) return Write(captured, "get");
                captured.Response.ContentType = "text/plain"; captured.Response.ContentLength64 = 123; return Task.CompletedTask;
            })));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var ready = new HealthCheckService(server); var running = server.RunAsync(stop.Token);
            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler { UseProxy = false };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                    DefaultRequestVersion = protocol == 10 ? HttpVersion.Version10 : protocol == 20 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                for (var i = 0; i < 2; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Head, "/items") { Version = client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                    using var response = await client.SendAsync(request, stop.Token);
                    Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(123));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(stop.Token), Is.Empty);
                }
                Assert.That(calls, Is.EqualTo(1)); Assert.That(await Read(client), Is.EqualTo("get")); Assert.That(calls, Is.EqualTo(2));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllerReleaseFailureCannotPublishAndReleaseRunsOnlyOnce(bool generated)
        {
            var cache = new OutputCache(); var state = new CachedControllerState(); var releases = 0;
            var module = new WebApiModule("/api", (context, data) => Write(context, data as string ?? string.Empty));
            module.ConfigureOutputCaching(cache, new Dictionary<string, OutputCachePolicy> { ["public"] = Policy() });
            Task Release(CachedController controller)
            {
                controller.Dispose();
                if (++releases == 1) throw new InvalidOperationException("release failed");
                return Task.CompletedTask;
            }
            if (generated) module.RegisterControllerWithContext(OutputCacheBindings.CachedController, _ => new CachedController(state), (_, controller) => Release(controller));
            else module.RegisterControllerWithContext(typeof(CachedController), _ => new CachedController(state), (_, controller) => Release((CachedController)controller));
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                using var failed = await client.GetAsync("/api/value");
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(cache.Count, Is.Zero); Assert.That(releases, Is.EqualTo(1));
                Assert.That(await Read(client, "/api/value"), Is.EqualTo("value-2"));
                Assert.That(await Read(client, "/api/value"), Is.EqualTo("value-2"));
                Assert.That(releases, Is.EqualTo(3)); Assert.That(state.Releases, Is.EqualTo(3)); Assert.That(state.Calls, Is.EqualTo(2));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task WaitersCannotObserveAnEntryBeforeControllerReleaseCompletes(bool generated)
        {
            var cache = new OutputCache(); var state = new CachedControllerState(); var entered = Signal(); var allowRelease = Signal(); var releases = 0;
            var module = new WebApiModule("/api", (context, data) => Write(context, data as string ?? string.Empty));
            module.ConfigureOutputCaching(cache, new Dictionary<string, OutputCachePolicy> { ["public"] = Policy() });
            async Task Release(CachedController controller)
            {
                if (Interlocked.Increment(ref releases) == 1) { entered.TrySetResult(); await allowRelease.Task; }
                controller.Dispose();
            }
            if (generated) module.RegisterControllerWithContext(OutputCacheBindings.CachedController, _ => new CachedController(state), (_, controller) => Release(controller));
            else module.RegisterControllerWithContext(typeof(CachedController), _ => new CachedController(state), (_, controller) => Release((CachedController)controller));
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                var first = Read(client, "/api/value"); Task<string>? waiter = null;
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); waiter = Read(client, "/api/value");
                    Assert.That(cache.Count, Is.Zero); Assert.That(state.Calls, Is.EqualTo(1)); Assert.That(waiter.IsCompleted, Is.False);
                }
                finally { allowRelease.TrySetResult(); }
                Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("value-1"));
                Assert.That(await (waiter ?? throw new AssertionException("Missing waiter.")).WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("value-1"));
                Assert.That(releases, Is.EqualTo(2)); Assert.That(state.Releases, Is.EqualTo(2)); Assert.That(cache.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void PolicyValidationRejectsMalformedOrUnboundedMetadata()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCachePolicy(TimeSpan.FromDays(2)));
            Assert.Throws<ArgumentException>(() => new OutputCachePolicy(TimeSpan.FromSeconds(1), new[] { "X-Bad\r\nName" }));
            Assert.Throws<ArgumentException>(() => new OutputCachePolicy(TimeSpan.FromSeconds(1), tags: Enumerable.Range(0, 17).Select(i => "tag" + i)));
            Assert.Throws<ArgumentException>(() => new OutputCachePolicy(TimeSpan.FromSeconds(1), tags: new[] { new string('x', 129) }));
            Assert.Throws<ArgumentException>(() => new OutputCachePolicy(TimeSpan.FromSeconds(1), tags: new[] { " " }));
        }
    }
}
