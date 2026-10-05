using System.Diagnostics;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Routing;

internal static class ColdStart
{
    private sealed class DelayedServer : WebServerBase<WebServerOptions>
    {
        protected override void Prepare(CancellationToken token) => Thread.Sleep(50);
        protected override Task ProcessRequestsAsync(CancellationToken token) => Task.CompletedTask;
        protected override void OnFatalException() { }
    }

    internal static bool Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--verify-cold-start")
        {
            foreach (var workload in new[] { "root", "base-100", "modules-100", "parameter-100", "server", "start-delayed" })
            {
                for (var sample = 0; sample < 3; sample++)
                {
                    var info = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };
                    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        info.ArgumentList.Add(typeof(ColdStart).Assembly.Location);
                    info.ArgumentList.Add("--cold-start");
                    info.ArgumentList.Add(workload);
                    using var child = Process.Start(info)!;
                    var output = child.StandardOutput.ReadToEndAsync();
                    var errors = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(30000))
                    {
                        child.Kill(entireProcessTree: true);
                        throw new TimeoutException("Cold-start child exceeded 30 seconds.");
                    }
                    var text = output.GetAwaiter().GetResult();
                    if (child.ExitCode != 0) throw new Exception(errors.GetAwaiter().GetResult());
                    using var data = JsonDocument.Parse(text);
                    var bytes = data.RootElement.GetProperty("allocatedBytes").GetInt64();
                    var limit = workload switch
                    {
                        "base-100" => 250000,
                        "modules-100" => 300000,
                        "parameter-100" => 1500000,
                        "start-delayed" => 30000,
                        _ => 80000
                    };
                    Console.WriteLine(text.Trim() + " ceiling=" + limit);
                    if (bytes > limit) throw new Exception(workload + " exceeded its cold-start allocation ceiling.");
                }
            }
            return true;
        }
        if (args.Length != 2 || args[0] != "--cold-start") return false;
        Action operation = args[1] switch
        {
            "start-delayed" => () => { using var server = new DelayedServer(); server.Start(); }
            ,
            "root" => () => GC.KeepAlive(RouteMatcher.Parse("/", true)),
            "base-100" => () =>
            {
                for (var i = 0; i < 100; i++)
                    GC.KeepAlive(RouteMatcher.Parse("/mount-" + i + "/", true));
            }
            ,
            "parameter-100" => () =>
            {
                for (var i = 0; i < 100; i++)
                    GC.KeepAlive(RouteMatcher.Parse("/items-" + i + "/{id}", false));
            }
            ,
            "server" => () => { using var server = new WebServer("http://localhost:9696/"); }
            ,
            "modules-100" => () =>
            {
                for (var i = 0; i < 100; i++)
                    GC.KeepAlive(new ActionModule("/mount-" + i + "/", HttpVerbs.Any, _ => Task.CompletedTask));
            }
            ,
            _ => throw new ArgumentException("Unknown cold-start workload.")
        };
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        operation();
        var elapsed = Stopwatch.GetElapsedTime(started);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            workload = args[1],
            milliseconds = elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before
        }));
        return true;
    }
}
