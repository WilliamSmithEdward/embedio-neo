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
            await server.RunAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= exit;
        }
    }
}
