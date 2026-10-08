using System.Diagnostics;
using System.Text.Json;
using EmbedIO;
using EmbedIO.PlatformTests;

internal static class BenchmarkHost
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--benchmark-endpoints", StringComparer.Ordinal)) return false;
        RunAsync(args).GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--url");
        var url = index < 0 ? "http://127.0.0.1:8080/" : index + 1 < args.Length
            ? args[index + 1] : throw new ArgumentException("--url requires a listener prefix.");
        var mode = args.Contains("--microsoft", StringComparer.Ordinal)
            ? HttpListenerMode.Microsoft : HttpListenerMode.EmbedIO;
        EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Off;
        using var stop = new CancellationTokenSource();
        using var server = BenchmarkEndpoints.CreateServer(url, mode);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        EventHandler exit = (_, _) => stop.Cancel();
        Console.CancelKeyPress += cancel;
        AppDomain.CurrentDomain.ProcessExit += exit;
        try
        {
            Console.WriteLine($"Benchmark endpoints: {url}json and {url}plaintext; {mode}. Ctrl+C stops the host.");
            var running = server.RunAsync(stop.Token);
            if (args.Contains("--measure", StringComparer.Ordinal))
            {
                if (await Console.In.ReadLineAsync(stop.Token) != "start") throw new InvalidOperationException("Expected start.");
                using var process = Process.GetCurrentProcess();
                var cpu = process.TotalProcessorTime;
                var bytes = GC.GetTotalAllocatedBytes(true);
                var collections = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                var clock = Stopwatch.StartNew();
                BenchmarkControl.Write("MEASURING");
                if (await Console.In.ReadLineAsync(stop.Token) != "stop") throw new InvalidOperationException("Expected stop.");
                clock.Stop();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    elapsedSeconds = clock.Elapsed.TotalSeconds,
                    cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - bytes,
                    collections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - collections[i]).ToArray(),
                    note = "Server process only; synchronized stdin control after client warmup. Includes all managed process allocations within the window."
                }));
                stop.Cancel();
            }
            await running.ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= exit;
        }
    }
}
