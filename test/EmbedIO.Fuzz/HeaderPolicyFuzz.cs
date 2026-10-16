using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Cors;
using EmbedIO.Forwarding;
using EmbedIO.Testing;

// The request plans carry independent expected policies. Production parsers are
// used only by the server under test, never to calculate an expected result.
internal static class HeaderPolicyFuzz
{
    internal static Task ForwardedAsync(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, "forwarded", token, false);
    internal static Task CorsAsync(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, "cors", token, false);
    internal static Task ConditionalAsync(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, "conditional", token, false);

    private static async Task RunAsync(StateMachineTrace trace, string kind, CancellationToken token, bool injectFault)
    {
        using var server = new TestWebServer();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var calls = new ConcurrentDictionary<string, Call>(StringComparer.Ordinal);
        var pending = new List<Task>();
        var limit = 1 + (int)((uint)trace.Seed % 3);
        var sequence = 0;
        server.WithModule(new Gate(async context =>
        {
            var call = calls[context.Request.Headers["X-Policy-Call"] ?? throw new InvalidOperationException("Missing policy id.")];
            context.OnClose(_ => { Interlocked.Increment(ref call.Closes); call.Closed.TrySetResult(); });
            context.Response.Headers.Add("Vary", "Accept-Encoding");
            call.Entered.TrySetResult();
            await call.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
        }));
        if (kind == "forwarded")
        {
            const ForwardedHeaderSources sources = ForwardedHeaderSources.Forwarded | ForwardedHeaderSources.XForwardedFor
                | ForwardedHeaderSources.XForwardedProto | ForwardedHeaderSources.XForwardedHost;
            server.WithModule(new ForwardedHeadersModule("/trusted", sources) { ForwardLimit = limit }
                .WithTrustedProxies("127.0.0.1", "10.0.0.0/8").WithAllowedHosts("app.example", "[2001:db8::1]:8443"));
            server.WithModule(new ForwardedHeadersModule("/untrusted", sources).WithTrustedProxies("10.0.0.0/8").WithAllowedHosts("app.example"));
            server.WithModule(new ForwardedHeadersModule("/standard", ForwardedHeaderSources.Forwarded)
                .WithTrustedProxies("127.0.0.1").WithAllowedHosts("app.example"));
            server.WithModule(new ForwardedHeadersModule("/legacy", ForwardedHeaderSources.XForwardedFor | ForwardedHeaderSources.XForwardedProto | ForwardedHeaderSources.XForwardedHost)
                .WithTrustedProxies("127.0.0.1").WithAllowedHosts("app.example"));
        }
        if (kind == "cors")
        {
            server.WithModule(new CorsModule("/cors", "https://one.example,https://two.example", "X-Allowed", "GET,POST"));
            server.WithModule(new CorsModule("/wild"));
        }
        server.WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
        {
            var call = calls[context.Request.Headers["X-Policy-Call"] ?? throw new InvalidOperationException("Missing policy id.")];
            Interlocked.Increment(ref call.HandlerCalls);
            if (kind == "forwarded")
            {
                var value = context.GetForwardedRequest();
                var physical = value.PhysicalRemoteEndPoint.Equals(context.Request.RemoteEndPoint)
                    && context.Request.RemoteEndPoint.Address.Equals(IPAddress.Loopback)
                    && context.Request.Url.Scheme == "http" && !context.Request.IsSecureConnection;
                var result = value.Outcome + "|" + value.ClientAddress + "|" + value.Scheme + "|" + (value.Host ?? "")
                    + "|" + value.HopsApplied + "|" + value.HostRejected + "|" + physical;
                await context.SendStringAsync(injectFault ? "corrupted" : call.Id + "|" + result, "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
            }
            else if (kind == "cors")
                await context.SendStringAsync(injectFault ? "corrupted" : call.Id, "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
            else
            {
                var plan = call.Plan;
                var condition = context.Request.EvaluatePreconditions(plan.Tag, Modified, plan.Exists);
                var start = 0L; var length = plan.Total; var partial = false;
                if (condition == null)
                    partial = context.Request.TryGetByteRange(plan.Total, plan.Tag, Modified, out start, out length, plan.StrongDate);
                var result = (condition.HasValue ? ((int)condition.Value).ToString(CultureInfo.InvariantCulture) : "0")
                    + "|" + partial + "|" + start + "|" + length;
                context.Response.Headers["X-Selection"] = injectFault ? "corrupted" : result;
            }
        }));
        var running = server.RunAsync(stop.Token);
        try
        {
            for (var mode = 0; mode < 12; mode++) await Batch(mode, mode).ConfigureAwait(false);
            foreach (var step in trace.Steps) await Batch(step.Command, step.Value + step.Actor).ConfigureAwait(false);
            await Batch(0, 0).ConfigureAwait(false);
            trace.Cover(kind + ".healthy-followup");
        }
        finally
        {
            foreach (var call in calls.Values) call.Release.TrySetResult();
            stop.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await Task.WhenAll(pending).WaitAsync(cleanup.Token).ConfigureAwait(false); }
            finally { await running.WaitAsync(cleanup.Token).ConfigureAwait(false); }
        }

