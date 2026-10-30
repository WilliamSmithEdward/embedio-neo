using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

internal static class AdvancedCases
{
    internal static async Task RunAsync(string engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var cases = new List<string>();
        var abortAcquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new WebServerOptions().WithUrlPrefix("http://127.0.0.1:" + LoopbackPorts.Free() + "/");
        options.MaximumRequestBodyBytes = 4;
        options.RequestBodyReadTimeout = TimeSpan.FromMilliseconds(250);

        if (engine == "kestrel") options.WithKestrel();

        using var server = new WebServer(options);
        server.WithLocalSessionManager();
        server.WithModule(new EchoModule());
        server.WithWebApi("/api", module => module.WithController<Controller>());
        server.WithModule(new ActionModule(async context =>
            {
                switch (context.Request.Url.AbsolutePath)
                {
                    case "/fixed":
                        context.Response.ContentLength64 = 2;
                        await context.Response.OutputStream.WriteAsync("ok"u8.ToArray(), context.CancellationToken);
                        break;

                    case "/session":
                        var count = context.Session["count"] is int value ? value : 0;
                        context.Session["count"] = ++count;
                        await context.SendStringAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", Encoding.UTF8);
                        break;

                    case "/echo":
                        using (var reader = new StreamReader(context.OpenRequestStream()))
                            await context.SendStringAsync(await reader.ReadToEndAsync(context.CancellationToken), "text/plain", Encoding.UTF8);

                        break;

                    case "/abort":
                        var token = context.GetRequestAbortedToken();
                        abortAcquired.TrySetResult(true);

                        try
                        {
                            await Task.Delay(Timeout.Infinite, token);
                        }

                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            if (context.CancellationToken.IsCancellationRequested) throw new InvalidOperationException("Peer reset canceled the server token.");

                            abortObserved.TrySetResult(true);
                        }

                        break;

                    default:
                        throw HttpException.NotFound();
                }
            }));
        var running = server.RunAsync(deadline.Token);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(options.UrlPrefixes[0]),
            Timeout = TimeSpan.FromSeconds(5)
        };

        try
        {
            using (var response = await client.GetAsync("api/value/7", deadline.Token))
            {
                Check(response.IsSuccessStatusCode, "controller status");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                Check(
                    json.RootElement
                        .GetProperty("id")
                        .GetInt32()
                        == 7,
                    "controller binding/JSON"
                );
                cases.Add("controller-json-binding");
            }

            Check(await client.GetStringAsync("session", deadline.Token) == "1", "first session");
            Check(await client.GetStringAsync("session", deadline.Token) == "2", "retained session cookie");
            cases.Add("session-cookie-roundtrip");

            using (var body = new ChunkedContent())
            using (var response = await client.PostAsync("echo", body, deadline.Token))
            {
                Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "chunked body limit");
                cases.Add("chunked-body-limit");
            }

            var port = client.BaseAddress.Port;
            var pipeline = await RawAsync(
                port,
                "GET /fixed HTTP/1.1\r\nHost: 127.0.0.1:"
                    + port
                    + "\r\n\r\nGET /fixed HTTP/1.1\r\nHost: 127.0.0.1:"
                    + port
                    + "\r\nConnection: close\r\n\r\n",
                deadline.Token,
                responseCount: 2
            );
            Check(
                pipeline.Length == 2 && pipeline.All(response => response == new WireResponse(200, "ok")),
                "pipeline order/completion"
            );
            cases.Add("raw-pipelining");
            var slow = await RawAsync(
                port,
                "POST /echo HTTP/1.1\r\nHost: 127.0.0.1:"
                    + port
                    + "\r\nContent-Length: 3\r\nConnection: close\r\n\r\na",
                deadline.Token
            );
            Check(slow is [{ Status: 408 }], "body read timeout");
            cases.Add("body-read-timeout");

            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws"), deadline.Token);
                await socket.SendAsync("early"u8.ToArray(), WebSocketMessageType.Text, true, deadline.Token);
                var buffer = new byte[64];
                var received = await socket.ReceiveAsync(buffer, deadline.Token);
                Check(
                    received.MessageType == WebSocketMessageType.Text
                        && Encoding.UTF8.GetString(buffer, 0, received.Count) == "early",
                    "immediate WebSocket echo"
                );
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
                cases.Add("websocket-immediate-echo-close");
            }

            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                await tcp
                    .GetStream()
                    .WriteAsync(
                        Encoding.ASCII.GetBytes("GET /abort HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n\r\n"),
                        deadline.Token
                    );
                await abortAcquired.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
                tcp.Client.LingerState = new LingerOption(true, 0);
            }

            await abortObserved.Task.WaitAsync(TimeSpan.FromSeconds(3), deadline.Token);
            cases.Add("request-abort-server-token-isolation");
        }

        finally
        {
            deadline.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await HttpsAsync(engine);
        cases.Add("https-fixed-response");
        Console.WriteLine(
            JsonSerializer.Serialize(
                new CompatibilityReport(engine, "advanced", cases.Count, cases),
                JsonSerializerOptions.Web
            )
        );
    }

    private static async Task<WireResponse[]> RawAsync(
        int port,
        string request,
        CancellationToken token,
        int responseCount = 1
    )
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, token);
        await tcp
            .GetStream()
            .WriteAsync(Encoding.ASCII.GetBytes(request), token);
        var responses = new WireResponse[responseCount];

        for (var index = 0; index < responseCount; index++)
            responses[index] = await WireResponse.ReadAsync(tcp.GetStream(), token);

        return responses;
    }

    private static async Task HttpsAsync(string engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
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
        var options = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithCertificate(certificate);

        if (engine == "kestrel") options.WithKestrel();

        using var server = new WebServer(options);
        server.WithModule(new ActionModule(context => context.SendStringAsync("secure", "text/plain", Encoding.UTF8)));
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint
                == certificate.Thumbprint
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var running = server.RunAsync(deadline.Token);

        try
        {
            Check(await client.GetStringAsync(options.UrlPrefixes[0], deadline.Token) == "secure", "HTTPS response");
        }

        finally
        {
            deadline.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    public sealed class Controller : WebApiController
    {
        [Route(HttpVerbs.Get, "/value/{id}")]
        public object GetValue(int id) => new
        {
            id,
            method = HttpContext.Request.HttpMethod
        };
    }

    private sealed class EchoModule() : WebSocketModule("/ws", false)
    {
        protected override Task OnMessageReceivedAsync(
            IWebSocketContext context,
            byte[] buffer,
            IWebSocketReceiveResult result
        ) => context.WebSocket.SendAsync(buffer, true, context.CancellationToken);
    }

    private sealed class ChunkedContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream
                .WriteAsync("abcde"u8.ToArray())
                .AsTask();
    }
}
