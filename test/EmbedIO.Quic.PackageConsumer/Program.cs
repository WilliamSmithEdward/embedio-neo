using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbedIO;
using EmbedIO.Authentication;
using EmbedIO.Authorization;
using EmbedIO.Net;
using EmbedIO.Net.Internal.Http3;
using EmbedIO.Quic;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.Quic.PackageConsumer
{
    internal static class Program
    {
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<int> Main(string[] arguments)
        {
            if (arguments.Length != 2) throw new ArgumentException("Supply the owned certificate and private key.");

            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();

            using var certificate = X509Certificate2.CreateFromPemFile(arguments[0], arguments[1]);
            var source = new ServerCertificateSource(certificate);
            await using var binding = new EmbedIOQuicListenerBinding(
                new IPEndPoint(IPAddress.Loopback, 0),
                EmbedIOQuicTls.CreateFactory(source, null)
            );
            var endpoint = binding.LocalEndPoint;
            var options = new WebServerOptions()
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithUrlPrefix($"https://127.0.0.1:{endpoint.Port}/")
                .WithCertificateSource(source);
            using var server = new WebServer(
                options,
                body => new Http3Listener(source, false, body, (requested, certificates, policy, native) =>
                {
                    if (!requested.Equals(endpoint) || !ReferenceEquals(source, certificates) || policy != null || native)
                        throw new InvalidOperationException("Unexpected package-consumer listener configuration.");

                    return binding;
                })
            );
            var module = new WebApiModule("/", ResponseSerializer.None(false));
            module.ConfigureAuthorization(
                new AuthorizationOptions().AddPolicy(
                    "read",
                    (context, _) => Task.FromResult(context.User.IsInRole("reader"))
                )
            );
            module.RegisterController(PackageControllers.ConsumerController);
            server
                .WithModule(
                    new BearerAuthenticationModule(
                        "/",
                        (token, _) => Task.FromResult<ClaimsPrincipal?>(
                            token == "reader"
                                ? new ClaimsPrincipal(
                                    new ClaimsIdentity([new Claim(ClaimTypes.Role, "reader")], "fixture")
                                )
                                : null
                        )
                    )
                )
                .WithModule(module);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) started.TrySetResult();
            };
            var running = server.RunAsync(deadline.Token);

            try
            {
                await Task
                    .WhenAny(started.Task, running)
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);

                if (!started.Task.IsCompletedSuccessfully)
                {
                    await running.ConfigureAwait(false);
                    throw new InvalidOperationException("Consumer ended before readiness.");
                }

                Console.WriteLine(
                    JsonSerializer.Serialize(new ReadyReport { Port = endpoint.Port }, ConsumerJson.Default.ReadyReport)
                );

                if (
                    await Console.In
                        .ReadLineAsync(deadline.Token)
                        .ConfigureAwait(false)
                    != "stop"
                )
                    throw new InvalidDataException("Expected the peer's completion command.");

                await server
                    .DrainAsync(TimeSpan.FromSeconds(5), deadline.Token)
                    .ConfigureAwait(false);
                await running.ConfigureAwait(false);
            }

            finally
            {
                deadline.Cancel();
                await running.ConfigureAwait(false);
            }

            await binding
                .DisposeAsync()
                .ConfigureAwait(false);

            if (OpenSslTls.ActiveContexts != 0 || OpenSslTls.ActiveBlocks != 0 || ConsumerController.Aborted != 1)
                throw new InvalidOperationException(
                    "Package consumer did not release its transport/application state."
                );

            Console.WriteLine(
                JsonSerializer.Serialize(
                    new ConsumerReport
                    {
                        Passed = true,
                        Aborted = 1,
                        Contexts = 0,
                        Blocks = 0
                    },
                    ConsumerJson.Default.ConsumerReport
                )
            );
            return 0;
        }
    }

    internal sealed class ConsumerController : WebApiController
    {
        internal static int Aborted;

        [Route(HttpVerbs.Get, "/health")]
        public string Health() => PlainText("package HTTP/3");

        [Route(HttpVerbs.Get, "/secure")]
        [Authorize("read")]
        public string Secure() => PlainText((User as ClaimsPrincipal)?.FindFirst(ClaimTypes.Role)?.Value ?? "missing");

        [Route(HttpVerbs.Post, "/echo")]
        public async Task Echo([RequestAborted] CancellationToken cancellationToken)
        {
            var bytes = new byte[131073];
            var count = 0;

            while (count < bytes.Length)
            {
                var read = await Request.InputStream
                    .ReadAsync(bytes.AsMemory(count), cancellationToken)
                    .ConfigureAwait(false);

                if (read == 0) break;

                count += read;
            }

            if (count > 131072) throw new HttpException(413);

            Response.ContentLength64 = count;
            Response.ContentType = "application/octet-stream";
            using var output = HttpContext.OpenResponseStream();
            await output
                .WriteAsync(bytes.AsMemory(0, count), cancellationToken)
                .ConfigureAwait(false);
        }

        [Route(HttpVerbs.Get, "/abort")]
        public async Task Abort([RequestAborted] CancellationToken cancellationToken)
        {
            using var output = HttpContext.OpenResponseStream();
            await output
                .WriteAsync(new byte[] { 114 }, cancellationToken)
                .ConfigureAwait(false);
            await output
                .FlushAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                await Task
                    .Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref Aborted);
            }
        }

        [Route(HttpVerbs.Get, "/abort-status")]
        public string AbortStatus() =>
            PlainText(
                Volatile
                    .Read(ref Aborted)
                    .ToString(CultureInfo.InvariantCulture)
            );

        private string PlainText(string text)
        {
            Response.ContentType = "text/plain";
            return text;
        }
    }

    internal sealed class ReadyReport
    {
        public int Port { get; init; }
    }

    internal sealed class ConsumerReport
    {
        public bool Passed { get; init; }
        public int Aborted { get; init; }
        public ulong Contexts { get; init; }
        public ulong Blocks { get; init; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReadyReport))]
    [JsonSerializable(typeof(ConsumerReport))]
    internal sealed partial class ConsumerJson : JsonSerializerContext;

    [GeneratedControllerBinding(typeof(ConsumerController))]
    internal static partial class PackageControllers;
}
