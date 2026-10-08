using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO.Actions;

namespace EmbedIO.MauiHttpsSmoke;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp() => MauiApp.CreateBuilder().UseMauiApp<SmokeApp>().Build();
}

public sealed class SmokeApp : Application
{
    private volatile string _phase = "starting";
    private volatile string? _error;
    private readonly ConcurrentDictionary<string, string> _checks = new();
    private bool _started;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly ConcurrentQueue<object> _events = new();

    private void Record(string name, string? detail = null)
    {
        _events.Enqueue(new { name, detail, milliseconds = _elapsed.ElapsedMilliseconds });
        while (_events.Count > 128) _events.TryDequeue(out _);
        Console.WriteLine($"HTTPS smoke: {name} at {_elapsed.ElapsedMilliseconds}ms: {detail}");
    }

    protected override Window CreateWindow(IActivationState? state)
    {
        var view = new WebView();
        var page = new ContentPage { Content = view };
        page.Loaded += async (_, _) =>
        {
            if (_started) return;
            _started = true;
            await RunAsync(view);
        };
        return new Window(page);
    }

    private object Report() => new
    {
        passed = _phase == "passed", phase = _phase, checks = _checks, error = _error,
        os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
        platform = DeviceInfo.Platform.ToString(), pid = Environment.ProcessId, events = _events.ToArray(),
    };

    private void WriteReport()
    {
        var directory = DeviceInfo.Platform == DevicePlatform.WinUI
            ? Environment.GetEnvironmentVariable("EMBEDIO_HTTPS_RESULTS") ?? FileSystem.AppDataDirectory
            : FileSystem.AppDataDirectory;
        File.WriteAllText(Path.Combine(directory, "https-result.json"), JsonSerializer.Serialize(Report()));
    }

    private async Task RunAsync(WebView view)
    {
        const string url = "https://127.0.0.1:59626/";
        const string marker = "EmbedIO MAUI HTTPS rendered";
        Record("app-start");
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var asset = await FileSystem.OpenAppPackageFileAsync("https-server.pfx");
            using var bytes = new MemoryStream();
            await asset.CopyToAsync(bytes);
            using var certificate = X509CertificateLoader.LoadPkcs12(bytes.ToArray(), null);
            using var server = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO)
                .WithUrlPrefix("https://*:59626/").WithCertificate(certificate))
                .WithModule(new ActionModule("/state", HttpVerbs.Get, context => context.SendDataAsync(Report())))
                .WithModule(new ActionModule("/finish", HttpVerbs.Post, async context =>
                {
                    Record("finish-request");
                    if (_phase != "ready") throw new InvalidOperationException("App tests have not completed.");
                    _checks["external_https"] = "passed";
                    _phase = "passed";
                    await context.SendDataAsync(Report());
                    Record("finish-body-written");
                    context.Response.Close();
                    Record("finish-response-closed");
                    finish.TrySetResult();
                }))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    if (!context.Request.IsSecureConnection) throw new InvalidOperationException("Expected TLS.");
                    return context.SendStringAsync($"<!doctype html><html><body>{marker}</body></html>", "text/html", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                if (server.State != WebServerState.Listening) throw new InvalidOperationException("Listener did not start.");
                Record("listener-started");
                _phase = "testing";
                await PlatformTests.HttpsSmoke.RunAsync();
                _checks["transport_and_untrusted_certificate"] = "passed";
                Record("transport-checks-passed");
                // Normal platform trust, without a certificate-validation callback.
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                if (!(await client.GetStringAsync(url)).Contains(marker, StringComparison.Ordinal))
                    throw new InvalidOperationException("Platform HTTP client did not retrieve the TLS page.");
                _checks["platform_client_trust"] = "passed";
                Record("native-trust-passed");
                var navigation = new TaskCompletionSource<WebNavigationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var expectedUrl = new Uri(url);
                view.Navigating += (_, e) => Record("webview-navigating", e.Url);
                view.Navigated += (_, e) =>
                {
                    Record("webview-navigated", $"{e.Result}: {e.Url}");
                    // Initial about:blank navigation can finish after the view
                    // loads. Only the requested HTTPS navigation is evidence.
                    if (Uri.TryCreate(e.Url, UriKind.Absolute, out var actualUrl) && actualUrl == expectedUrl)
                        navigation.TrySetResult(e.Result);
                };
                Record("webview-source-assigned", url);
                view.Source = url;
                if (await navigation.Task.WaitAsync(TimeSpan.FromSeconds(45)) != WebNavigationResult.Success)
                    throw new InvalidOperationException("Trusted HTTPS WebView navigation failed.");
                var rendered = false;
                string? lastDom = null;
                var domWait = Stopwatch.StartNew();
                while (domWait.Elapsed < TimeSpan.FromSeconds(10))
                {
                    lastDom = await view.EvaluateJavaScriptAsync(
                        "document.readyState === 'complete' && document.body ? document.body.textContent : ''")
                        .WaitAsync(TimeSpan.FromSeconds(2));
                    if (lastDom?.Contains(marker, StringComparison.Ordinal) == true)
                    {
                        rendered = true;
                        break;
                    }
                    await Task.Delay(100);
                }
                if (!rendered)
                    throw new InvalidOperationException($"WebView did not render the HTTPS page; last DOM result: {lastDom}");
                _checks["webview_trust_and_render"] = "passed";
                Record("webview-render-passed", lastDom);
                _phase = "ready";
                await finish.Task.WaitAsync(TimeSpan.FromMinutes(3));
            }
            catch (Exception error)
            {
                _error = error.ToString();
                _phase = "failed";
                Console.Error.WriteLine(_error);
                // Keep the state endpoint available briefly so the runner captures the error.
                await Task.Delay(TimeSpan.FromSeconds(15));
                throw;
            }
            finally
            {
                Record("listener-stopping");
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                Record("listener-stopped");
            }
            WriteReport();
            Environment.Exit(0);
        }
        catch (Exception error)
        {
            _error = error.ToString();
            _phase = "failed";
            WriteReport();
            Console.Error.WriteLine(_error);
            Environment.Exit(1);
        }
    }
}
