using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using EmbedIO.Diagnostics;
using EmbedIO.Files;
using System.Runtime.Versioning;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.WebApi;

namespace EmbedIO.AppContainerSmoke;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int Port = 59654;
    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows AppContainer fixture");
        if (args.Length > 0 && args[0] == "serve")
        {
            try { await ServeAsync(int.Parse(args[1]), args[2], args[3]); return 0; }
            catch (Exception error) { File.WriteAllText(Path.Combine(args[2], "failure.txt"), error.ToString()); return 1; }
        }
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") != "WilliamSmithEdward/embedio-neo")
            throw new InvalidOperationException("Profile, ACL and exemption changes are restricted to disposable GitHub runners.");
        var results = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(results);
        var checks = new Dictionary<string, object>();
        var passed = false;
        string? failure = null;
        using var profile = new ContainerProfile();
        Process? child = null, inbound = null;
        var outboundAdded = false;
        var firewallAdded = false;
        try
        {
            var executable = Path.Combine(AppContext.BaseDirectory, "EmbedIO.AppContainerSmoke.exe");
            await RunToolAsync("icacls.exe", AppContext.BaseDirectory, "/grant", "*" + profile.Sid + ":(OI)(CI)(RX)", "/T", "/Q");
            Environment.SetEnvironmentVariable("DOTNET_EnableDiagnostics", "0");
            firewallAdded = true;
            await RunToolAsync("WindowsPowerShell/v1.0/powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
                $"New-NetFirewallRule -Name '{profile.Name}' -DisplayName '{profile.Name}' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {Port} -LocalAddress 127.0.0.1 -RemoteAddress 127.0.0.1 -Profile Any -Package '{profile.Sid}' | Out-Null");
            child = profile.Launch(executable, Port);
            await WaitReadyAsync(profile.Folder, child);
            checks["restricted_child"] = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(profile.Folder, "ready.json")));
            await RequireBlockedAsync();
            checks["baseline_inbound_blocked"] = true;
            outboundAdded = true;
            await RunToolAsync("CheckNetIsolation.exe", "LoopbackExempt", "-a", "-p=" + profile.Sid);
            await RequireBlockedAsync();
            checks["outbound_exemption_does_not_allow_inbound"] = true;
            inbound = StartTool("CheckNetIsolation.exe", "LoopbackExempt", "-is", "-p=" + profile.Sid);
            await WaitReachableAsync(inbound);
            await VerifyHttpAsync();
            checks["inbound_session_html_api_sessions"] = true;
            Stop(inbound);
            await inbound.WaitForExitAsync();
            File.WriteAllText(Path.Combine(results, "inbound-session.txt"), await inbound.StandardOutput.ReadToEndAsync() + await inbound.StandardError.ReadToEndAsync());
            await RequireBlockedAsync();
            checks["ending_inbound_session_restores_isolation"] = true;
            passed = true;
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            Stop(inbound);
            Stop(child);
            if (child != null) { await child.WaitForExitAsync(); child.Dispose(); }
            if (inbound != null)
                File.WriteAllText(Path.Combine(results, "inbound-session.txt"), await inbound.StandardOutput.ReadToEndAsync() + await inbound.StandardError.ReadToEndAsync());
            if (firewallAdded)
                await RunToolAsync("WindowsPowerShell/v1.0/powershell.exe", "-NoProfile", "-NonInteractive", "-Command", $"Remove-NetFirewallRule -Name '{profile.Name}' -ErrorAction SilentlyContinue");
            if (outboundAdded) await RunToolAsync("CheckNetIsolation.exe", "LoopbackExempt", "-d", "-p=" + profile.Sid);
            foreach (var name in new[] { "listener.log", "ready.json", "failure.txt" })
                if (File.Exists(Path.Combine(profile.Folder, name))) File.Copy(Path.Combine(profile.Folder, name), Path.Combine(results, name), true);
            await RunToolAsync("icacls.exe", AppContext.BaseDirectory, "/remove:g", "*" + profile.Sid, "/T", "/Q");
            inbound?.Dispose();
            var report = new { passed, failure, checks, profile = profile.Name, sid = profile.Sid, os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), port = Port, appModel = "Win32 .NET 10 process in real AppContainer; not Xamarin/UWP or a WebView" };
            File.WriteAllText(Path.Combine(results, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }

    private static async Task ServeAsync(int port, string folder, string expectedSid)
    {
        var sid = ContainerProfile.CurrentContainerSid();
        Require(sid == expectedSid, "AppContainer SID mismatch");
        var html = Path.Combine(folder, "html");
        Directory.CreateDirectory(html);
        File.WriteAllText(Path.Combine(html, "index.html"), "<!doctype html><title>EmbedIO</title><p>appcontainer-554</p>");
        using var trace = new TextWriterTraceListener(Path.Combine(folder, "listener.log"));
        Log.Source.Listeners.Add(trace);
        Log.Source.Switch.Level = SourceLevels.Verbose;
        Trace.AutoFlush = true;
        using var stop = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix($"http://*:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithLocalSessionManager()
            .WithWebApi("/api", m => m.WithController<ProbeController>())
            .WithStaticFolder("/", html, true, m => m.WithContentCaching(true));
        server.StateChanged += (_, e) =>
        {
            if (e.NewState == WebServerState.Listening)
                File.WriteAllText(Path.Combine(folder, "ready.json"), JsonSerializer.Serialize(new { state = "Listening", sid, appContainer = true, pid = Environment.ProcessId, runtime = Environment.Version.ToString(), prefix = $"http://*:{port}/" }));
        };
        await server.RunAsync(stop.Token);
    }

    private static async Task VerifyHttpAsync()
    {
        using var handler = new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(5) };
        Require((await client.GetStringAsync("/")).Contains("appcontainer-554", StringComparison.Ordinal), "Missing HTML");
        var first = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("api/probe"));
        var second = JsonSerializer.Deserialize<JsonElement>(await client.GetStringAsync("api/probe"));
        Require(first.GetProperty("session").GetString() == second.GetProperty("session").GetString(), "Session continuity failed");
        Require(IPAddress.Parse(first.GetProperty("localAddress").GetString()!).MapToIPv4().Equals(IPAddress.Loopback), "Unexpected actual request endpoint");
        Require(!string.IsNullOrEmpty(first.GetProperty("session").GetString()), "Missing session");
    }

    private static async Task RequireBlockedAsync()
    {
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{Port}/");
            throw new InvalidOperationException("Expected inbound isolation, received HTTP " + response.StatusCode);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    private static async Task WaitReachableAsync(Process allowance)
    {
        for (var i = 0; i < 30; i++)
        {
            if (allowance.HasExited) throw new InvalidOperationException("Inbound allowance exited: " + await allowance.StandardOutput.ReadToEndAsync() + await allowance.StandardError.ReadToEndAsync());
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(1) };
            try { if ((await client.GetStringAsync($"http://127.0.0.1:{Port}/")).Contains("appcontainer-554", StringComparison.Ordinal)) return; }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("Inbound allowance did not permit HTTP");
    }

    private static async Task WaitReadyAsync(string folder, Process child)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(Path.Combine(folder, "failure.txt"))) throw new InvalidOperationException(File.ReadAllText(Path.Combine(folder, "failure.txt")));
            if (File.Exists(Path.Combine(folder, "ready.json"))) return;
            if (child.HasExited) throw new InvalidOperationException("Restricted child exited: " + child.ExitCode);
            await Task.Delay(200);
        }
        throw new TimeoutException("Restricted listener did not report Listening");
    }

    private static Process StartTool(string name, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Unable to start " + name);
    }
    private static async Task RunToolAsync(string name, params string[] args)
    {
        using var process = StartTool(name, args);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var text = await output + await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(name + ": " + text);
    }
    private static void Stop(Process? process) { if (process != null && !process.HasExited) process.Kill(true); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}

public sealed class ProbeController : WebApiController
{
    [Route(HttpVerbs.Get, "/probe")]
    public object Probe()
    {
        HttpContext.Session["visits"] = HttpContext.Session.GetValue<int>("visits") + 1;
        return new { session = HttpContext.Session.Id, localAddress = HttpContext.Request.LocalEndPoint.Address.ToString() };
    }
}
