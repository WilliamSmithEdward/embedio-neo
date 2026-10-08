using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.PlatformTests
{
    public static class BasicAuthenticationSmoke
    {
        public static Task<int> RunAsync(HttpListenerMode mode, Uri url, bool layered)
            => RunAsync(mode, url?.OriginalString ?? throw new ArgumentNullException(nameof(url)), layered);

        public static async Task<int> RunAsync(HttpListenerMode mode, string url, bool layered)
        {
            using var stop = new CancellationTokenSource();
            var state = new State();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithLocalSessionManager();
            BasicAuthenticationModule Authentication(string realm) => new BasicAuthenticationModule("/", realm)
                .WithAccount("user", "password").WithAccount("élève", "pass:word");
            if (layered)
                server.WithModule(Authentication("first"));
            var realm = layered ? "neo test" : "/";
            server.WithModule(Authentication(realm))
                .WithWebApi("/api", m => m.WithController(() => new ProbeController(state)));
            using var client = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            { Timeout = TimeSpan.FromSeconds(10) };
            var running = server.RunAsync(stop.Token);
            var requests = 0;
            try
            {
                async Task Check(HttpMethod method, string? header, bool authorized)
                {
                    var before = Volatile.Read(ref state.Calls);
                    using var request = new HttpRequestMessage(method, url + "api/probe");
                    if (header != null) request.Headers.TryAddWithoutValidation("Authorization", header);
                    if (method == HttpMethod.Post) request.Content = new ByteArrayContent(Array.Empty<byte>());
                    using var response = await client.SendAsync(request).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.StatusCode != (authorized ? HttpStatusCode.OK : HttpStatusCode.Unauthorized))
                        throw new InvalidOperationException("Unexpected auth status: " + (int)response.StatusCode + "; " + body);
                    var values = response.Headers.GetValues("WWW-Authenticate");
                    var expectedRealm = layered && !authorized ? "first" : realm;
                    var count = 0;
                    foreach (var value in values)
                    {
                        if (value != "Basic realm=\"" + expectedRealm + "\" charset=UTF-8")
                            throw new InvalidOperationException("Unexpected challenge: " + value);
                        count++;
                    }
                    if (count != 1 || state.Calls != before + (authorized ? 1 : 0) || (authorized && body != "true"))
                        throw new InvalidOperationException("Challenge replacement or protected-handler isolation failed.");
                    requests++;
                }
                string Basic(string pair, string scheme = "Basic") => scheme + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(pair));
                await Check(HttpMethod.Post, Basic("user:password"), true).ConfigureAwait(false);
                await Check(HttpMethod.Get, null, false).ConfigureAwait(false);
                await Check(HttpMethod.Post, Basic("user:wrong"), false).ConfigureAwait(false);
                await Check(HttpMethod.Get, "Basic %%%", false).ConfigureAwait(false);
                await Check(HttpMethod.Get, "Basic", false).ConfigureAwait(false);
                await Check(HttpMethod.Get, "Bearer token", false).ConfigureAwait(false);
                await Check(HttpMethod.Get, Basic("missing:password"), false).ConfigureAwait(false);
                await Check(HttpMethod.Get, Basic("user:password", "bAsIc"), true).ConfigureAwait(false);
                await Check(HttpMethod.Post, Basic("élève:pass:word"), true).ConfigureAwait(false);
                await Check(HttpMethod.Get, Basic("user:password"), true).ConfigureAwait(false);
                return requests;
            }
            finally
            {
                stop.Cancel();
                if (await Task.WhenAny(running, Task.Delay(10000)).ConfigureAwait(false) != running)
                    throw new TimeoutException("Authentication fixture shutdown timed out.");
                await running.ConfigureAwait(false);
            }
        }

        public sealed class State { public int Calls; }
        public sealed class ProbeController : WebApiController
        {
            private readonly State _state;
            public ProbeController(State state) => _state = state;
            [Route(HttpVerbs.Post, "/probe")]
            [Route(HttpVerbs.Get, "/probe")]
            public async Task<bool> Probe()
            {
                Interlocked.Increment(ref _state.Calls);
                return await Task.FromResult(true).ConfigureAwait(false);
            }
        }
    }
}
