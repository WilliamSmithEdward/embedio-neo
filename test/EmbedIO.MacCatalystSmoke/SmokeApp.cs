using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EmbedIO.MacCatalystSmoke;

public sealed class SmokeApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var view = new WebView();
        var page = new ContentPage { Content = view };
        page.Loaded += async (_, _) => await RunSmokeAsync(view);
        return new Window(page);
    }

    private static async Task RunSmokeAsync(WebView view)
    {
        var result = Path.Combine(FileSystem.AppDataDirectory, "smoke-result.json");
        var root = Path.Combine(FileSystem.AppDataDirectory, "www");
        Directory.CreateDirectory(root);
        try
        {
            await PlatformTests.HttpsSmoke.RunAsync();
            const string marker = "EmbedIO issue 601 passed";
            File.WriteAllText(Path.Combine(root, "index.html"), $"<!doctype html><html><body>{marker}</body></html>");
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
            port.Stop();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithStaticFolder("/", root, true);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = server.RunAsync(stop.Token);
            try
            {
                if (server.State != WebServerState.Listening)
                {
                    await running;
                    throw new InvalidOperationException("Listener did not start.");
                }

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                if (!(await client.GetStringAsync(url)).Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException("HTTP response did not contain the test page.");

                var navigation = new TaskCompletionSource<WebNavigationResult>();
                view.Navigated += (_, e) => navigation.TrySetResult(e.Result);
                view.Source = url;
                if (await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30)) != WebNavigationResult.Success)
                    throw new InvalidOperationException("WebView navigation failed.");
                if (!(await view.EvaluateJavaScriptAsync("document.body.textContent")).Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException("WebView did not render the served page.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }

            File.WriteAllText(result, JsonSerializer.Serialize(new { passed = true, https = "passed", os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString() }));
            Environment.Exit(0);
        }
        catch (Exception error)
        {
            File.WriteAllText(result, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
            Environment.Exit(1);
        }
    }
}
