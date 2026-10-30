using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.Net;
using EmbedIO.PlatformTests;

internal static class StartupCases
{
    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cases = new List<string>();
        var port = LoopbackPorts.Free();
        await RefusedAsync(
            new WebServerOptions()
                .WithUrlPrefix("http://127.0.0.1:" + port + "/")
                .WithUrlPrefix("https://127.0.0.1:" + port + "/"),
            typeof(ArgumentException),
            deadline.Token
        );
        cases.Add("mixed-http-https-port-refused");
        var webTransport = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithMode(HttpListenerMode.EmbedIOHttp3);
        webTransport.WebTransport = new WebTransportOptions();
        await RefusedAsync(webTransport, typeof(NotSupportedException), deadline.Token);
        cases.Add("unsupported-webtransport-refused");
        var retry = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithMode(HttpListenerMode.EmbedIOHttp3);
        retry.RequireHttp3AddressValidation = true;
        await RefusedAsync(retry, typeof(NotSupportedException), deadline.Token);
        cases.Add("unsupported-forced-retry-refused");
        var oldTls = new WebServerOptions()
            .WithUrlPrefix("https://127.0.0.1:" + LoopbackPorts.Free() + "/")
            .WithMode(HttpListenerMode.EmbedIOHttp3);
        oldTls.TlsPolicy = new ServerTlsPolicy { EnabledProtocols = SslProtocols.Tls12 };
        await RefusedAsync(oldTls, typeof(NotSupportedException), deadline.Token);
        cases.Add("http3-without-tls13-refused");
        var first = LoopbackPorts.Free();
        var occupiedPort = LoopbackPorts.Free();
        using var occupied = new TcpListener(IPAddress.Loopback, occupiedPort);
        occupied.Start();
        var failed = new WebServerOptions()
            .WithUrlPrefix("http://127.0.0.1:" + first + "/")
            .WithUrlPrefix("http://127.0.0.1:" + occupiedPort + "/");
        await RefusedAsync(failed, null, deadline.Token);

        using (var restored = new TcpListener(IPAddress.Loopback, first)) restored.Start();

        cases.Add("partial-bind-failure-releases-earlier-port");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var healthy = new WebServer(
            new WebServerOptions()
                .WithUrlPrefix("http://127.0.0.1:" + first + "/")
                .WithKestrel()
        );
        healthy.WithModule(new ActionModule(context => context.SendStringAsync("ok", "text/plain", Encoding.UTF8)));
        var running = healthy.RunAsync(lifetime.Token);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        try
        {
            if (await client.GetStringAsync("http://127.0.0.1:" + first + "/", deadline.Token) != "ok")
                throw new InvalidOperationException("Healthy server did not recover after startup failure.");

            cases.Add("healthy-server-after-failed-startup");
        }

        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Console.WriteLine(
            JsonSerializer.Serialize(
                new CompatibilityReport("kestrel", "startup", cases.Count, cases),
                JsonSerializerOptions.Web
            )
        );
    }

    private static async Task RefusedAsync(WebServerOptions options, Type? expected, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? observed = null;

        try
        {
            using var server = new WebServer(options.WithKestrel());
            var running = server.RunAsync(lifetime.Token);

            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(3), token);
            }

            finally
            {
                lifetime.Cancel();
            }
        }

        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        {
            observed = error;
        }

        finally
        {
            lifetime.Cancel();
        }

        if (observed == null || expected != null && !expected.IsInstanceOfType(observed))
            throw new InvalidOperationException(
                "Startup accepted an unsupported or conflicting configuration.",
                observed
            );
    }
}
