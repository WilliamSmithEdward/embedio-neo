using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Idempotency;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.PlatformTests
{
    internal static class IdempotencySmoke
    {
        internal sealed class State { internal int Calls; }
        internal static State Configure(WebServer server)
        {
            var state = new State(); var module = new WebApiModule("/idempotency", ResponseSerializer.None(false));
            module.ConfigureIdempotency(new Idempotency.Idempotency(), new Dictionary<string, IdempotencyPolicy>
            { ["writes"] = new IdempotencyPolicy("smoke", _ => "single-loopback-fixture", TimeSpan.FromMinutes(1)) });
#if NETFRAMEWORK
            module.RegisterController(() => new IdempotentSmokeController(state));
#else
            module.RegisterController(IdempotencySmokeBindings.IdempotentSmokeController, () => new IdempotentSmokeController(state));
#endif
            server.WithModule(module); return state;
        }
        internal static async Task<int> RunAsync(string url, State state)
        {
            using var handler = new HttpClientHandler { UseCookies = false, UseProxy = false }; using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using (var first = await Send("same", true))
                if (first.StatusCode != HttpStatusCode.OK || await first.Content.ReadAsStringAsync() != "created-1") throw new InvalidOperationException("Idempotency first response failed.");
            using (var retry = await Send("same", true))
                if (retry.StatusCode != HttpStatusCode.OK || await retry.Content.ReadAsStringAsync() != "created-1" || state.Calls != 1) throw new InvalidOperationException("Idempotency replay repeated the operation.");
            using (var conflict = await Send("different", true))
                if (conflict.StatusCode != HttpStatusCode.Conflict || state.Calls != 1) throw new InvalidOperationException("Idempotency fingerprint isolation failed.");
            for (var i = 2; i <= 3; i++)
                using (var normal = await Send("same", false))
                    if (normal.StatusCode != HttpStatusCode.OK || await normal.Content.ReadAsStringAsync() != "normal-" + i || normal.Headers.CacheControl?.NoStore != true)
                        throw new InvalidOperationException("Unconfigured write behavior changed.");
            return 4;
            async Task<HttpResponseMessage> Send(string body, bool protectedRoute)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url + "idempotency/" + (protectedRoute ? "value" : "normal")) { Content = new StringContent(body) };
                if (protectedRoute) request.Headers.Add("Idempotency-Key", "one"); return await client.SendAsync(request);
            }
        }
    }
#if !NETFRAMEWORK
    [GeneratedControllerBinding(typeof(IdempotentSmokeController))]
    internal static partial class IdempotencySmokeBindings { }
#endif
    [Idempotency("writes")]
    internal sealed class IdempotentSmokeController : WebApiController
    {
        private readonly IdempotencySmoke.State _state;
        internal IdempotentSmokeController(IdempotencySmoke.State state) => _state = state;
        [Route(HttpVerbs.Post, "/value")]
        public async Task<string> Create() { _ = await HttpContext.GetRequestBodyAsStringAsync(); return "created-" + Interlocked.Increment(ref _state.Calls); }
        [Route(HttpVerbs.Post, "/normal"), Idempotency(null)]
        public string Normal() => "normal-" + Interlocked.Increment(ref _state.Calls);
    }
}
