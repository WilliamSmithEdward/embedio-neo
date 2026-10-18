using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Idempotency;
using EmbedIO.Testing;
using Coordinator = EmbedIO.Idempotency.Idempotency;

// Three hosts coordinate through one atomic store. The oracle is an independent business ledger,
// never a reflection of store internals; externally visible effects are counted separately.
internal static class IdempotencyFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--idempotency-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --idempotency-selftest");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("idempotency", 1729, Array.Empty<StateMachineStep>()), stop.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "idempotency.result-isolation")
        { Console.WriteLine("Rejected injected foreign result: " + failure.Invariant); return true; }
        throw new StateMachineFailure("idempotency.negative-oracle", "Injected foreign response was not detected.");
    }
    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var store = new MemoryIdempotencyStore(maxEntries: 32, maxBytes: 32768, maxEntryBytes: 1024);
        var effects = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var ledger = new Dictionary<string, string>(StringComparer.Ordinal);
        var uncertain = new HashSet<string>(StringComparer.Ordinal);
        var entered = Signal(); var release = Signal();
        var hosts = Enumerable.Range(0, 3).Select(_ => new Host(store, effects, entered, release, token, injectFault)).ToArray();
        try
        {
            var owner = hosts[0].Send("overlap", "same", 0, false, token);
            await entered.Task.WaitAsync(token);
            var sibling = hosts[1].Send("overlap", "same", 0, false, token);
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var waiter = hosts[2].Send("overlap", "same", 0, false, canceled.Token); canceled.Cancel();
            try { await waiter; throw new StateMachineFailure("idempotency.waiter-cancellation", "Canceled waiter succeeded."); }
            catch (OperationCanceledException) { trace.Cover("idempotency.waiter-cancellation-isolation"); }
            release.TrySetResult();
            var values = await Task.WhenAll(owner, sibling);
            foreach (var value in values) Require(value.Status == HttpStatusCode.OK && value.Body == Result(0, "overlap", "same"), "idempotency.result-isolation");
            ledger.Add(Identity(0, "overlap"), "same"); trace.Cover("idempotency.cross-host-overlap");
            var failed = await hosts[0].Send("uncertain", "same", 0, true, token);
            Require(failed.Status == HttpStatusCode.InternalServerError, "idempotency.failure-visible"); uncertain.Add(Identity(0, "uncertain"));
            var replayFailure = await hosts[2].Send("uncertain", "same", 0, false, token);
            Require(replayFailure.Status == HttpStatusCode.Conflict, "idempotency.uncertain-not-reexecuted"); trace.Cover("idempotency.uncertain-outcome");
            var sequence = 0;
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested(); var host = hosts[step.Actor]; var caller = step.Actor;
                var key = "key-" + (step.Value % 6).ToString(CultureInfo.InvariantCulture); var body = "body-" + (step.Value % 2).ToString(CultureInfo.InvariantCulture);
                switch (step.Command % 5)
                {
                    case 0: await Probe(host, key, body, caller); trace.Cover("idempotency.lookup-and-new-write"); break;
                    case 1:
                        await Probe(host, key, body, caller); await Probe(hosts[(step.Actor + 1) % 3], key, body, caller);
                        trace.Cover("idempotency.cross-host-replay"); break;
                    case 2:
                        await Probe(host, key, body, caller); await Probe(host, key, body + "-mismatch", caller);
                        trace.Cover("idempotency.payload-mismatch"); break;
                    case 3:
                        await Probe(host, key, body, caller); await Probe(host, key, body, (caller + 1) % 3);
                        trace.Cover("idempotency.caller-isolation"); break;
                    default:
                        await Probe(host, "unique-" + (sequence++).ToString(CultureInfo.InvariantCulture), body, caller);
                        trace.Cover("idempotency.bounded-admission"); break;
                }
                Require(store.Count <= 32 && store.SizeBytes <= 32768, "idempotency.resident-bounds");
                Require(effects.All(pair => pair.Value == 1), "idempotency.no-duplicate-effects");
            }
            // Exhaust capacity without evicting any of the independent ledger's earlier outcomes.
            for (var i = 0; i < 33; i++) await Probe(hosts[i % 3], "capacity-" + i.ToString(CultureInfo.InvariantCulture), "fill", i % 3);
            Require(store.Count == 32, "idempotency.capacity-observed");
            foreach (var pair in ledger)
            {
                var split = pair.Key.IndexOf(':', StringComparison.Ordinal); var caller = int.Parse(pair.Key.Substring(0, split), CultureInfo.InvariantCulture);
                await Probe(hosts[(caller + 1) % 3], pair.Key.Substring(split + 1), pair.Value, caller);
            }
            Require(effects.Count == ledger.Count + uncertain.Count && effects.All(pair => pair.Value == 1), "idempotency.independent-effect-ledger");
            trace.Cover("idempotency.capacity-preserves-results");
        }
        finally { release.TrySetResult(); foreach (var host in hosts) await host.DisposeAsync(); }
        async Task Probe(Host host, string key, string body, int caller)
        {
            var id = Identity(caller, key); var exists = ledger.TryGetValue(id, out var prior);
            var full = !exists && ledger.Count + uncertain.Count >= 32;
            var response = await host.Send(key, body, caller, false, token);
            var expected = full ? HttpStatusCode.ServiceUnavailable : exists && prior != body ? HttpStatusCode.Conflict : HttpStatusCode.OK;
            Require(response.Status == expected, "idempotency.model-status");
            if (expected != HttpStatusCode.OK) return;
            Require(response.Body == Result(caller, key, body), "idempotency.result-isolation");
            if (!exists) ledger.Add(id, body);
        }
    }
    private static string Identity(int caller, string key) => caller.ToString(CultureInfo.InvariantCulture) + ":" + key;
    private static string Result(int caller, string key, string body) => Identity(caller, key) + "=" + body;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value, string invariant) { if (!value) throw new StateMachineFailure(invariant, "Independent idempotency model diverged."); }
    private sealed class Host : IAsyncDisposable
    {
        private readonly TestWebServer _server = new();
        private readonly CancellationTokenSource _stop;
        private readonly Task _running;
        internal Host(MemoryIdempotencyStore store, ConcurrentDictionary<string, int> effects, TaskCompletionSource entered, TaskCompletionSource release, CancellationToken token, bool corrupt)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var coordinator = new Coordinator(store);
            var policy = new IdempotencyPolicy("fuzz-v1", c => c.User.Identity?.Name ?? throw new InvalidOperationException(), TimeSpan.FromHours(1), maxResponseBytes: 256);
            _server.WithModule(new ActionModule(async context =>
            {
                var caller = int.Parse(context.Request.Headers["Test-Caller"] ?? "0", CultureInfo.InvariantCulture);
                context.GetImplementation().User = new GenericPrincipal(new GenericIdentity(caller.ToString(CultureInfo.InvariantCulture), "fixture"), Array.Empty<string>());
                await coordinator.ExecuteAsync(context, policy, async staged =>
                {
                    var key = staged.Request.Headers["Idempotency-Key"] ?? throw new InvalidOperationException();
                    var body = await staged.GetRequestBodyAsStringAsync(); var id = Identity(caller, key);
                    effects.AddOrUpdate(id, 1, (_, count) => count + 1);
                    if (key == "overlap") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
                    if (staged.Request.Headers["Test-Fail"] == "yes") throw new InvalidOperationException("Injected uncertain business outcome.");
                    var bytes = Encoding.UTF8.GetBytes(corrupt ? "foreign-result" : Result(caller, key, body));
                    staged.Response.ContentType = "text/plain"; staged.Response.ContentLength64 = bytes.Length;
                    await staged.Response.OutputStream.WriteAsync(bytes, token);
                });
            }));
            _running = _server.RunAsync(_stop.Token);
        }
        internal async Task<(HttpStatusCode Status, string Body)> Send(string key, string body, int caller, bool fail, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/write") { Content = new StringContent(body) };
            request.Headers.Add("Idempotency-Key", key); request.Headers.Add("Test-Caller", caller.ToString(CultureInfo.InvariantCulture));
            if (fail) request.Headers.Add("Test-Fail", "yes");
            using var response = await _server.Client.SendAsync(request, token);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(token));
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); await _running; _server.Dispose(); _stop.Dispose(); }
    }
}
