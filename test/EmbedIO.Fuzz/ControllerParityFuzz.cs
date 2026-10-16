using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Routing;
using EmbedIO.Testing;
using EmbedIO.WebApi;

// Differential registration, plus independent status/body/lifetime expectations.
internal static class ControllerParityFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        using var reflection = new TestWebServer();
        using var generated = new TestWebServer();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ledger = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);
        Configure(reflection, false); Configure(generated, true);
        var runs = new[] { reflection.RunAsync(stop.Token), generated.RunAsync(stop.Token) };
        var pending = new ConcurrentBag<Task>();
        try
        {
            // Fixed modes survive shrinking and guarantee coverage of every outcome.
            for (var mode = 0; mode < 12; mode++) await Batch(mode, mode, mode % 3).ConfigureAwait(false);
            foreach (var step in trace.Steps)
                await Batch(step.Command, step.Value, step.Actor).ConfigureAwait(false);
        }
        finally
        {
            foreach (var call in ledger.Values) call.Release.TrySetResult();
            stop.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await Task.WhenAll(pending).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            finally { await Task.WhenAll(runs).WaitAsync(cleanup.Token).ConfigureAwait(false); }
        }

        void Configure(TestWebServer server, bool useGenerated)
        {
            server.WithModule(new Gate(async context =>
            {
                var call = ledger[context.Request.Headers["X-Call"] ?? throw new InvalidOperationException("Missing call.")];
                context.OnClose(_ => { Interlocked.Increment(ref call.CloseCount); call.Closed.TrySetResult(); });
                call.Entered.TrySetResult();
                await call.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            }));
            server.WithModule(new BearerAuthenticationModule("/", (credential, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                var role = credential == "writer" ? "writer" : "reader";
                return Task.FromResult<ClaimsPrincipal?>(new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.Name, credential), new Claim(ClaimTypes.Role, role) }, "Bearer")));
            }));
            var api = new WebApiModule("/", ResponseSerializer.None(false));
            if (useGenerated)
                api.RegisterControllerWithContext(ParityBindings.ParityController, context => new ParityController(ledger, context.Request.Headers["X-Call"] ?? ""),
                    (_, controller) => { controller.Release(); return Task.CompletedTask; });
            else
                api.RegisterControllerWithContext(typeof(ParityController), context => new ParityController(ledger, context.Request.Headers["X-Call"] ?? ""),
                    (_, controller) => { ((ParityController)controller).Release(); return Task.CompletedTask; });
            server.WithModule(api);
        }

        async Task Batch(int mode, int value, int actor)
        {
            token.ThrowIfCancellationRequested();
            var id = actor + "-" + ledger.Count;
            var calls = Enumerable.Range(0, 6).Select(index => new Call(id + "-" + index, injectFault && index % 2 == 1)).ToArray();
            var tasks = calls.Select((call, index) =>
            {
                ledger.TryAdd(call.Id, call);
                var client = index % 2 == 0 ? reflection.Client : generated.Client;
                var task = Send(client, call, mode, value + index / 2 * 64);
                pending.Add(task);
                return task;
            }).ToArray();
            try
            {
                await Task.WhenAll(calls.Select(call => call.Entered.Task)).WaitAsync(token).ConfigureAwait(false);
                trace.Cover("controller.six-overlapping-dispatches");
            }
            finally { foreach (var call in calls.Reverse()) call.Release.TrySetResult(); }
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            Require(Enumerable.Range(0, 3).All(pair => results[pair * 2] == results[pair * 2 + 1]), "controller.registration-parity", id);
            trace.Cover("controller.mode-" + mode);
        }

        async Task<string> Send(HttpClient client, Call call, int mode, int value)
        {
            value = (value & 1) == 0 ? value : -value;
            var path = mode switch
            {
                0 => "sum/" + value + "?q=" + (value + 1),
                1 => "sum/not-an-int?q=1",
                2 => "sum/1?q=bad",
                3 => "form",
                4 or 5 => "json",
                6 => "async/" + value,
                7 => "http-error",
                8 => "exception",
                _ => "private"
            };
            using var request = new HttpRequestMessage(mode is 3 or 4 or 5 ? HttpMethod.Post : HttpMethod.Get, path);
            request.Headers.Add("X-Call", call.Id);
            if (mode >= 10) request.Headers.Add("Authorization", "Bearer " + (mode == 11 ? "writer" : "reader"));
            if (mode == 3) request.Content = new StringContent("value=" + value, Encoding.UTF8, "application/x-www-form-urlencoded");
            if (mode is 4 or 5) request.Content = new StringContent(mode == 4 ? "{\"Value\":" + value + "}" : "{bad", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var status = mode switch { 1 or 2 or 5 => 400, 7 => 409, 8 => 500, 9 => 401, 10 => 403, _ => 200 };
            Require((int)response.StatusCode == status, "controller.expected-status", mode + ": " + response.StatusCode);
            if (status == 200)
            {
                var expected = mode == 0 ? (value * 2 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : mode == 11 ? "writer" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Require(body == call.Id + ":" + expected, "controller.expected-body", mode + ": " + body);
                Require(call.Hooks == 1 && call.Invocations == 1, "controller.hook-invocation-once", call.Id);
            }
            if (mode >= 9 && mode <= 10)
                Require(call.Hooks == 0 && call.Invocations == 0 && call.Releases == 0, "controller.authorization-before-activation", call.Id);
            await call.Closed.Task.WaitAsync(token).ConfigureAwait(false);
            Require(call.CloseCount == 1 && call.Releases <= 1, "controller.context-lifetime", call.Id);
            if (status == 200) Require(call.Releases == 1, "controller.awaited-release", call.Id);
            // Error documents may contain contextual details; compare semantic outcomes/callback counts.
            return status + ":" + (status == 200 ? body[(call.Id.Length + 1)..] : "") + ":" + call.Hooks + ":" + call.Invocations + ":" + call.Releases;
        }
    }

    internal sealed class Call(string id, bool corrupt)
    {
        internal string Id { get; } = id;
        internal bool Corrupt { get; } = corrupt;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Hooks, Invocations, Releases, CloseCount;
    }
    private sealed class Gate(Func<IHttpContext, Task> callback) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;
        protected override Task OnRequestAsync(IHttpContext context) => callback(context);
    }
    internal static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}

