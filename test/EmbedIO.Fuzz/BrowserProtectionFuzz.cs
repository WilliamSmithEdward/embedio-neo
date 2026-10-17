using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Testing;

internal static class BrowserProtectionFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--browser-protection-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --browser-protection-selftest");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("browser-protection", 1729, Array.Empty<StateMachineStep>()), stop.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "browser.response-isolation")
        { Console.WriteLine("Rejected injected browser response corruption: " + failure.Invariant); return true; }
        throw new StateMachineFailure("browser.negative-oracle", "Injected corruption was not detected.");
    }

    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { TestWebServer.DefaultBaseUrl }, tokenLifetime: TimeSpan.FromDays(1)));
        using var foreign = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { TestWebServer.DefaultBaseUrl }));
        using var server = new TestWebServer(); using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var calls = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);
        var clients = Enumerable.Range(0, 3).Select(_ => TestHttpClient.Create(server)).ToArray();
        var users = new[] { "user0", "user1", "user2" }; var issuers = new[] { "issuer0", "issuer1", "issuer2" }; var tokens = new Saved?[3]; var sequence = 0;
        server.WithSessionManager(new LocalSessionManager());
        server.WithModule(new Gate(context =>
        {
            var user = context.Request.Headers["X-Model-User"] ?? "anonymous";
            var issuer = context.Request.Headers["X-Model-Issuer"] ?? "default";
            context.GetImplementation().User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, user, ClaimValueTypes.String, issuer) }, "model"));
            return Task.CompletedTask;
        }));
        server.WithModule(new ActionModule("/token", HttpVerbs.Get, context => context.SendStringAsync(protection.GetToken(context), "text/plain", WebServer.Utf8NoBomEncoding)));
        server.WithModule(new ActionModule("/foreign", HttpVerbs.Get, context => context.SendStringAsync(foreign.GetToken(context), "text/plain", WebServer.Utf8NoBomEncoding)));
        server.WithModule(new Gate(async context =>
        {
            if (!context.Request.Url.AbsolutePath.StartsWith("/guard", StringComparison.Ordinal)) return;
            var call = calls[context.Request.Headers["X-Model-Call"] ?? throw new InvalidOperationException("Missing call id.")];
            call.Entered.TrySetResult(); await call.Release.Task.WaitAsync(token).ConfigureAwait(false);
        }));
        server.WithModule(new BrowserRequestProtectionModule("/guard", protection));
        server.WithModule(new ActionModule("/guard", HttpVerbs.Any, context =>
        {
            var call = calls[context.Request.Headers["X-Model-Call"] ?? throw new InvalidOperationException("Missing call id.")]; Interlocked.Increment(ref call.Executions);
            return context.SendStringAsync(injectFault ? "corrupted" : call.Id, "text/plain", WebServer.Utf8NoBomEncoding);
        }));
        var running = server.RunAsync(stop.Token);
        try
        {
            for (var actor = 0; actor < 3; actor++) await Issue(actor);
            await Batch(0, 0);
            trace.Cover("browser.three-clients-overlap");
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested(); var actor = step.Actor;
                switch (step.Command % 8)
                {
                    case 0: await Issue(actor); trace.Cover("browser.issue"); break;
                    case 1: users[actor] = users[actor] == "changed" ? "user" + actor : "changed"; trace.Cover("browser.identity-change"); break;
                    case 2: clients[actor].Dispose(); clients[actor] = TestHttpClient.Create(server); trace.Cover("browser.fresh-session"); break;
                    case 4: issuers[actor] = issuers[actor] == "changed" ? "issuer" + actor : "changed"; trace.Cover("browser.issuer-change"); break;
                    case 3:
                        var other = (actor + 1) % 3;
                        clients[actor].Dispose(); clients[actor] = TestHttpClient.Create(server);
                        foreach (Cookie cookie in clients[other].CookieContainer.GetCookies(server.BaseUrl))
                            clients[actor].CookieContainer.Add(server.BaseUrl, new Cookie(cookie.Name, cookie.Value, cookie.Path));
                        trace.Cover("browser.share-cookie"); break;
                }
                await Batch(step.Command % 10, actor);
            }
        }
        finally
        {
            stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); foreach (var client in clients) client.Dispose();
        }
        string? Cookie(int actor) => clients[actor].CookieContainer.GetCookies(server.BaseUrl)[LocalSessionManager.DefaultCookieName]?.Value;
        async Task Issue(int actor)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/token"); request.Headers.Add("X-Model-User", users[actor]); request.Headers.Add("X-Model-Issuer", issuers[actor]);
            using var response = await clients[actor].SendAsync(request, token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            var value = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false); Require(value.Length == 119, "browser.bounded-token");
            tokens[actor] = new Saved(value, Cookie(actor) ?? throw new InvalidOperationException("Token issuance did not set a session cookie."), users[actor], issuers[actor]);
        }
        async Task Batch(int variant, int selected)
        {
            var pending = new List<Task>(); var batch = new List<Call>();
            for (var actor = 0; actor < 3; actor++)
            {
                var id = (sequence++).ToString(System.Globalization.CultureInfo.InvariantCulture); var call = new Call(id); calls[id] = call; batch.Add(call);
                pending.Add(Execute(actor, actor == selected ? variant : 0, call));
            }
            try { await Task.WhenAll(batch.Select(call => call.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
            finally
            {
                foreach (var call in batch) call.Release.TrySetResult();
                await Task.WhenAll(pending).ConfigureAwait(false);
                foreach (var call in batch) calls.TryRemove(call.Id, out _);
            }
        }
        async Task Execute(int actor, int variant, Call call)
        {
            var saved = tokens[variant == 1 ? (actor + 1) % 3 : actor];
            var value = saved?.Value; var accepted = saved != null && saved.Session == Cookie(actor) && saved.User == users[actor] && saved.Issuer == issuers[actor];
            if (variant == 2 && value != null) { value = "!" + value.Substring(1); accepted = false; }
            if (variant == 3) { value = null; accepted = false; }
            if (variant == 8)
            {
                using var issuing = new HttpRequestMessage(HttpMethod.Get, "/foreign"); issuing.Headers.Add("X-Model-User", users[actor]); issuing.Headers.Add("X-Model-Issuer", issuers[actor]);
                using var issued = await clients[actor].SendAsync(issuing, token).ConfigureAwait(false); issued.EnsureSuccessStatusCode(); value = await issued.Content.ReadAsStringAsync(token).ConfigureAwait(false); accepted = false;
            }
            using var request = new HttpRequestMessage(variant == 9 ? HttpMethod.Get : HttpMethod.Post, "/guard") { Content = variant == 9 ? null : new StringContent("payload") };
            request.Headers.Add("X-Model-User", users[actor]); request.Headers.Add("X-Model-Issuer", issuers[actor]); request.Headers.Add("X-Model-Call", call.Id);
            if (value != null) request.Headers.TryAddWithoutValidation(BrowserRequestProtection.TokenHeaderName, value);
            if (variant == 4) { request.Headers.Add("Origin", "http://evil.example"); accepted = false; }
            if (variant == 5) { request.Headers.Host = "evil.example:8080"; accepted = false; }
            if (variant == 6) { request.Headers.TryAddWithoutValidation(BrowserRequestProtection.TokenHeaderName, value ?? "missing"); accepted = false; }
            if (variant == 7) request.Headers.Add("Origin", "http://test.example.com:8080");
            if (variant == 9) accepted = true;
            using var response = await clients[actor].SendAsync(request, token).ConfigureAwait(false);
            Require(response.StatusCode == (accepted ? HttpStatusCode.OK : HttpStatusCode.Forbidden), "browser.independent-admission");
            Require(call.Executions == (accepted ? 1 : 0), "browser.side-effect-boundary");
            if (accepted) Require(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) == call.Id, "browser.response-isolation");
            trace.Cover(accepted ? "browser.allowed" : "browser.denied");
        }
    }
    private static void Require(bool condition, string invariant) { if (!condition) throw new StateMachineFailure(invariant, "Browser model invariant failed."); }
    private sealed record Saved(string Value, string Session, string User, string Issuer);
    private sealed class Call(string id)
    {
        internal string Id { get; } = id;
        internal int Executions;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Gate(Func<IHttpContext, Task> callback) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;
        protected override Task OnRequestAsync(IHttpContext context) => callback(context);
    }
}