        async Task Batch(int mode, int value)
        {
            var group = Enumerable.Range(0, 3).Select(actor =>
            {
                var id = (++sequence).ToString(CultureInfo.InvariantCulture);
                return new Call(id, Plan.Create(kind, mode, value + actor, limit));
            }).ToArray();
            var tasks = group.Select(call => { calls.TryAdd(call.Id, call); var task = Send(call); pending.Add(task); return task; }).ToArray();
            try { await Task.WhenAll(group.Select((call, index) => EnteredAsync(call, tasks[index]))).WaitAsync(token).ConfigureAwait(false); }
            finally { foreach (var call in group.Reverse()) call.Release.TrySetResult(); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            trace.Cover(kind + ".three-entered-requests");
            trace.Cover(kind + ".mode-" + mode);
        }

        async Task EnteredAsync(Call call, Task sending)
        {
            var first = await Task.WhenAny(call.Entered.Task, sending).ConfigureAwait(false);
            if (first == sending && !call.Entered.Task.IsCompleted)
            {
                await sending.ConfigureAwait(false);
                throw new StateMachineFailure("policy.application-entry", call.Id);
            }
            await call.Entered.Task.ConfigureAwait(false);
        }

        async Task Send(Call call)
        {
            var plan = call.Plan;
            using var request = new HttpRequestMessage(new HttpMethod(plan.Method), plan.Path);
            request.Headers.Add("X-Policy-Call", call.Id);
            if (plan.Method == "QUERY")
            {
                request.Content = new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            foreach (var field in plan.Fields) request.Headers.TryAddWithoutValidation(field.Key, field.Value);
            using var response = await server.Client.SendAsync(request, token).ConfigureAwait(false);
            Require((int)response.StatusCode == plan.Status, kind + ".expected-status", call.Id + ": " + response.StatusCode);
            if (kind == "conditional" && plan.Status == 200)
                Require(Header(response, "X-Selection") == plan.Expected, "conditional.selection", call.Id);
            else if (plan.Status == 200)
                Require(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) == (plan.Downstream ? call.Id + plan.Expected : ""),
                    kind + ".expected-body", call.Id);
            if (kind == "cors")
            {
                foreach (var field in plan.ResponseFields)
                    Require(Header(response, field.Key) == field.Value, "cors.response-field", call.Id + ": " + field.Key);
                var vary = response.Headers.Vary.ToHashSet(StringComparer.OrdinalIgnoreCase);
                Require(vary.SetEquals(plan.Path == "/cors" ? new[] { "Accept-Encoding", "Origin" } : new[] { "Accept-Encoding" }),
                    "cors.vary-preservation", call.Id);
            }
            await call.Closed.Task.WaitAsync(token).ConfigureAwait(false);
            Require(call.Closes == 1 && call.HandlerCalls == (plan.Downstream ? 1 : 0), kind + ".context-isolation", call.Id);
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    private static readonly DateTimeOffset Modified = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private sealed class Plan
    {
        internal string Path = "/";
        internal string Method = "GET";
        internal int Status = 200;
        internal bool Downstream = true;
        internal string Expected = "";
        internal Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, string?> ResponseFields { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal long Total;
        internal string Tag = "\"selected\"";
        internal bool Exists = true;
        internal bool StrongDate;

        internal static Plan Create(string kind, int mode, int value, int limit)
            => kind == "forwarded" ? Forwarded(mode, value, limit) : kind == "cors" ? Cors(mode, value) : Conditional(mode, value);

        private static Plan Forwarded(int mode, int value, int limit)
        {
            var plan = new Plan { Path = mode == 8 ? "/untrusted" : mode == 11 ? "/none" : "/trusted" };
            var address = "203.0.113." + (1 + value % 200);
            var outcome = "Applied"; var client = address; var scheme = "https"; var host = "app.example"; var hops = 1; var rejected = false;
            switch (mode)
            {
                case 0: outcome = "NoHeaders"; break;
                case 1:
                    plan.Fields["Forwarded"] = "for=" + address + ";proto=https;host=app.example";
                    if ((value & 1) != 0) { plan.Path = "/legacy"; outcome = "NoHeaders"; }
                    break;
                case 2:
                    plan.Fields["X-Forwarded-For"] = address; plan.Fields["X-Forwarded-Proto"] = "https"; plan.Fields["X-Forwarded-Host"] = host;
                    if ((value & 1) != 0) { plan.Path = "/standard"; outcome = "NoHeaders"; }
                    break;
                case 3:
                    plan.Fields["Forwarded"] = "for=" + address + ";proto=http;host=app.example,for=10.0.0.2;proto=https;host=app.example,for=10.0.0.1;proto=https;host=app.example";
                    if (limit < 3) { client = limit == 1 ? "10.0.0.1" : "10.0.0.2"; hops = limit; }
                    else { scheme = "http"; hops = 3; }
                    break;
                case 4: plan.Fields["Forwarded"] = "for=198.51.100.1;proto=http,for=" + address + ";proto=https;host=app.example"; break;
                case 5:
                    plan.Fields["Forwarded"] = "for=" + address; plan.Fields["X-Forwarded-For"] = address; outcome = "Conflict";
                    if ((value & 1) != 0) { plan.Path = "/standard"; outcome = "Applied"; scheme = "http"; host = ""; }
                    break;
                case 6:
                    plan.Fields["Forwarded"] = value % 3 == 0 ? "for=\"unterminated"
                        : value % 3 == 1 ? string.Join(",", Enumerable.Repeat("for=" + address, 33))
                        : "for=" + address + ";x=\"" + new string('x', 8192) + "\"";
                    outcome = "Malformed"; break;
                case 7:
                    plan.Fields["Forwarded"] = "for=" + address + ";proto=https;host=evil.example"; host = ""; rejected = true; break;
                case 8: plan.Fields["Forwarded"] = "for=" + address + ";proto=https"; outcome = "UntrustedPeer"; break;
                case 9:
                    plan.Fields["X-Forwarded-For"] = address + ",10.0.0.1"; plan.Fields["X-Forwarded-Proto"] = "https";
                    host = ""; if (limit == 1) client = "10.0.0.1"; else hops = 2; break;
                case 10:
                    plan.Fields["Forwarded"] = "for=\"[2001:db8::2]:1234\";proto=https;host=\"[2001:db8::1]:8443\"";
                    client = "2001:db8::2"; host = "[2001:db8::1]:8443"; break;
                default: plan.Fields["Forwarded"] = "for=" + address; outcome = "NotProcessed"; break;
            }
            if (outcome != "Applied") { client = "127.0.0.1"; scheme = "http"; host = ""; hops = 0; rejected = false; }
            plan.Expected = "|" + outcome + "|" + client + "|" + scheme + "|" + host + "|" + hops + "|" + rejected + "|True";
            return plan;
        }

        private static Plan Cors(int mode, int value)
        {
            var plan = new Plan { Path = mode is 4 or 5 or 10 ? "/wild" : "/cors" };
            var origin = value % 2 == 0 ? "https://one.example" : "https://two.example";
            var preflight = mode is 2 or 3 or 5 or 6 or 8 or 10 or 11;
            if (preflight) plan.Method = "OPTIONS";
            if (mode is 1 or 4 or 6) origin = value % 2 == 0 ? "https://one.example.evil" : "https://one.example:8443";
            if (mode == 8) origin = origin.ToUpperInvariant();
            if (mode is not (7 or 10)) plan.Fields["Origin"] = origin;
            if (preflight && mode != 11) plan.Fields["Access-Control-Request-Method"] = mode == 3 ? "DELETE" : mode == 8 ? "pOsT" : mode == 5 ? "PATCH" : value % 2 == 0 ? "GET" : "POST";
            if (preflight || mode == 9) plan.Fields["Access-Control-Request-Headers"] = "x-request-" + value;
            var allowed = mode is not (1 or 6 or 7);
            plan.Downstream = !(preflight && allowed);
            if (mode == 3) plan.Status = 400;
            plan.ResponseFields["Access-Control-Allow-Origin"] = allowed ? plan.Path == "/wild" ? "*" : origin : null;
            plan.ResponseFields["Access-Control-Allow-Headers"] = preflight && allowed ? plan.Path == "/wild" ? plan.Fields["Access-Control-Request-Headers"] : "X-Allowed" : null;
            plan.ResponseFields["Access-Control-Allow-Methods"] = preflight && allowed && mode is not (3 or 11) ? plan.Fields["Access-Control-Request-Method"] : null;
            plan.ResponseFields["Access-Control-Allow-Credentials"] = null;
            plan.ResponseFields["Access-Control-Expose-Headers"] = null;
            return plan;
        }

        private static Plan Conditional(int mode, int value)
        {
            var plan = new Plan
            {
                Method = new[] { "GET", "HEAD", "QUERY", "POST", "OPTIONS" }[value % 5],
                Total = new[] { 0L, 1L, 127L, 128L, 4096L, long.MaxValue }[value % 6],
                Exists = (value & 2) == 0,
                Tag = (value & 1) == 0 ? "\"selected\"" : "W/\"selected\"",
                StrongDate = (value & 4) != 0,
            };
            var first = plan.Total == long.MaxValue ? (BigInteger)long.MaxValue - 2 : value % 131;
            var last = first + 1 + value;
            var suffix = new BigInteger(1 + value);
            var range = "bytes=" + first + "-" + last;
            var applicable = true; var condition = 0;
            switch (mode)
            {
                case 1: if (value % 4 == 0) suffix = 0; range = "bytes=-" + suffix; break;
                case 2: range = "bytes=" + first + "-"; last = (BigInteger)plan.Total - 1; break;
                case 3: first = (BigInteger)plan.Total + ((value & 8) != 0 ? BigInteger.Pow(10, 40) : BigInteger.Zero); range = "bytes=" + first + "-"; break;
                case 4: range = "items=0-1"; applicable = false; break;
                case 5: range = "bytes=9-1"; applicable = false; break;
                case 6: range = "bytes=0-1,3-4"; applicable = false; break;
                case 7:
                    if ((value & 1) == 0) { plan.Fields["If-Range"] = "\"selected\""; applicable = !plan.Tag.StartsWith("W/", StringComparison.Ordinal); }
                    else { plan.Fields["If-Range"] = "Thu, 02 Jan 2020 03:04:05 GMT"; applicable = plan.StrongDate; }
                    break;
                case 8:
                    plan.Fields["If-Match"] = value % 3 == 1 ? "*" : value % 3 == 0 ? "\"different\", \"selected\"" : "\"selected\"";
                    plan.Fields["If-Unmodified-Since"] = "Thu, 03 Jan 2019 00:00:00 GMT";
                    if (!plan.Exists || (value % 3 != 1 && plan.Tag.StartsWith("W/", StringComparison.Ordinal))) condition = 412;
                    break;
                case 9:
                    plan.Fields["If-None-Match"] = value % 3 == 0 ? "*" : value % 3 == 1 ? "\"different\", W/\"selected\"" : "W/\"selected\"";
                    if (plan.Exists) condition = plan.Method is "GET" or "HEAD" or "QUERY" ? 304 : 412;
                    break;
                case 10:
                    plan.Fields["If-None-Match"] = "\"different\""; plan.Fields["If-Modified-Since"] = "Sun, 03 Jan 2021 00:00:00 GMT";
                    break;
                case 11: plan.Fields["If-Match"] = "\"unterminated"; plan.Status = 400; break;
            }
            plan.Fields["Range"] = (value & 8) != 0 ? range.Replace("bytes=", " BYTES=", StringComparison.Ordinal) : range;
            if (plan.Method == "OPTIONS") { condition = 0; plan.Status = 200; }
            var partial = false; var start = BigInteger.Zero; var count = new BigInteger(plan.Total);
            if (condition == 0 && plan.Status == 200 && plan.Method is "GET" or "QUERY" && applicable && mode == 1 && suffix == 0) plan.Status = 416;
            if (condition == 0 && plan.Status == 200 && plan.Method is "GET" or "QUERY" && applicable && plan.Total != 0)
            {
                if (mode == 1) { count = BigInteger.Min(suffix, plan.Total); start = plan.Total - count; partial = true; }
                else if (first >= plan.Total) plan.Status = 416;
                else { start = first; count = BigInteger.Min(last, (BigInteger)plan.Total - 1) - first + 1; partial = true; }
            }
            plan.Expected = condition + "|" + partial + "|" + start + "|" + count;
            return plan;
        }
    }

    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--header-policy-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --header-policy-selftest");
        foreach (var kind in new[] { "forwarded", "cors", "conditional" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var invariant = kind == "conditional" ? "conditional.selection" : kind + ".expected-body";
            var rejected = false;
            try { await RunAsync(new StateMachineTrace(kind, 1729, Array.Empty<StateMachineStep>()), kind, deadline.Token, true).ConfigureAwait(false); }
            catch (StateMachineFailure failure) when (failure.Invariant == invariant) { rejected = true; }
            Require(rejected, "policy.negative-oracle", kind);
            Console.WriteLine("Rejected injected policy fault: " + kind + " => " + invariant);
        }
        return true;
    }

    private sealed class Call(string id, Plan plan)
    {
        internal string Id { get; } = id;
        internal Plan Plan { get; } = plan;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Closes, HandlerCalls;
    }
    private sealed class Gate(Func<IHttpContext, Task> callback) : WebModuleBase("/")
    {
        public override bool IsFinalHandler => false;
        protected override Task OnRequestAsync(IHttpContext context) => callback(context);
    }
    private static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}
