using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Health;

// Finite JSON responses over real HTTP/1.1: first miss, warmed hit, and policy bypass.
// This is a workload comparison, not a universal speedup or cold-process benchmark.
internal static class OutputCacheTiming
{
    internal static bool Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--output-cache-latency") return false;
        var samples = args.Length == 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1000;
        if (args.Length > 2 || samples is < 100 or > 10000) throw new ArgumentException("Use --output-cache-latency [samples:100..10000].");
        RunAsync(samples).GetAwaiter().GetResult(); return true;
    }

    private static async Task RunAsync(int samples)
    {
        EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var cache = new OutputCache(); var policy = new OutputCachePolicy(TimeSpan.FromMinutes(2));
        var calls = new Dictionary<string, int>();
        using var server = new WebServer($"http://127.0.0.1:{port}/").PreferNoCompressionFor("application/json");
        server.WithModule(new ActionModule(context =>
        {
            var mode = context.Request.Url.AbsolutePath;
            return mode == "/uncached" ? Write(context, mode) : cache.ExecuteAsync(context, policy, capture => Write(capture, mode));
        }));
        Task Write(IHttpContext context, string mode)
        {
            calls.TryGetValue(mode, out var count); calls[mode] = count + 1;
            var rows = Enumerable.Range(0, 128).Select(i => new { id = i, name = "Product " + i.ToString(CultureInfo.InvariantCulture), available = true }).ToArray();
            return ResponseSerializer.Json(context, rows);
        }
        using var ready = new HealthCheckService(server); var running = server.RunAsync(stop.Token);
        try
        {
            await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
            using var handler = new SocketsHttpHandler { UseProxy = false, UseCookies = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            // Initialize connection, controller-independent JSON metadata and transport first.
            await Request("/uncached", false);
            var firstMiss = await Request("/cached", false);
            Console.WriteLine(JsonSerializer.Serialize(new { scenario = "cached-first-miss-after-control", milliseconds = firstMiss, runtime = Environment.Version.ToString() }));
            for (var round = 0; round < 4; round++)
            {
                var modes = round % 2 == 0 ? new[] { "uncached", "cached", "bypass" } : new[] { "bypass", "cached", "uncached" };
                foreach (var mode in modes)
                {
                    var path = "/" + mode;
                    for (var i = 0; i < 100; i++) await Request(path, mode == "bypass");
                    var timings = new double[samples];
                    for (var i = 0; i < samples; i++) timings[i] = await Request(path, mode == "bypass");
                    Array.Sort(timings);
                    Console.WriteLine(JsonSerializer.Serialize(new { scenario = mode, round, samples, p50Milliseconds = timings[(int)(samples * .50)], p95Milliseconds = timings[(int)(samples * .95)], p99Milliseconds = timings[(int)(samples * .99)], maximumMilliseconds = timings[^1], actionExecutions = calls.GetValueOrDefault(path) }));
                }
            }
            if (calls["/cached"] != 1 || cache.Count != 1 || calls["/bypass"] != 4 * (100 + samples)) throw new InvalidDataException("Benchmark cache execution counts differ.");

            async Task<double> Request(string path, bool bypass)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                if (bypass) request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                var started = Stopwatch.GetTimestamp();
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                using var document = JsonDocument.Parse(bytes);
                if (response.StatusCode != HttpStatusCode.OK || document.RootElement.GetArrayLength() != 128 || document.RootElement[127].GetProperty("id").GetInt32() != 127)
                    throw new InvalidDataException("Unexpected benchmark representation.");
                return elapsed;
            }
        }
        finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
