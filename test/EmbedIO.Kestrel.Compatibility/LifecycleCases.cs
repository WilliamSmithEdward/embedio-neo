using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.PlatformTests;

internal static class LifecycleCases
{
    internal static async Task RunAsync(string engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var cases = new List<string>();
        await AdmissionAsync(engine, cases, token);
        await OwnershipAsync(engine, cases, token);
        await InitialDeadlineAsync(engine, cases, token);
        await PortReuseAsync(engine, cases, token);
        await EarlyResponseAsync(engine, cases, token);
        Console.WriteLine(
            JsonSerializer.Serialize(
                new CompatibilityReport(engine, "lifecycle", cases.Count, cases),
                JsonSerializerOptions.Web
            )
        );
    }

    private static async Task EventuallyHealthyAsync(HttpClient client, string address, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));

        while (true)
        {
            try
            {
                Check(await client.GetStringAsync(address, deadline.Token) == "ok", "healthy successor response");
                return;
            }

            catch (HttpRequestException) when (!deadline.IsCancellationRequested) { }

            await Task.Delay(10, deadline.Token);
        }
    }

    private static WebServerOptions Options(string engine)
    {
        var options = new WebServerOptions().WithUrlPrefix("http://127.0.0.1:" + LoopbackPorts.Free() + "/");

        if (engine == "kestrel") options.WithKestrel();

        return options;
    }

    private static async Task AdmissionAsync(string engine, List<string> cases, CancellationToken token)
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(engine);
        options.MaximumConcurrentRequests = 1;
        options.RequestQueueLimit = 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options);
        server.WithModule(new ActionModule(async context =>
            {
                if (context.Request.Url.AbsolutePath == "/hold")
                {
                    entered.TrySetResult(true);
                    await release.Task.WaitAsync(context.CancellationToken);
                }

                await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            }));
        var running = server.RunAsync(lifetime.Token);
        using var client = new HttpClient
        {
            BaseAddress = new Uri(options.UrlPrefixes[0]),
            Timeout = TimeSpan.FromSeconds(4),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        try
        {
            var held = client.GetStringAsync("hold", token);
            await entered.Task.WaitAsync(token);
            using var request = new HttpRequestMessage(HttpMethod.Get, "busy")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var rejected = await client.SendAsync(request, token);
            Check(
                rejected.StatusCode == HttpStatusCode.ServiceUnavailable,
                "request admission did not refuse overflow"
            );
            release.TrySetResult(true);
            Check(await held == "ok", "held response");
            await EventuallyHealthyAsync(client, "healthy", token);
            cases.Add("cross-protocol-request-admission-overflow-and-release");
        }

        finally
        {
            release.TrySetResult(true);
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        }
    }

    private static async Task OwnershipAsync(string engine, List<string> cases, CancellationToken token)
    {
        var options = Options(engine);
        IHttpRequest? retained = null;
        Task<int>? abandoned = null;
        var callbackOrder = new List<int>();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options);
        server.WithModule(new ActionModule(async context =>
            {
                if (context.Request.Url.AbsolutePath == "/trailers")
                {
                    retained = context.Request;
                    using var reader = new StreamReader(context.OpenRequestStream());
                    Check(await reader.ReadToEndAsync(context.CancellationToken) == "abc", "chunked request body");
                    Check(context.Request is IHttpRequestTrailers trailers && trailers.TryGetTrailers(out var fields) && fields.Any(field => field.Key.Equals("X-Final", StringComparison.OrdinalIgnoreCase) && field.Value == "owned"), "request trailers");
                    context.OnClose(_ => callbackOrder.Add(1));
                    context.OnClose(_ => throw new InvalidOperationException("Expected callback failure."));
                    context.OnClose(closed =>
                    {
                        ((IHttpContextImpl)closed).Close();
                        callbackOrder.Add(2);
                    });
                }

                else if (context.Request.Url.AbsolutePath == "/abandoned")
                {
                    abandoned = context.Request.InputStream.ReadAsync(new byte[1], 0, 1, context.CancellationToken);
                }

                else if (context.Request.Url.AbsolutePath == "/bad")
                {
                    using var reader = new StreamReader(context.OpenRequestStream());
                    _ = await reader.ReadToEndAsync(context.CancellationToken);
                }

                context.Response.ContentLength64 = 2;
                await context.Response.OutputStream.WriteAsync("ok"u8.ToArray(), context.CancellationToken);
            }));
        var running = server.RunAsync(lifetime.Token);
        var port = new Uri(options.UrlPrefixes[0]).Port;

        try
        {
            using (var tcp = await ConnectAsync(port, token))
            {
                await SendAsync(
                    tcp,
                    "POST /trailers HTTP/1.1\r\nHost: 127.0.0.1:"
                        + port
                        + "\r\nTransfer-Encoding: chunked\r\nTrailer: X-Final\r\n\r\n3\r\nabc\r\n0\r\nX-Final: owned\r\n\r\nGET /next HTTP/1.1\r\nHost: 127.0.0.1:"
                        + port
                        + "\r\n\r\n",
                    token
                );
                Check(
                    await WireResponse.ReadAsync(tcp.GetStream(), token) == new WireResponse(200, "ok"),
                    "trailer response"
                );
                Check(
                    await WireResponse.ReadAsync(tcp.GetStream(), token) == new WireResponse(200, "ok"),
                    "successor response"
                );
            }

            Check(
                retained is IHttpRequestTrailers saved
                    && saved.TryGetTrailers(out var snapshot)
                    && snapshot.Any(field => field.Value == "owned"),
                "retained request lost its trailer snapshot"
            );
            Check(callbackOrder is [2, 1], "close callback order, reentrancy or failure isolation");
            cases.Add("retained-trailers-and-reentrant-close-callbacks");

            using (var tcp = await ConnectAsync(port, token))
            {
                await SendAsync(
                    tcp,
                    "POST /abandoned HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nContent-Length: 3\r\n\r\n",
                    token
                );
                Check(
                    await WireResponse.ReadAsync(tcp.GetStream(), token) == new WireResponse(200, "ok"),
                    "abandoned read response"
                );
                var pendingRead = abandoned ?? throw new InvalidOperationException("Missing admitted body read.");

                try
                {
                    _ = await pendingRead.WaitAsync(TimeSpan.FromSeconds(2), token);
                    throw new InvalidOperationException("An abandoned read completed successfully.");
                }

                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }

                catch (ObjectDisposedException) { }

                await SendAsync(tcp, "abcGET /next HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n\r\n", token);
                Check(
                    await WireResponse.ReadAsync(tcp.GetStream(), token) == new WireResponse(200, "ok"),
                    "abandoned read consumed successor input"
                );
            }

            cases.Add("pending-body-read-retirement-and-successor");

            using (var tcp = await ConnectAsync(port, token))
            {
                await SendAsync(
                    tcp,
                    "POST /bad HTTP/1.1\r\nHost: 127.0.0.1:"
                        + port
                        + "\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\nz\r\n",
                    token
                );
                Check(
                    (await WireResponse.ReadAsync(tcp.GetStream(), token)).Status == 400,
                    "invalid chunk was not refused with 400"
                );
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Check(
                await client.GetStringAsync(options.UrlPrefixes[0] + "healthy", token) == "ok",
                "malformed request stopped the listener"
            );
            cases.Add("malformed-body-refusal-and-healthy-listener");
        }

        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        }
    }

    private static async Task InitialDeadlineAsync(string engine, List<string> cases, CancellationToken token)
    {
        var options = Options(engine);
        options.MaximumConnections = 1;
        options.RequestHeadersTimeout = TimeSpan.FromMilliseconds(350);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options);
        server.WithModule(new ActionModule(context => context.SendStringAsync("ok", "text/plain", Encoding.UTF8)));
        var running = server.RunAsync(lifetime.Token);
        var port = new Uri(options.UrlPrefixes[0]).Port;

        try
        {
            using var slow = await ConnectAsync(port, token);
            await SendAsync(slow, "PRI * HTTP/2.0\r\n", token);
            var end = new byte[1];

            try
            {
                Check(
                    await slow
                        .GetStream()
                        .ReadAsync(end, token)
                        == 0,
                    "partial preface escaped initial deadline"
                );
            }

            catch (IOException) { }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            // Peer EOF can precede completion of the server's transport disposal.
            await EventuallyHealthyAsync(client, options.UrlPrefixes[0], token);
            cases.Add("partial-preface-deadline-releases-connection-slot");
        }

        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        }
    }

    private static async Task PortReuseAsync(string engine, List<string> cases, CancellationToken token)
    {
        var original = Options(engine);
        var prefix = original.UrlPrefixes[0];
        var names = new[] { "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "ASPNETCORE_URLS", "ASPNETCORE_PREFERHOSTINGURLS" };
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            Environment.SetEnvironmentVariable(names[0], "EmbedIO_Compatibility_MustNotLoad");
            Environment.SetEnvironmentVariable(names[1], "http://127.0.0.1:1/");
            Environment.SetEnvironmentVariable(names[2], "true");

            for (var cycle = 0; cycle < 3; cycle++)
            {
                var options = new WebServerOptions().WithUrlPrefix(prefix);
                options.RequestHeadersTimeout = TimeSpan.FromSeconds(10);

                if (engine == "kestrel") options.WithKestrel();

                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var server = new WebServer(options);
                server.WithModule(
                    new ActionModule(context => context.SendStringAsync("ok", "text/plain", Encoding.UTF8))
                );
                var running = server.RunAsync(lifetime.Token);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var partial = await ConnectAsync(new Uri(prefix).Port, token);

                try
                {
                    await SendAsync(partial, "PRI * HTTP/2.0\r\n", token);
                    Check(
                        await client.GetStringAsync(prefix, token) == "ok",
                        "ambient ASP.NET configuration changed the selected endpoint"
                    );
                }

                finally
                {
                    lifetime.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
                }
            }

            cases.Add("ambient-host-settings-isolation-and-port-reuse-with-pending-preface");
        }

        finally
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, saved[name]);
        }
    }

    private static async Task EarlyResponseAsync(string engine, List<string> cases, CancellationToken token)
    {
        var options = Options(engine);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options);
        server.WithModule(new ActionModule(async context =>
            {
                if (context.Request.Url.AbsolutePath == "/early")
                {
                    context.Response.ContentLength64 = 5;
                    await context.Response.OutputStream.WriteAsync("first"u8.ToArray(), context.CancellationToken);
                    context.Response.Close();
                    await release.Task.WaitAsync(context.CancellationToken);

                    try
                    {
                        await context.Response.OutputStream.WriteAsync("late"u8.ToArray(), context.CancellationToken);
                        ended.TrySetException(new InvalidOperationException("Old output accepted a late write."));
                    }

                    catch (ObjectDisposedException)
                    {
                        ended.TrySetResult(true);
                    }
                }

                else await context.SendStringAsync("second", "text/plain", Encoding.UTF8);
            }));
        var running = server.RunAsync(lifetime.Token);
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 1 };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.UrlPrefixes[0]),
            Timeout = TimeSpan.FromSeconds(3)
        };

        try
        {
            Check(await client.GetStringAsync("early", token) == "first", "early response");
            Check(!ended.Task.IsCompleted, "old handler already ended");
            Check(
                await client.GetStringAsync("next", token) == "second",
                "ended response blocked its keep-alive successor"
            );
            release.TrySetResult(true);
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            cases.Add("early-response-completion-and-late-handler-isolation");
        }

        finally
        {
            release.TrySetResult(true);
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        }
    }

    private static async Task<TcpClient> ConnectAsync(int port, CancellationToken token)
    {
        var tcp = new TcpClient();

        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port, token);
            return tcp;
        }

        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static ValueTask SendAsync(TcpClient tcp, string text, CancellationToken token) =>
        tcp
            .GetStream()
            .WriteAsync(Encoding.ASCII.GetBytes(text), token);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