[GeneratedControllerBinding(typeof(ParityController))]
internal static partial class ParityBindings { }

internal sealed class ParityController(ConcurrentDictionary<string, ControllerParityFuzz.Call> calls, string id) : WebApiController
{
    protected override void OnBeforeHandler() => Interlocked.Increment(ref calls[id].Hooks);
    internal void Release() => Interlocked.Increment(ref calls[id].Releases);
    private string Reply(string value) { Interlocked.Increment(ref calls[id].Invocations); return calls[id].Corrupt ? "corrupted" : id + ":" + value; }
    [Route(HttpVerbs.Get, "/sum/{value}")]
    public string Sum(int value, [QueryField(true)] int q) => Reply((value + q).ToString(System.Globalization.CultureInfo.InvariantCulture));
    [Route(HttpVerbs.Post, "/form")]
    public string Form([FormField(true)] int value) => Reply(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    [Route(HttpVerbs.Post, "/json")]
    public string Json([JsonData] ParityPayload value) => Reply(value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    [Route(HttpVerbs.Get, "/async/{value}")]
    public async Task<string> Async(int value) { await Task.Yield(); return Reply(value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
    [Route(HttpVerbs.Get, "/http-error")]
    public string Error() { Reply(""); throw new HttpException(409); }
    [Route(HttpVerbs.Get, "/exception")]
    public string Exception() { Reply(""); throw new InvalidOperationException("Injected controller error."); }
    [Route(HttpVerbs.Get, "/private"), Authorize(Roles = "writer")]
    public string Private() => Reply(HttpContext.User.Identity?.Name ?? "anonymous");
}
internal sealed class ParityPayload { public int Value { get; set; } }
