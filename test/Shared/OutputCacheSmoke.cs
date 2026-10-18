using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Caching;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.PlatformTests
{
    internal static class OutputCacheSmoke
    {
        internal sealed class State
        {
            internal readonly OutputCache Cache = new OutputCache();
            internal int Calls;
            internal int Hooks;
            internal int Releases;
        }
        internal static State Configure(WebServer server)
        {
            var state = new State();
            var module = new WebApiModule("/output-cache", ResponseSerializer.None(false));
            module.ConfigureOutputCaching(state.Cache, new Dictionary<string, OutputCachePolicy>
            { ["smoke"] = new OutputCachePolicy(TimeSpan.FromMinutes(1), tags: new[] { "smoke" }) });
#if NETFRAMEWORK
            // Legacy consumers use the reflection registration; AOT must use the generated binding.
            module.RegisterController(() => new CacheSmokeController(state));
#else
            module.RegisterController(CacheSmokeBindings.CacheSmokeController, () => new CacheSmokeController(state));
#endif
            server.WithModule(module); return state;
        }
        internal static async Task RunAsync(string url, State state)
        {
            using var handler = new HttpClientHandler { UseCookies = false, UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var value = url + "output-cache/value?__context=literal";
            if (await client.GetStringAsync(value) != "literal:1" || await client.GetStringAsync(value) != "literal:1")
                throw new InvalidOperationException("Output-cache snapshot or generated query binding failed.");
            if (state.Calls != 1 || state.Hooks != 2 || state.Cache.Count != 1) throw new InvalidOperationException("Output-cache hook/hit accounting failed.");
            state.Cache.InvalidateTag("smoke");
            if (await client.GetStringAsync(value) != "literal:2") throw new InvalidOperationException("Output-cache invalidation failed.");
            for (var i = 3; i <= 4; i++)
            {
                using var response = await client.GetAsync(url + "output-cache/uncached");
                if (await response.Content.ReadAsStringAsync() != "uncached:" + i || response.Headers.CacheControl?.NoStore != true)
                    throw new InvalidOperationException("Uncached controller behavior changed.");
            }
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref state.Releases) < 5 && DateTime.UtcNow < deadline) await Task.Delay(10);
            if (state.Hooks != 5 || state.Calls != 4 || state.Releases != 5) throw new InvalidOperationException("Output-cache controller release failed.");
        }
    }

#if !NETFRAMEWORK
    [GeneratedControllerBinding(typeof(CacheSmokeController))]
    internal static partial class CacheSmokeBindings { }
#endif
    [OutputCache("smoke"), BeforeHandler(nameof(Prepare))]
    internal sealed class CacheSmokeController : WebApiController, IDisposable
    {
        private readonly OutputCacheSmoke.State _state;
        internal CacheSmokeController(OutputCacheSmoke.State state) => _state = state;
        public Task Prepare() { Interlocked.Increment(ref _state.Hooks); return Task.CompletedTask; }
        [Route(HttpVerbs.Get, "/value")]
        public Task<string> Value([QueryField("__context")] string text)
            => Task.FromResult(text + ":" + Interlocked.Increment(ref _state.Calls));
        [Route(HttpVerbs.Get, "/uncached"), OutputCache(null)]
        public Task<string> Uncached() => Value("uncached");
        public void Dispose() => Interlocked.Increment(ref _state.Releases);
    }
}
