using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EmbedIO.Actions;
using EmbedIO.DependencyInjection;
using EmbedIO.Endpoints;
using EmbedIO.OpenApi;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using Microsoft.Extensions.DependencyInjection;

namespace EmbedIO.AotSmoke
{
    // HTTPS, HTTP/1.1, HTTP/2 and HTTP/3 from one combined listener, WebSockets over TLS,
    // local sessions and a controller activated from request-scoped services, all in the
    // native executable. The certificate is generated per run and trusted by exact hash.
    internal static class ProtocolChecks
    {
        internal static async Task RunAsync(SortedDictionary<string, string> checks)
        {
            Require(QuicListener.IsSupported, "QUIC is not supported on this host, so HTTP/3 cannot be checked.");
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var url = $"https://localhost:{LoopbackPorts.Free()}/";

            var services = new ServiceCollection()
                .AddScoped<Greeter>()
                .BuildServiceProvider();
            await using var disposeServices = services;
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIOCombined)
                .WithCertificate(certificate))
                .WithDependencyInjection(services)
                .WithLocalSessionManager()
                .WithModule(new EchoSocketModule("/ws"))
                .WithModule(
                    new WebApiModule("/di", ResponseSerializer.Json(SmokeJson.Default.Options)).WithControllerServices(
                        SmokeControllers.GreetingController
                    )
                )
                .WithModule(
                    new ActionModule(
                        "/protocol",
                        HttpVerbs.Get,
                        context => context.SendStringAsync(
                            "HTTP/"
                                + context.Request.ProtocolVersion
                                + (context.Request.IsSecureConnection ? " secure" : " plain"),
                            "text/plain",
                            Encoding.UTF8
                        )
                    )
                )
                .WithModule(new ActionModule("/session", HttpVerbs.Get, context =>
                    {
                        var count = context.Session.TryGetValue("count", out var value) && value is int previous ? previous + 1 : 1;
                        context.Session["count"] = count;
                        return context.SendStringAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", Encoding.UTF8);
                    }));
            var minimal = new EndpointModule(ResponseSerializer.Json(SmokeJson.Default.Options));
            minimal.Root
                .MapGet("/minimal-di/{name}", MinimalDiHandlers.GreetEndpoint)
                .WithName("minimal-di");
            var minimalDocument = new OpenApiDocumentBuilder("Minimal DI", "1")
                .AddEndpoints(minimal)
                .Build(SmokeJson.Default.Options);
            Require(
                minimalDocument.Root["paths"]?["/minimal-di/{name}"]?["get"]?["parameters"]?.AsArray().Count == 1,
                "AOT minimal DI metadata exposed framework/service parameters."
            );
            server.WithModule(minimal);
            var running = server.RunAsync(stop.Token);

            try
            {
                using var client = HttpsSmoke.CreateClient(certificate);

                foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "protocol")
                    {
                        Version = version,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact
                    };
                    using var response = await client.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    Require(
                        response.StatusCode == HttpStatusCode.OK
                            && response.Version == version
                            && body == "HTTP/" + version + " secure",
                        "HTTP/"
                            + version
                            + " returned "
                            + (int)response.StatusCode
                            + " over HTTP/"
                            + response.Version
                            + ": "
                            + body
                    );
                    checks["https_http" + version.Major + version.Minor] = body;

                    async Task<string> MinimalGreeting()
                    {
                        using var greeting = new HttpRequestMessage(HttpMethod.Get, url + "minimal-di/native")
                        {
                            Version = version,
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact
                        };
                        using var result = await client.SendAsync(greeting);
                        Require(
                            result.StatusCode == HttpStatusCode.OK && result.Version == version,
                            "AOT minimal DI transport mismatch."
                        );
                        return await result.Content.ReadAsStringAsync();
                    }

                    var minimalA = await MinimalGreeting();
                    var minimalB = await MinimalGreeting();
                    Require(
                        minimalA.StartsWith("\"Hello native from scope ", StringComparison.Ordinal)
                            && minimalB.StartsWith("\"Hello native from scope ", StringComparison.Ordinal)
                            && minimalA != minimalB,
                        "AOT minimal DI did not isolate request services."
                    );
                    checks["minimal_di_http" + version.Major + version.Minor] =
                        "request-owned FromServices/context and ValueTask over exact protocol";
                }

                var first = await client.GetStringAsync(url + "session");
                var second = await client.GetStringAsync(url + "session");
                Require(first == "1" && second == "2", "Session counter returned " + first + ", " + second);
                checks["session"] = first + "," + second;

                var greetA = await client.GetStringAsync(url + "di/greet/aot");
                var greetB = await client.GetStringAsync(url + "di/greet/aot");
                Require(
                    greetA.StartsWith("\"Hello aot from scope ", StringComparison.Ordinal)
                        && greetB.StartsWith("\"Hello aot from scope ", StringComparison.Ordinal)
                        && greetA != greetB,
                    "DI controller returned " + greetA + " and " + greetB
                );
                checks["di_controller"] = greetA + " " + greetB;

                checks["websocket"] = await WebSocketEchoAsync(url, certificate);
            }

            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            checks["protocol_shutdown"] = "completed";
        }

        private static async Task<string> WebSocketEchoAsync(string url, X509Certificate2 certificate)
        {
            var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            using var socket = new ClientWebSocket();
            socket.Options.RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
                    == System.Net.Security.SslPolicyErrors.None
                && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(new Uri("wss" + url["https".Length..] + "ws"), timeout.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, timeout.Token);
            var buffer = new byte[64];
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            var text = Encoding.UTF8.GetString(buffer, 0, received.Count);
            Require(
                received.MessageType == WebSocketMessageType.Text && received.EndOfMessage && text == "echo:ping",
                "WebSocket returned " + text
            );
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            Require(socket.State == WebSocketState.Closed, "WebSocket ended in state " + socket.State);
            return text;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class EchoSocketModule : WebSocketMessageModule
        {
            public EchoSocketModule(string path) : base(path) { }

            protected override Task OnTextMessageReceivedAsync(IWebSocketContext context, string text) =>
                SendAsync(context, "echo:" + text);
        }
    }

    // A request-scoped service, constructed by the container for each request.
    internal sealed class Greeter
    {
        private static int _instances;

        public int Id { get; } = Interlocked.Increment(ref _instances);

        public string Greet(string name) =>
            "Hello " + name + " from scope " + Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class GreetingController : WebApiController
    {
        private readonly Greeter _greeter;

        public GreetingController(Greeter greeter) => _greeter = greeter;

        // The parameter comes from the same request scope as the constructor argument.
        [Route(HttpVerbs.Get, "/greet/{name}")]
        public string Greet(string name, [FromServices] Greeter fromServices) =>
            ReferenceEquals(fromServices, _greeter)
                ? _greeter.Greet(name)
                : "parameter and constructor services differ";
    }

    [GeneratedEndpointBinding]
    internal static partial class MinimalDiHandlers
    {
        public static ValueTask<string> Greet(string name, [FromServices] Greeter service, IHttpContext context) =>
            ValueTask.FromResult(
                ReferenceEquals(
                    service,
                    context
                        .GetRequestServices()
                        .GetRequiredService<Greeter>()
                )
                    ? service.Greet(name)
                    : throw new InvalidOperationException("Minimal endpoint service scope mismatch.")
            );
    }
}
