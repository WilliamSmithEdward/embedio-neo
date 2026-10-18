using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authorization;
using EmbedIO.Caching;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed partial class OutputCacheTest
    {
        private static OutputCachePolicy Policy(TimeSpan? duration = null) => new(duration ?? TimeSpan.FromMinutes(1), new[] { "X-Variant" }, new[] { "products" });
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static async Task Write(IHttpContext context, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.ContentType = "text/plain";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, context.CancellationToken);
        }
        private static Task With(OutputCache cache, OutputCachePolicy policy, Func<IHttpContext, Task> action, Func<HttpClient, Task> verify,
            Action<IHttpContext>? before = null)
            => TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                before?.Invoke(context);
                await cache.ExecuteAsync(context, policy, action);
            })), verify);
        private static async Task<string> Read(HttpClient client, string path = "/items")
        {
            using var response = await client.GetAsync(path);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            return await response.Content.ReadAsStringAsync();
        }

        [Test]
        public async Task ReusesCompletedBytesAndInvalidatesAfterMutation()
        {
            var cache = new OutputCache(); var calls = 0; var state = "old";
            await With(cache, Policy(), c => { calls++; return Write(c, state); }, async client =>
            {
                Assert.That(await Read(client), Is.EqualTo("old")); state = "new";
                Assert.That(await Read(client), Is.EqualTo("old")); Assert.That(calls, Is.EqualTo(1));
                cache.InvalidateTag("products"); Assert.That(await Read(client), Is.EqualTo("new")); Assert.That(calls, Is.EqualTo(2));
                cache.Clear(); Assert.That(cache.Count, Is.Zero); Assert.That(cache.SizeBytes, Is.Zero);
            });
        }

        [TestCase("/items?a=1", "/items?a=2")]
        [TestCase("/items?a=1&b=2", "/items?b=2&a=1")]
        [TestCase("/items/A", "/items/a")]
        [TestCase("http://one.test/items", "http://two.test/items")]
        public async Task KeysPreserveUrlAndAuthority(string one, string two)
        {
            var calls = 0;
            await With(new OutputCache(), Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            {
                Assert.That(await Read(client, one), Is.EqualTo("1")); Assert.That(await Read(client, two), Is.EqualTo("2"));
                Assert.That(await Read(client, one), Is.EqualTo("1"));
            });
        }

        [TestCase("Accept", "text/plain", "application/json")]
        [TestCase("Accept-Encoding", "identity", "gzip")]
        [TestCase("Accept-Language", "en", "de")]
        [TestCase("Origin", "https://one.test", "https://two.test")]
        [TestCase("X-Variant", "one", "two")]
        public async Task DeclaredHeadersVaryTheKey(string field, string one, string two)
        {
            var calls = 0;
            await With(new OutputCache(), Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(field, one); Assert.That(await Read(client), Is.EqualTo("1"));
                client.DefaultRequestHeaders.Remove(field); client.DefaultRequestHeaders.TryAddWithoutValidation(field, two);
                Assert.That(await Read(client), Is.EqualTo("2")); Assert.That(await Read(client), Is.EqualTo("2"));
            });
        }

        [TestCase("Authorization", "Bearer secret")]
        [TestCase("Proxy-Authorization", "Basic secret")]
        [TestCase("Cookie", "session=secret")]
        [TestCase("Range", "bytes=0-1")]
        [TestCase("If-None-Match", "\"tag\"")]
        [TestCase("If-Match", "\"tag\"")]
        [TestCase("If-Modified-Since", "Wed, 21 Oct 2015 07:28:00 GMT")]
        [TestCase("If-Unmodified-Since", "Wed, 21 Oct 2015 07:28:00 GMT")]
        [TestCase("If-Range", "\"tag\"")]
        [TestCase("Cache-Control", "no-cache")]
        [TestCase("Pragma", "no-cache")]
        public async Task SensitiveAndConditionalRequestsBypassExistingEntries(string field, string value)
        {
            var calls = 0;
            await With(new OutputCache(), Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            {
                Assert.That(await Read(client), Is.EqualTo("1")); client.DefaultRequestHeaders.TryAddWithoutValidation(field, value);
                Assert.That(await Read(client), Is.EqualTo("2")); Assert.That(await Read(client), Is.EqualTo("3"));
                client.DefaultRequestHeaders.Remove(field); Assert.That(await Read(client), Is.EqualTo("1"));
            });
        }

        [TestCase("cookie")]
        [TestCase("cookie-header")]
        [TestCase("no-store")]
        [TestCase("private")]
        [TestCase("vary-star")]
        [TestCase("vary-undeclared")]
        [TestCase("custom-field")]
        [TestCase("flush")]
        [TestCase("sse")]
        [TestCase("session")]
        [TestCase("completion")]
        [TestCase("async-completion")]
        public async Task UncacheableResponsesAreNeverStored(string kind)
        {
            var cache = new OutputCache(); var calls = 0;
            await With(cache, Policy(), async c =>
            {
                calls++;
                switch (kind)
                {
                    case "cookie": c.Response.SetCookie(new Cookie("a", "b")); break;
                    case "cookie-header": c.Response.Headers["Set-Cookie"] = "a=b"; break;
                    case "no-store": c.Response.Headers["Cache-Control"] = "no-store"; break;
                    case "private": c.Response.Headers["Cache-Control"] = "private"; break;
                    case "vary-star": c.Response.Headers["Vary"] = "*"; break;
                    case "vary-undeclared": c.Response.Headers["Vary"] = "User-Agent"; break;
                    case "custom-field": c.Response.Headers["X-Request"] = c.Id; break;
                    case "session": _ = c.Session; break;
                    case "completion": c.OnClose(_ => { }); break;
                    case "async-completion": c.OnRequestCompleted(() => Task.CompletedTask); break;
                }
                await Write(c, "value");
                if (kind == "flush") await c.Response.OutputStream.FlushAsync();
                if (kind == "sse") c.Response.ContentType = "text/event-stream";
            }, async client => { await Read(client); await Read(client); Assert.That(calls, Is.EqualTo(2)); Assert.That(cache.Count, Is.Zero); });
        }

        [TestCase(201)]
        [TestCase(204)]
        [TestCase(302)]
        [TestCase(400)]
        [TestCase(500)]
        public async Task OnlySuccessful200ResponsesAreStored(int status)
        {
            var cache = new OutputCache(); var calls = 0;
            await With(cache, Policy(), c => { calls++; c.Response.StatusCode = status; return Task.CompletedTask; }, async client =>
            {
                using var first = await client.GetAsync("/"); using var second = await client.GetAsync("/");
                Assert.That(calls, Is.EqualTo(2)); Assert.That(cache.Count, Is.Zero);
            });
        }

        [Test]
        public async Task AuthenticatedPrincipalBypassesEvenWithoutAuthorizationHeader()
        {
            var calls = 0; var cache = new OutputCache();
            await With(cache, Policy(), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            { await Read(client); await Read(client); Assert.That(calls, Is.EqualTo(2)); Assert.That(cache.Count, Is.Zero); },
            c => c.GetImplementation().User = new GenericPrincipal(new GenericIdentity("user", "custom"), Array.Empty<string>()));
        }

        [Test]
        public async Task ConcurrentFillRunsOnceAndCanceledWaiterCannotCancelProducer()
        {
            var entered = Signal(); var release = Signal(); var calls = 0;
            await With(new OutputCache(), Policy(), async c => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(c, "ready"); }, async client =>
            {
                var producer = Read(client);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    using var cancellation = new CancellationTokenSource();
                    var canceled = client.GetAsync("/items", cancellation.Token);
                    var live = Read(client);
                    cancellation.Cancel();
                    await Assert.ThrowsAsync<TaskCanceledException>(async () => { using var response = await canceled; });
                    release.TrySetResult();
                    Assert.That(await producer.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("ready"));
                    Assert.That(await live.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("ready"));
                    Assert.That(calls, Is.EqualTo(1));
                }
                finally { release.TrySetResult(); }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InvalidationFencesAnEarlierFill(bool clear)
        {
            var cache = new OutputCache(); var entered = Signal(); var release = Signal(); var calls = 0; var state = "old";
            await With(cache, Policy(), async c =>
            {
                var value = state;
                if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await release.Task; }
                await Write(c, value);
            }, async client =>
            {
                var old = Read(client);
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); state = "new";
                    if (clear) cache.Clear(); else cache.InvalidateTag("products");
                }
                finally { release.TrySetResult(); }
                Assert.That(await old, Is.EqualTo("old")); Assert.That(cache.Count, Is.Zero);
                Assert.That(await Read(client), Is.EqualTo("new")); Assert.That(await Read(client), Is.EqualTo("new"));
                Assert.That(calls, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task FaultedFillReleasesWaitersAndDoesNotPublishPartialOutput()
        {
            var cache = new OutputCache(); var calls = 0;
            await With(cache, Policy(), async c => { if (++calls == 1) throw new InvalidOperationException("failure"); await Write(c, "healthy"); }, async client =>
            {
                using var failed = await client.GetAsync("/items"); Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(cache.Count, Is.Zero); Assert.That(await Read(client), Is.EqualTo("healthy")); Assert.That(await Read(client), Is.EqualTo("healthy"));
                Assert.That(calls, Is.EqualTo(2));
            });
        }

        [Test]
        public async Task ExpiryUsesFillStartAndRefills()
        {
            var calls = 0;
            await With(new OutputCache(), Policy(TimeSpan.FromMilliseconds(50)), c => Write(c, (++calls).ToString(System.Globalization.CultureInfo.InvariantCulture)), async client =>
            { await Read(client); await Task.Delay(100); Assert.That(await Read(client), Is.EqualTo("2")); });
        }

        [TestCase(3, 4096, 8, 2)]
        [TestCase(2, 800, 128, 2)]
        public async Task EntryAndByteCapsEvictWithoutChangingBodies(int entries, long bytes, int bodyLimit, int expectedMaximum)
        {
            var cache = new OutputCache(entries, bytes, bodyLimit); var calls = 0;
            await With(cache, Policy(), c => { calls++; return Write(c, "12345678"); }, async client =>
            {
                for (var i = 0; i < 8; i++) { Assert.That(await Read(client, "/" + i), Is.EqualTo("12345678")); Assert.That(cache.Count, Is.LessThanOrEqualTo(entries)); Assert.That(cache.SizeBytes, Is.LessThanOrEqualTo(bytes)); }
                Assert.That(cache.Count, Is.InRange(1, expectedMaximum + 1));
            });
        }

        [Test]
        public async Task OversizeBodiesAndKeysBypassWithoutTruncation()
        {
            var cache = new OutputCache(maxResponseBytes: 4, maxKeyCharacters: 128); var calls = 0;
            await With(cache, Policy(), c => { calls++; return Write(c, "12345"); }, async client =>
            { Assert.That(await Read(client), Is.EqualTo("12345")); Assert.That(await Read(client), Is.EqualTo("12345")); await Read(client, "/" + new string('x', 200)); Assert.That(calls, Is.EqualTo(3)); Assert.That(cache.Count, Is.Zero); });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllerParityHooksRunOnHitsAndMethodCanDisableClassPolicy(bool generated)
        {
            var cache = new OutputCache(); var state = new CachedControllerState();
            var module = new WebApiModule("/api", ResponseSerializer.None(false));
            module.ConfigureOutputCaching(cache, new Dictionary<string, OutputCachePolicy> { ["public"] = Policy() });
            if (generated) module.RegisterController(OutputCacheBindings.CachedController, () => new CachedController(state));
            else module.RegisterController(() => new CachedController(state));
            await TestWebServer.UseAsync(server => server.WithModule(module), async client =>
            {
                Assert.That(await Read(client, "/api/value"), Is.EqualTo("value-1"));
                Assert.That(await Read(client, "/api/value"), Is.EqualTo("value-1"));
                Assert.That(state.Hooks, Is.EqualTo(2)); Assert.That(state.Releases, Is.EqualTo(2));
                Assert.That(await Read(client, "/api/uncached"), Is.EqualTo("value-2")); Assert.That(await Read(client, "/api/uncached"), Is.EqualTo("value-3"));
                state.Reject = true;
                using var rejected = await client.GetAsync("/api/value"); Assert.That(rejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(state.Calls, Is.EqualTo(3)); Assert.That(state.Releases, Is.EqualTo(5));
                using var protectedRoute = await client.GetAsync("/api/protected"); Assert.That(protectedRoute.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnknownControllerPolicyFailsBeforeRegistration(bool generated)
        {
            var module = new WebApiModule("/");
            Assert.Throws<InvalidOperationException>(() => { if (generated) module.RegisterController(OutputCacheBindings.CachedController, () => new CachedController(new CachedControllerState())); else module.RegisterController(() => new CachedController(new CachedControllerState())); });
            Assert.That(module.ControllerCount, Is.Zero);
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void InvalidLimitsAreRejected(int value)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCache(maxEntries: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCache(maxBytes: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCache(maxResponseBytes: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCache(maxConcurrentFills: value));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OutputCachePolicy(TimeSpan.FromSeconds(value)));
        }
    }

    public sealed class CachedControllerState
    {
        public int Calls; public int Hooks; public int Releases; public bool Reject;
    }

    [OutputCache("public"), BeforeHandler(nameof(Prepare))]
    public sealed class CachedController : WebApiController, IDisposable
    {
        private readonly CachedControllerState _state;
        public CachedController(CachedControllerState state) => _state = state;
        public void Prepare() { _state.Hooks++; if (_state.Reject) throw HttpException.Forbidden(); Response.Headers["X-Hook"] = _state.Hooks.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        [Route(HttpVerbs.Get, "/value")]
        public string Value() => "value-" + (++_state.Calls).ToString(System.Globalization.CultureInfo.InvariantCulture);
        [Route(HttpVerbs.Get, "/uncached"), OutputCache(null)]
        public string Uncached() => Value();
        [Route(HttpVerbs.Get, "/protected"), Authorize]
        public string Protected() => Value();
        [Route(HttpVerbs.Get, "/query")]
        public Task<string> Query([QueryField("__context")] string text) => Task.FromResult(text);
        public void Dispose() => _state.Releases++;
    }

    [GeneratedControllerBinding(typeof(CachedController))]
    internal partial class OutputCacheBindings;
}
