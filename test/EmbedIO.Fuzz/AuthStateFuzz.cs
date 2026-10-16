using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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

// Expected identities and permissions come from the generated request ledger,
// independently of the Authorization header parser and policy implementation.
internal static class AuthStateFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 0, token);
    internal static Task Http1Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 1, token);
    internal static Task Http2Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 2, token);

    private static async Task RunAsync(StateMachineTrace trace, int protocol, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var ledger = new Ledger(trace);
        var url = protocol == 0 ? TestWebServer.DefaultBaseUrl : Prefix();
        using IWebServer server = protocol == 0 ? new TestWebServer() : new WebServer(HttpListenerMode.EmbedIO, url);
        server.WithModule(new BearerAuthenticationModule("/api", ledger.ValidateAsync));
        var api = new WebApiModule("/", ResponseSerializer.None(false));
        api.ConfigureAuthorization(new AuthorizationOptions().AddPolicy("write", async (context, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            var id = context.Request.Headers["X-Fuzz-Request"] ?? throw new StateMachineFailure("auth.policy-request-id", "Missing request identity.");
            var request = ledger.Requests[id];
            request.PolicyEntered.TrySetResult();
            // Deliberately model a callback that can complete after cancellation.
            // The authorization boundary must still reject the late success.
            await request.PolicyRelease.Task.ConfigureAwait(false);
            var allowed = context.User is ClaimsPrincipal principal && principal.HasClaim("permission", "write");
            trace.Cover(allowed ? "auth.policy-allowed" : "auth.policy-denied");
            return allowed;
        }));
        api.RegisterController(() => new Controller(ledger));
        server.WithModule(api);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = server.RunAsync(stop.Token);
        var clients = Enumerable.Range(0, 3).Select(_ => Client(server, url, protocol)).ToArray();
        var all = new List<Request>();
        var sequence = 0;
        try
        {
            // Validators for distinct identities are simultaneously held. Release
            // order is commanded; completion scheduling is not part of the oracle.
            var overlap = new[] { Start(0, 8, true, true), Start(1, 8, true, true), Start(2, 8, true, true) };
            await Task.WhenAll(overlap.Select(EnteredAsync)).ConfigureAwait(false);
            trace.Cover("auth.three-validators-overlap");
            foreach (var request in overlap) request.Release.TrySetResult();
            await Task.WhenAll(overlap.Select(request => request.PolicyEntered.Task)).WaitAsync(token).ConfigureAwait(false);
            trace.Cover("auth.three-published-principals-overlap");
            await Task.WhenAll(overlap.Reverse().Select(FinishAsync)).ConfigureAwait(false);
            if (protocol == 0)
            {
                var canceledPolicy = Start(0, 8, false, true);
                await canceledPolicy.PolicyEntered.Task.WaitAsync(token).ConfigureAwait(false);
                canceledPolicy.Canceled = true;
                await canceledPolicy.Cancel.CancelAsync().ConfigureAwait(false);
                await FinishAsync(canceledPolicy).ConfigureAwait(false);
                trace.Cover("auth.pending-policy-cancellation");
            }
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var request = Start(step.Actor, step.Command, step.Command == 6);
                if (request.Canceled)
                {
                    await EnteredAsync(request).ConfigureAwait(false);
                    await request.Cancel.CancelAsync().ConfigureAwait(false);
                }
                await FinishAsync(request).ConfigureAwait(false);
            }

            // Mandatory reuse sequence verifies that missing credentials do not inherit
            // the last successful principal on any actor's persistent client connection.
            for (var actor = 0; actor < 3; actor++)
            {
                await FinishAsync(Start(actor, 0, false)).ConfigureAwait(false);
                await FinishAsync(Start(actor, 2, false)).ConfigureAwait(false);
                await FinishAsync(Start(actor, 5, false)).ConfigureAwait(false);
            }
            Check(ledger.Templates.All(principal => !principal.HasClaim(claim => claim.Type == "fuzz-request")),
                "auth.validator-principal-isolation", "A published identity mutated validator-owned claims.");
            foreach (var request in all)
            {
                Check(request.HandlerCalls == (!request.Canceled && request.ExpectedStatus == 200 ? 1 : 0), "auth.handler-admission", request.Id);
                if (protocol == 0 && (request.Canceled || request.ExpectedStatus != 200))
                    Check(request.Content.Reads == 0, "auth.rejection-before-body", request.Id);
            }
            trace.Cover(protocol == 0 ? "auth.inprocess-complete" : "auth.http" + protocol + "-complete");
        }
        finally
        {
            foreach (var request in all) { request.Release.TrySetResult(); request.PolicyRelease.TrySetResult(); }
            var cleanup = new List<Task> { stop.CancelAsync() };
            foreach (var request in all) cleanup.Add(RetireAsync(request));
            cleanup.Add(ObserveServerAsync());
            try { await Task.WhenAll(cleanup).ConfigureAwait(false); }
            finally { foreach (var client in clients) client.Dispose(); }
        }

        Request Start(int actor, int mode, bool held, bool heldPolicy = false)
        {
            var id = trace.Seed + "-" + sequence++;
            var request = new Request(id, actor, mode, protocol, token);
            all.Add(request);
            ledger.Requests[id] = request;
            ledger.Tokens[request.Token] = request;
            if (!held) request.Release.TrySetResult();
            if (!heldPolicy) request.PolicyRelease.TrySetResult();
            request.Message.RequestUri = new Uri(url + request.Path);
            if (protocol != 0)
            {
                request.Message.Version = protocol == 1 ? HttpVersion.Version11 : HttpVersion.Version20;
                request.Message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            }
            request.Response = clients[actor].SendAsync(request.Message, request.Cancel.Token);
            return request;
        }

        async Task EnteredAsync(Request request)
        {
            var completed = await Task.WhenAny(request.Entered.Task, request.Response).WaitAsync(token).ConfigureAwait(false);
            if (completed == request.Response)
            {
                using var response = await request.Response.ConfigureAwait(false);
                throw new StateMachineFailure("auth.validator-entry", request.Id + ": " + response.StatusCode);
            }
            await request.Entered.Task.WaitAsync(token).ConfigureAwait(false);
        }

        async Task FinishAsync(Request request)
        {
            request.Release.TrySetResult();
            request.PolicyRelease.TrySetResult();
            try
            {
                using var response = await request.Response.WaitAsync(token).ConfigureAwait(false);
                if (request.Canceled) throw new StateMachineFailure("auth.client-cancellation", request.Id);
                Check((int)response.StatusCode == request.ExpectedStatus, "auth.status", request.Id + ": " + response.StatusCode);
                if (request.ExpectedStatus == 200)
                {
                    var actual = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    Check(actual == request.ExpectedName + ":" + request.Id, "auth.response-identity", request.Id + ": " + actual);
                    if (protocol != 0) Check(response.Version.Major == protocol, "auth.protocol", response.Version.ToString());
                }
            }
            catch (OperationCanceledException) when (request.Canceled && !token.IsCancellationRequested)
            {
                trace.Cover("auth.validator-request-canceled");
            }
            // In-process SendAsync finishes after request cleanup; the network client
            // can finish first, so explicitly observe the separately owned validator.
            if (request.HasValidator) await request.Validated.Task.WaitAsync(token).ConfigureAwait(false);
            trace.Cover("auth.result-" + request.ExpectedStatus);
        }

        async Task ObserveServerAsync()
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task RetireAsync(Request request)
    {
        try
        {
            if (request.PendingResponse is { } pending)
            {
                using var response = await pending.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (request.Cancel.IsCancellationRequested) { }
        catch (HttpRequestException) when (request.Cancel.IsCancellationRequested) { }
        finally
        {
            try
            {
                if (request.Entered.Task.IsCompleted) await request.Validated.Task.ConfigureAwait(false);
            }
            finally { request.Dispose(); }
        }
    }

    private static HttpClient Client(IWebServer server, string url, int protocol)
    {
        if (server is TestWebServer test) return TestHttpClient.Create(test);
        return new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 })
        {
            BaseAddress = new Uri(url),
            DefaultRequestVersion = protocol == 1 ? HttpVersion.Version11 : HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static string Prefix()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return "http://127.0.0.1:" + ((IPEndPoint)(socket.LocalEndPoint ?? throw new InvalidOperationException("Missing endpoint."))).Port + "/";
    }

    private static void Check(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }

    private sealed class Ledger(StateMachineTrace trace)
    {
        internal ConcurrentDictionary<string, Request> Requests { get; } = new(StringComparer.Ordinal);
        internal ConcurrentDictionary<string, Request> Tokens { get; } = new(StringComparer.Ordinal);
        internal ClaimsPrincipal[] Templates { get; } = Enumerable.Range(0, 3).Select(actor => new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "actor" + actor), new Claim(ClaimTypes.Role, "editor"),
            new Claim("permission", actor == 1 ? "read" : "write"),
        }, "Bearer"))).ToArray();

        internal async Task<ClaimsPrincipal?> ValidateAsync(string token, CancellationToken cancellation)
        {
            if (!Tokens.TryGetValue(token, out var request)) return null;
            request.Entered.TrySetResult();
            try
            {
                await request.Release.Task.WaitAsync(cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if (!request.ValidToken) return null;
                trace.Cover("auth.validated-actor" + request.Actor);
                if (request.Mode is 1 or 7)
                    return new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "actor" + request.Actor), new Claim(ClaimTypes.Role, "reader") }, "Bearer"));
                return Templates[request.Actor];
            }
            finally { request.Validated.TrySetResult(); }
        }
    }

    private sealed class Controller(Ledger ledger) : WebApiController
    {
        [Route(HttpVerbs.Post, "/api/private")]
        [Authorize(Roles = "editor")]
        public string Private([FormData] NameValueCollection body) => Reply(body);

        [Route(HttpVerbs.Post, "/api/policy")]
        [Authorize("write")]
        public string Policy([FormData] NameValueCollection body) => Reply(body);

        [Route(HttpVerbs.Post, "/public")]
        [Route(HttpVerbs.Post, "/api/public")]
        [AllowAnonymous]
        public string Public([FormData] NameValueCollection body) => Reply(body);

        private string Reply(NameValueCollection body)
        {
            var id = HttpContext.Request.Headers["X-Fuzz-Request"] ?? throw new StateMachineFailure("auth.request-id", "Missing request identity.");
            var request = ledger.Requests[id];
            Interlocked.Increment(ref request.HandlerCalls);
            Check(body["id"] == id, "auth.body-isolation", id);
            var name = HttpContext.User.Identity?.IsAuthenticated == true ? HttpContext.User.Identity.Name : "anonymous";
            Check(name == request.ExpectedName, "auth.context-identity", id);
            if (HttpContext.User is ClaimsPrincipal principal && principal.Identity is ClaimsIdentity identity)
            {
                Check(!principal.HasClaim(claim => claim.Type == "fuzz-request"), "auth.fresh-principal", id);
                identity.AddClaim(new Claim("fuzz-request", id));
            }
            return name + ":" + id;
        }
    }

    private sealed class Request : IDisposable
    {
        private Task<HttpResponseMessage>? _response;
        internal Request(string id, int actor, int mode, int protocol, CancellationToken token)
        {
            Id = id; Actor = actor; Mode = mode;
            Token = "opaque-" + id;
            Canceled = mode == 6;
            ValidToken = mode is 0 or 1 or 7 or 8 || (mode == 6 && protocol == 0);
            HasValidator = mode is 0 or 1 or 3 or 6 or 7 or 8;
            ExpectedName = mode == 5 ? "anonymous" : "actor" + actor;
            ExpectedStatus = mode switch { 0 or 5 or 7 => 200, 1 => 403, 4 or 11 => 400, 8 => actor == 1 ? 403 : 200, _ => 401 };
            Path = mode == 5 ? "public" : mode == 7 ? "api/public" : mode == 8 ? "api/policy" : "api/private";
            if (mode == 9) Path += "?access_token=" + Token;
            Cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            Content = new ProbeContent(id);
            Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");
            Message = new HttpRequestMessage(HttpMethod.Post, (Uri?)null) { Content = Content };
            Message.Headers.Add("X-Fuzz-Request", id);
            if (HasValidator) Message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Token);
            if (mode == 4) Message.Headers.TryAddWithoutValidation("Authorization", "Bearer ?");
            if (mode == 10) Message.Headers.TryAddWithoutValidation("Authorization", "Basic Zm9vOmJhcg==");
            if (mode == 11) Message.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer a", "Bearer b" });
        }
        internal string Id { get; }
        internal int Actor { get; }
        internal int Mode { get; }
        internal string Token { get; }
        internal string Path { get; }
        internal bool ValidToken { get; }
        internal bool HasValidator { get; }
        internal bool Canceled { get; set; }
        internal string ExpectedName { get; }
        internal int ExpectedStatus { get; }
        internal ProbeContent Content { get; }
        internal HttpRequestMessage Message { get; }
        internal CancellationTokenSource Cancel { get; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Validated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PolicyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource PolicyRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<HttpResponseMessage> Response { get => _response ?? throw new InvalidOperationException("Request not sent."); set => _response = value; }
        internal Task<HttpResponseMessage>? PendingResponse => _response;
        internal int HandlerCalls;
        public void Dispose() { Message.Dispose(); Cancel.Dispose(); }
    }

    private sealed class ProbeContent(string id) : HttpContent
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes("id=" + id);
        internal int Reads;
        protected override bool TryComputeLength(out long length) { length = _bytes.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Increment(ref Reads);
            return stream.WriteAsync(_bytes, 0, _bytes.Length);
        }
    }
}
