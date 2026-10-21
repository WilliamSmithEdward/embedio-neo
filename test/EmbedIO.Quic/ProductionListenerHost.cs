using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.Quic
{
    internal static class ProductionListenerHost
    {
        internal static Task<int> RunAsync(string library, string certificate, string key)
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return RunSupportedAsync(library, certificate, key);

            throw new PlatformNotSupportedException("HTTP/3 validation requires a desktop platform.");
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<int> RunSupportedAsync(string library, string certificatePath, string key)
        {
            using var provider = new DevelopmentTlsProvider(library, certificatePath, key);
            using var imported = provider.NativeCertificate == null
                ? X509Certificate2.CreateFromPemFile(certificatePath, key)
                : null;
            var source = new ServerCertificateSource(
                provider.NativeCertificate
                    ?? imported
                    ?? throw new InvalidOperationException("Missing development certificate.")
            );
            await using var binding = new EmbedIOQuicListenerBinding(
                new IPEndPoint(IPAddress.Loopback, 0),
                parameters => provider.Create(parameters, "h3")
            );
            var endpoint = binding.LocalEndPoint;
            var options = new WebServerOptions()
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithUrlPrefix($"https://127.0.0.1:{endpoint.Port}/")
                .WithCertificateSource(source);
            var bound = false;
            using var server = new WebServer(
                options,
                bodyPolicy => new Http3Listener(
                    source,
                    false,
                    bodyPolicy,
                    (requested, certificates, policy, native) =>
                    {
                        // This host validates the real pipeline with a development credential.
                        // It must reject configurations its TLS fixture cannot honor.
                        if (
                            bound
                            || !requested.Equals(endpoint)
                            || !ReferenceEquals(source, certificates)
                            || policy != null
                            || native
                        )
                            throw new NotSupportedException("Unsupported development listener configuration.");

                        bound = true;
                        return binding;
                    }
                )
            );
            var state = new ProductionListenerState();
            var module = new WebApiModule("/", ResponseSerializer.None(false));
            module.ConfigureAuthorization(
                new AuthorizationOptions().AddPolicy(
                    "read",
                    (context, _) => Task.FromResult(context.User.IsInRole("reader"))
                )
            );
            module.RegisterController(() => new ProductionListenerController(state));
            server
                .WithModule(new BearerAuthenticationModule("/", (token, _) =>
                    {
                        var principal = token is "reader" or "writer"
                            ? new ClaimsPrincipal(
                                new ClaimsIdentity(
                                    [
                                        new Claim(ClaimTypes.Name, token),
                                        new Claim(ClaimTypes.Role, token)
                                    ],
                                    "development"
                                )
                            )
                            : null;
                        return Task.FromResult(principal);
                    }))
                .WithModule(module);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) started.TrySetResult();
            };
            var running = server.RunAsync(stop.Token);

            try
            {
                await Task
                    .WhenAny(started.Task, running)
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);

                if (!started.Task.IsCompletedSuccessfully)
                {
                    await running.ConfigureAwait(false);
                    throw new InvalidOperationException("Server stopped before readiness.");
                }

                var ready = new
                {
                    ready = true,
                    port = endpoint.Port
                };
                Console.WriteLine(JsonSerializer.Serialize(ready));
                var command = await Console.In
                    .ReadLineAsync(stop.Token)
                    .ConfigureAwait(false);

                if (command != "drain") throw new InvalidDataException("Expected drain command.");

                await state.DrainEntered.Task
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
                // Prove that ending acceptance retains UDP for an already dispatched response.
                var drain = server.DrainAsync(TimeSpan.FromSeconds(5), stop.Token);

                while (binding.IsAccepting)
                    await Task
                        .Delay(1, stop.Token)
                        .ConfigureAwait(false);

                state.DrainRelease.TrySetResult();
                await drain.ConfigureAwait(false);
                await running
                    .WaitAsync(stop.Token)
                    .ConfigureAwait(false);
            }

            finally
            {
                stop.Cancel();
                state.DrainRelease.TrySetResult();
                await running.ConfigureAwait(false);
            }

            await binding
                .DisposeAsync()
                .ConfigureAwait(false);

            await using (var rebound = new EmbedIOQuicListenerBinding(
                endpoint,
                parameters => provider.Create(parameters, "h3")
            ))
                QuicFuzzChecks.Require(rebound.LocalEndPoint.Equals(endpoint), "production-listener-rebind");

            var report = new
            {
                completed = true,
                aborted = state.Aborted.Count,
                drainCompleted = state.DrainCompleted,
                rebind = true,
                healthRequests = state.HealthRequests,
                numberRequests = state.NumberRequests,
                secureRequests = state.SecureRequests
            };
            Console.WriteLine(JsonSerializer.Serialize(report));
            return 0;
        }
    }

    internal sealed class ProductionListenerState
    {
        internal ConcurrentDictionary<int, bool> Started { get; } = new();
        internal ConcurrentDictionary<int, bool> Aborted { get; } = new();
        internal TaskCompletionSource DrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DrainRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool DrainCompleted;
        internal int HealthRequests;
        internal int NumberRequests;
        internal int SecureRequests;
    }

    internal sealed class ProductionListenerController(ProductionListenerState state) : WebApiController
    {
        [Route(HttpVerbs.Get, "/health")]
        public TextHttpResult Health()
        {
            Interlocked.Increment(ref state.HealthRequests);
            return HttpResults.Text("controller HTTP/3");
        }

        [Route(HttpVerbs.Get, "/number/{value}")]
        public TextHttpResult Number(int value)
        {
            Interlocked.Increment(ref state.NumberRequests);
            return HttpResults.Text(value.ToString(CultureInfo.InvariantCulture));
        }

        [Route(HttpVerbs.Get, "/secure")]
        [Authorize("read")]
        public TextHttpResult Secure()
        {
            Interlocked.Increment(ref state.SecureRequests);
            return HttpResults.Text(User?.Identity?.Name ?? "missing");
        }

        [Route(HttpVerbs.Get, "/metadata")]
        public TextHttpResult Metadata()
        {
            var local = Request.LocalEndPoint;
            var remote = Request.RemoteEndPoint;
            QuicFuzzChecks.Require(
                local.Address.Equals(IPAddress.Loopback)
                    && local.Port > 0
                    && remote.Address.Equals(IPAddress.Loopback)
                    && remote.Port > 0,
                "request-endpoints"
            );
            var result = $"{local}|{remote}";
            local.Port = 1;
            remote.Port = 2;
            return HttpResults.Text(result);
        }

        [Route(HttpVerbs.Post, "/echo")]
        public async Task<FileHttpResult> Echo()
        {
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;

            while (
                (count = await Request.InputStream
                    .ReadAsync(buffer, CancellationToken)
                    .ConfigureAwait(false))
                != 0
            )
            {
                if (body.Length + count > 262144) throw new InvalidDataException("Fixture body limit exceeded.");

                body.Write(buffer, 0, count);
            }

            return HttpResults.Bytes(body.ToArray());
        }

        [Route(HttpVerbs.Post, "/timeout")]
        public async Task<TextHttpResult> TimeoutBody()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, CancellationToken);

            try
            {
                var bytes = new byte[1024];

                while (
                    await Request.InputStream
                        .ReadAsync(bytes, linked.Token)
                        .ConfigureAwait(false)
                    != 0
                ) { }

                throw new InvalidOperationException("Timeout fixture received a complete upload.");
            }

            catch (OperationCanceledException) when (
                timeout.IsCancellationRequested && !CancellationToken.IsCancellationRequested
            )
            {
                return HttpResults.Text("complete timeout response", "text/plain", 408);
            }
        }

        [Route(HttpVerbs.Get, "/blocked/{id}")]
        public async Task<TextHttpResult> Blocked(int id)
        {
            var token = HttpContext.GetRequestAbortedToken();
            state.Started.TryAdd(id, true);

            try
            {
                await Task
                    .Delay(Timeout.Infinite, token)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                state.Aborted.TryAdd(id, true);
            }

            return HttpResults.Text("aborted");
        }

        [Route(HttpVerbs.Get, "/state/{id}")]
        public TextHttpResult State(int id) =>
            HttpResults.Text(
                state.Aborted.ContainsKey(id) ? "aborted" : state.Started.ContainsKey(id) ? "started" : "waiting"
            );

        [Route(HttpVerbs.Get, "/drain")]
        public async Task<TextHttpResult> Drain()
        {
            state.DrainEntered.TrySetResult();
            await state.DrainRelease.Task
                .WaitAsync(CancellationToken)
                .ConfigureAwait(false);
            state.DrainCompleted = true;
            return HttpResults.Text(new string('d', 131072));
        }
    }
}
