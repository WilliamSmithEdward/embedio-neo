using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Authorization;
using EmbedIO.Idempotency;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;
using NUnit.Framework;
using Coordinator = EmbedIO.Idempotency.IdempotencyCoordinator;

namespace EmbedIO.Tests
{
    [TestFixture]
    public sealed partial class IdempotencyTest
    {
        private static IdempotencyPolicy Policy(TimeSpan? retention = null, int request = 4096, int response = 4096, TimeSpan? wait = null, string partition = "tests")
            => new IdempotencyPolicy(partition, c => c.User.Identity?.Name ?? "fixture", retention ?? TimeSpan.FromMinutes(1), request, response, wait, new[] { "X-Variant" });
        private static Task With(Coordinator coordinator, IdempotencyPolicy policy, Func<IHttpContext, Task> action, Func<HttpClient, Task> verify, Action<IHttpContext>? before = null)
            => TestWebServer.UseAsync(server => server.WithModule(new ActionModule(async context =>
            {
                // Fixture middleware supplies a trusted principal; applications must use their authentication layer.
                context.GetImplementation().User = new GenericPrincipal(new GenericIdentity(context.Request.Headers["Test-Caller"] ?? "a"), Array.Empty<string>());
                before?.Invoke(context);
                await coordinator.ExecuteAsync(context, policy, action);
            })), verify);
        private static async Task Write(IHttpContext context, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, context.CancellationToken);
        }
        private static async Task<HttpResponseMessage> Send(HttpClient client, string key = "one", string body = "payload", string path = "/write", string caller = "a", string variant = "first", CancellationToken token = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", key); request.Headers.Add("Test-Caller", caller); request.Headers.Add("X-Variant", variant);
            return await client.SendAsync(request, token);
        }
        private static TaskCompletionSource Signal() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        [Test]
        public async Task ReplaysStatusHeadersAndBytesWithoutRepeatingSideEffect()
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), async c =>
            { calls++; Assert.That(await c.GetRequestBodyAsStringAsync(), Is.EqualTo("payload")); c.Response.StatusCode = 201; c.Response.Headers["Location"] = "/created/1"; await Write(c, "created-1"); }, async client =>
            {
                using var first = await Send(client); using var second = await Send(client);
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Created)); Assert.That(second.StatusCode, Is.EqualTo(first.StatusCode));
                Assert.That(await second.Content.ReadAsStringAsync(), Is.EqualTo("created-1")); Assert.That(second.Headers.Location?.ToString(), Is.EqualTo("/created/1")); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [TestCase("body")]
        [TestCase("variant")]
        public async Task ReusingKeyWithDifferentFingerprintConflicts(string changed)
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); using var conflict = await Send(client, body: changed == "body" ? "different" : "payload", variant: changed == "variant" ? "second" : "first");
                Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict)); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [TestCase("caller")]
        [TestCase("route")]
        [TestCase("query")]
        [TestCase("key")]
        public async Task IndependentOperationDimensionsDoNotCollide(string changed)
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); using var second = await Send(client, key: changed == "key" ? "two" : "one", caller: changed == "caller" ? "b" : "a", path: changed == "route" ? "/other" : changed == "query" ? "/write?q=1" : "/write");
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await second.Content.ReadAsStringAsync(), Is.EqualTo("2")); Assert.That(calls, Is.EqualTo(2));
            });
        }
        [Test]
        public async Task ConcurrentDuplicateWaitsAndReplaysOneExecution()
        {
            var calls = 0; var entered = Signal(); var release = Signal();
            await With(new Coordinator(), Policy(), async c => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(c, "done"); }, async client =>
            {
                var first = Send(client); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var second = Send(client);
                try { await Task.Delay(60); Assert.That(second.IsCompleted, Is.False); Assert.That(calls, Is.EqualTo(1)); }
                finally { release.TrySetResult(); }
                using var a = await first; using var b = await second; Assert.That(await b.Content.ReadAsStringAsync(), Is.EqualTo("done")); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [Test]
        public async Task CanceledWaiterDoesNotCancelOwnerOrSibling()
        {
            var calls = 0; var entered = Signal(); var release = Signal();
            await With(new Coordinator(), Policy(), async c => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(c, "done"); }, async client =>
            {
                var first = Send(client); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancel = new CancellationTokenSource(); var canceled = Send(client, token: cancel.Token); var sibling = Send(client); cancel.Cancel();
                try { await Assert.CatchAsync<OperationCanceledException>(async () => { using var result = await canceled; }); }
                finally { release.TrySetResult(); }
                using var a = await first; using var b = await sibling; Assert.That(await b.Content.ReadAsStringAsync(), Is.EqualTo("done")); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [Test]
        public async Task DuplicateWaitDeadlineConflictsWithoutRunningAgain()
        {
            var entered = Signal(); var release = Signal(); var calls = 0;
            await With(new Coordinator(), Policy(wait: TimeSpan.Zero), async c => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; await Write(c, "done"); }, async client =>
            {
                var first = Send(client); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                try { using var second = await Send(client); Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Conflict)); Assert.That(calls, Is.EqualTo(1)); }
                finally { release.TrySetResult(); }
                using var finished = await first;
            });
        }
        [TestCase("exception")]
        [TestCase("oversize")]
        [TestCase("cookie")]
        [TestCase("completion")]
        [TestCase("length")]
        public async Task UncertainOrUnrecordableOperationIsNotRepeated(string failure)
        {
            var calls = 0;
            await With(new Coordinator(), Policy(response: 8), async c =>
            {
                calls++;
                switch (failure)
                {
                    case "exception": throw new InvalidOperationException("committed then failed");
                    case "cookie": c.Response.Headers["Set-Cookie"] = "a=b"; break;
                    case "completion": c.OnRequestCompleted(() => Task.CompletedTask); break;
                    case "length": await Write(c, "short"); c.Response.ContentLength64 = 7; return;
                    default: await Write(c, "too-long-body"); return;
                }
                await Write(c, "done");
            }, async client =>
            {
                using var first = await Send(client); Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                using var retry = await Send(client); Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.Conflict)); Assert.That(calls, Is.EqualTo(1));
            });
        }
        [Test]
        public async Task CompletedRecordsExpireButActiveClaimsDoNot()
        {
            var store = new MemoryIdempotencyStore(); var retention = TimeSpan.FromMilliseconds(20); var token = CancellationToken.None;
            var first = await store.AcquireAsync("active", "body", retention, token); await Task.Delay(60);
            var active = await store.AcquireAsync("active", "body", retention, token); Assert.That(active.State, Is.EqualTo(IdempotencyState.InProgress)); Assert.That(active.OwnerToken, Is.Null);
            Assert.That(await store.CompleteAsync("active", first.OwnerToken ?? throw new InvalidOperationException(), new IdempotencyResponse(200, new byte[] { 1 }), token), Is.True);
            Assert.That((await store.AcquireAsync("active", "body", retention, token)).State, Is.EqualTo(IdempotencyState.Completed));
            await Task.Delay(60); Assert.That((await store.AcquireAsync("active", "body", retention, token)).State, Is.EqualTo(IdempotencyState.Acquired));
        }
        [Test]
        public async Task CapacityNeverEvictsUnexpiredResult()
        {
            var store = new MemoryIdempotencyStore(maxEntries: 1); var calls = 0;
            await With(new Coordinator(store), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); using var overflow = await Send(client, key: "other"); using var retry = await Send(client);
                Assert.That(overflow.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable)); Assert.That(await retry.Content.ReadAsStringAsync(), Is.EqualTo("1")); Assert.That(calls, Is.EqualTo(1)); Assert.That(store.Count, Is.EqualTo(1));
            });
        }
        [Test]
        public async Task StoreOwnershipCannotBeStolenOrTerminalStateReplaced()
        {
            var store = new MemoryIdempotencyStore(); var token = CancellationToken.None; var ttl = TimeSpan.FromMinutes(1);
            var first = await store.AcquireAsync("key", "body", ttl, token); var response = new IdempotencyResponse(200, new byte[] { 1 });
            Assert.That(await store.CompleteAsync("key", "wrong", response, token), Is.False); Assert.That(await store.FailAsync("key", "wrong", token), Is.False);
            Assert.That((await store.AcquireAsync("key", "other", ttl, token)).State, Is.EqualTo(IdempotencyState.Conflict));
            var owner = first.OwnerToken ?? throw new InvalidOperationException(); Assert.That(await store.CompleteAsync("key", owner, response, token), Is.True);
            Assert.That(await store.FailAsync("key", owner, token), Is.False); Assert.That(await store.CompleteAsync("key", owner, new IdempotencyResponse(500, Array.Empty<byte>()), token), Is.False);
            var completed = await store.AcquireAsync("key", "body", ttl, token); Assert.That(completed.Response?.StatusCode, Is.EqualTo(200));
            var body = completed.Response?.Body ?? throw new InvalidOperationException(); body[0] = 9; Assert.That(completed.Response?.Body[0], Is.EqualTo(1));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ControllerReplayRunsSecurityAndPreservesUnconfiguredActions(bool generated)
        {
            var state = new ControllerState(); var allowed = true; var module = new WebApiModule("/api", ResponseSerializer.None(false));
            module.ConfigureAuthorization(new AuthorizationOptions().AddPolicy("active", (_, _) => Task.FromResult(allowed)));
            module.ConfigureIdempotency(new Coordinator(), new Dictionary<string, IdempotencyPolicy> { ["writes"] = Policy() });
            if (generated) module.RegisterController(IdempotencyBindings.WriteController, () => new WriteController(state)); else module.RegisterController(() => new WriteController(state));
            await TestWebServer.UseAsync(server => server.WithModule(new PrincipalModule()).WithModule(module), async client =>
            {
                using var first = await Send(client, path: "/api/value");
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK), await first.Content.ReadAsStringAsync());
                using var second = await Send(client, path: "/api/value");
                Assert.That(await second.Content.ReadAsStringAsync(), Is.EqualTo("created-1")); Assert.That(state.Calls, Is.EqualTo(1));
                Assert.That(state.Hooks, Is.EqualTo(1)); Assert.That(state.Releases, Is.EqualTo(1));
                allowed = false; using var denied = await Send(client, path: "/api/value"); Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); Assert.That(state.Calls, Is.EqualTo(1));
                allowed = true;
                for (var i = 2; i <= 3; i++) { using var normal = await client.PostAsync("/api/normal", new StringContent("x")); Assert.That(await normal.Content.ReadAsStringAsync(), Is.EqualTo("normal-" + i)); Assert.That(normal.Headers.CacheControl?.NoStore, Is.True); }
                Assert.That(state.Hooks, Is.EqualTo(3));
            });
        }
        [TestCase(1)]
        [TestCase(4096)]
        public async Task RequestCapRejectsBeforeAcquiringOrExecuting(int bodyLength)
        {
            var calls = 0; var store = new MemoryIdempotencyStore();
            await With(new Coordinator(store), Policy(request: 1), c => Write(c, (++calls).ToString()), async client =>
            {
                using var result = await Send(client, body: new string('x', bodyLength + 1)); Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)); Assert.That(calls, Is.Zero); Assert.That(store.Count, Is.Zero);
            });
        }
        [Test]
        public async Task MissingKeyAndReadVerbNeverExecute()
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var missing = await client.PostAsync("/write", new StringContent("x")); using var get = await client.GetAsync("/write");
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest)); Assert.That(get.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest)); Assert.That(calls, Is.Zero);
            });
        }
        [Test]
        public async Task RequestSpecificGuardHeadersStayFreshOnReplay()
        {
            var requests = 0; var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            {
                using var first = await Send(client); using var retry = await Send(client);
                Assert.That(string.Join("", retry.Headers.GetValues("X-Request-Id")), Is.EqualTo("2")); Assert.That(calls, Is.EqualTo(1));
            }, c => c.Response.Headers["X-Request-Id"] = (++requests).ToString());
        }
        [Test]
        public async Task UndeclaredVaryFailsBeforeOperation()
        {
            var calls = 0;
            await With(new Coordinator(), Policy(), c => Write(c, (++calls).ToString()), async client =>
            { using var result = await Send(client); Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest)); Assert.That(calls, Is.Zero); }, c => c.Response.Headers["Vary"] = "X-Undeclared");
        }
        private sealed class PrincipalModule : WebModuleBase
        {
            internal PrincipalModule() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            { context.GetImplementation().User = new GenericPrincipal(new GenericIdentity("fixture", "test"), Array.Empty<string>()); return Task.CompletedTask; }
        }
        internal sealed class ControllerState { internal int Calls; internal int Hooks; internal int Releases; }
        [Idempotency("writes"), Authorize("active"), BeforeHandler(nameof(Prepare))]
        public sealed class WriteController : WebApiController, IDisposable
        {
            private readonly ControllerState _state;
            internal WriteController(ControllerState state) => _state = state;
            public Task Prepare() { PreProcessRequest(); _state.Hooks++; return Task.CompletedTask; }
            [Route(HttpVerbs.Post, "/value")]
            public async Task<string> Create() { _ = await HttpContext.GetRequestBodyAsStringAsync(); return "created-" + Interlocked.Increment(ref _state.Calls); }
            [Route(HttpVerbs.Post, "/normal"), Idempotency(null)]
            public string Normal() => "normal-" + Interlocked.Increment(ref _state.Calls);
            public void Dispose() => Interlocked.Increment(ref _state.Releases);
        }
    }
    [GeneratedControllerBinding(typeof(IdempotencyTest.WriteController))]
    internal static partial class IdempotencyBindings { }
}
