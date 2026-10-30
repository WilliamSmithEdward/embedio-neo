using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;

internal static class ProtocolCases
{
    internal static async Task RunAsync(string engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cases = new List<string>();
        var clear = new WebServerOptions().WithUrlPrefix("http://127.0.0.1:" + LoopbackPorts.Free() + "/");

        if (engine == "kestrel") clear.WithKestrel();

        await ServeAsync(clear, [HttpVersion.Version11, HttpVersion.Version20], cases, deadline.Token);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        using var certificate = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable
        );
        var combined = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithCertificate(certificate)
            .WithMode(HttpListenerMode.EmbedIOCombined);

        if (engine == "kestrel") combined.WithKestrel();

        await ServeAsync(
            combined,
            [HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30],
            cases,
            deadline.Token
        );
        var http3 = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithCertificate(certificate)
            .WithMode(HttpListenerMode.EmbedIOHttp3);

        if (engine == "kestrel") http3.WithKestrel();

        await ServeAsync(http3, [HttpVersion.Version30], cases, deadline.Token);
        Console.WriteLine(
            JsonSerializer.Serialize(
                new CompatibilityReport(engine, "protocols", cases.Count, cases),
                JsonSerializerOptions.Web
            )
        );
    }

    private static async Task ServeAsync(
        WebServerOptions options,
        Version[] versions,
        List<string> cases,
        CancellationToken token
    )
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options);
        server.WithModule(new EchoModule());
        server.WithModule(new ActionModule(async context =>
            {
                if (context.Request.ProtocolVersion >= HttpVersion.Version20)
                    _ = context.GetRequestAbortedToken(cancelOnHttp1InputEnd: true);

                string content;

                using (var reader = new StreamReader(context.OpenRequestStream())) content = await reader.ReadToEndAsync(context.CancellationToken);

                var body = Encoding.UTF8.GetBytes(context.Request.ProtocolVersion.Major + ":" + context.Request.HttpMethod + ":" + content);
                context.Response.ContentType = "text/plain";
                context.Response.SendChunked = context.Request.QueryString["chunked"] == "1";

                if (!context.Response.SendChunked) context.Response.ContentLength64 = body.Length;

                await context.Response.OutputStream.WriteAsync(body, context.CancellationToken);
            }));
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, actual, _, _) =>
                actual?.Thumbprint == options.Certificate?.Thumbprint
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.UrlPrefixes[0]),
            Timeout = TimeSpan.FromSeconds(6)
        };
        var running = server.RunAsync(lifetime.Token);

        try
        {
            var tasks = versions.SelectMany(version => Enumerable
                .Range(0, 8)
                .Select(async index =>
                {
                    var payload = "payload-" + index;
                    using var request = new HttpRequestMessage(HttpMethod.Post, "echo?chunked=" + (index & 1))
                    {
                        Version = version,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = new StringContent(payload)
                    };
                    using var response = await client.SendAsync(request, token);
                    var body = await response.Content.ReadAsStringAsync(token);

                    if (!response.IsSuccessStatusCode || response.Version != version || body != version.Major + ":POST:" + payload)
                        throw new InvalidOperationException(options.UrlPrefixes[0] + " HTTP/" + version + " did not preserve its protocol and body: " + response.StatusCode + " " + response.Version + " " + body);

                    return (options.Mode == HttpListenerMode.EmbedIOHttp3 ? "http3-only-" : options.Certificate == null ? "cleartext-" : "tls-") + version + "-" + index;
                }));
            cases.AddRange(await Task.WhenAll(tasks));

            if (versions.Contains(HttpVersion.Version20))
            {
                using var socket = new ClientWebSocket();
                socket.Options.HttpVersion = HttpVersion.Version20;
                socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
                var address =
                    new UriBuilder(client.BaseAddress ?? throw new InvalidOperationException("Missing client address."))
                    {
                        Scheme = options.Certificate == null ? "ws" : "wss",
                        Path = "/ws"
                    }.Uri;
                await socket.ConnectAsync(address, invoker, token);
                await socket.SendAsync("h2-websocket"u8.ToArray(), WebSocketMessageType.Text, true, token);
                var buffer = new byte[64];
                var received = await socket.ReceiveAsync(buffer, token);

                if (
                    received.MessageType != WebSocketMessageType.Text
                    || Encoding.UTF8.GetString(buffer, 0, received.Count) != "h2-websocket"
                )
                    throw new InvalidOperationException("HTTP/2 WebSocket did not preserve its immediate message.");

                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", token);
                cases.Add((options.Certificate == null ? "cleartext-" : "tls-") + "http2-websocket");
            }
        }

        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private sealed class EchoModule() : WebSocketModule("/ws", false)
    {
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] buffer,
            IWebSocketReceiveResult result
        ) => context.WebSocket.SendAsync(buffer, true, context.CancellationToken);
    }
}
