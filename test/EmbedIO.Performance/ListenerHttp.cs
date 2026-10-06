using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;

internal static class ListenerHttp
{
    internal static bool Run(string[] args)
    {
        var verify = args.Contains("--verify-listener-http", StringComparer.Ordinal);
        if (!verify && !args.Contains("--listener-http", StringComparer.Ordinal)) return false;
        RunAsync(args, verify).GetAwaiter().GetResult();
        return true;
    }

    private static async Task RunAsync(string[] args, bool verify)
    {
        var requests = Integer(args, "--requests", verify ? 8 : 20);
        var rounds = Integer(args, "--rounds", verify ? 1 : 3);
        var payloadSize = Integer(args, "--payload-bytes", 1024);
        var bodySize = args.Contains("--request-body-bytes", StringComparer.Ordinal)
            ? Integer(args, "--request-body-bytes", 65536) : 0;
        var consumptionIndex = Array.IndexOf(args, "--body-consumption");
        var consumption = consumptionIndex < 0 ? "full"
            : consumptionIndex + 1 < args.Length ? args[consumptionIndex + 1] : string.Empty;
        if (consumption is not ("full" or "partial" or "none") || (consumptionIndex >= 0 && bodySize == 0))
            throw new ArgumentException("--body-consumption needs full, partial or none together with --request-body-bytes.");
        var concurrency = args.Contains("--concurrency", StringComparer.Ordinal)
            ? new[] { Integer(args, "--concurrency", 1) } : verify ? new[] { 4 } : new[] { 1, 16 };
        var policyIndex = Array.IndexOf(args, "--connection-policy");
        var policy = policyIndex < 0 ? bodySize > 0 && consumption != "full" ? "keep-alive" : "both" : policyIndex + 1 < args.Length ? args[policyIndex + 1] : string.Empty;
        if (policy is not ("both" or "keep-alive" or "close"))
            throw new ArgumentException("--connection-policy needs both, keep-alive or close.");
        if (bodySize > 0 && consumption != "full" && (policy != "keep-alive" || 8L + (long)requests * rounds >= 100))
            throw new ArgumentException("Partial/unread POST workloads need keep-alive and fewer than 100 requests per worker including eight warmups; the existing forced-close lifetime can reset a request body still being sent.");
        var churnModes = policy == "keep-alive" ? new[] { false } : policy == "close" ? new[] { true } : new[] { false, true };
        var retain = verify || args.Contains("--retain-connections", StringComparer.Ordinal);
        EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
        var results = new List<object>();
        foreach (var secure in new[] { false, true })
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            foreach (var churn in churnModes)
                foreach (var workers in concurrency)
                {
                    var url = HttpsSmoke.GetUrl();
                    if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
                    var payload = Enumerable.Repeat((byte)'x', payloadSize).ToArray();
                    var requestPayload = Enumerable.Repeat((byte)'b', bodySize).ToArray();
                    var connections = new ConcurrentDictionary<object, byte>();
                    var ports = new ConcurrentDictionary<int, byte>();
                    PropertyInfo? connectionProperty = null;
                    using var server = new WebServer(options => options.WithUrlPrefix(url)
                        .WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                        .WithModule(new ActionModule("/", bodySize > 0 ? HttpVerbs.Post : HttpVerbs.Get, async context =>
                        {
                            ports.TryAdd(context.Request.RemoteEndPoint.Port, 0);
                            if (retain)
                            {
                                connectionProperty ??= context.GetType().GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                                connections.TryAdd(connectionProperty.GetValue(context)!, 0);
                            }
                            if (bodySize > 0)
                            {
                                var toRead = consumption == "full" ? bodySize : consumption == "partial" ? bodySize / 2 : 0;
                                var buffer = new byte[Math.Min(Math.Max(toRead, 1), 8192)];
                                while (toRead > 0)
                                {
                                    var read = await context.Request.InputStream.ReadAsync(buffer, 0,
                                        Math.Min(buffer.Length, toRead), context.CancellationToken).ConfigureAwait(false);
                                    if (read == 0 || buffer.AsSpan(0, read).ContainsAnyExcept((byte)'b'))
                                        throw new InvalidOperationException("Request payload mismatch.");
                                    toRead -= read;
                                }
                            }
                            context.Response.KeepAlive = !churn;
                            context.Response.ContentLength64 = payload.Length;
                            await context.Response.OutputStream.WriteAsync(payload, context.CancellationToken).ConfigureAwait(false);
                        }));
                    using var stop = new CancellationTokenSource();
                    var running = server.RunAsync(stop.Token);
                    using var client = secure ? HttpsSmoke.CreateClient(certificate!) : new HttpClient();
                    client.DefaultRequestVersion = HttpVersion.Version11;
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    client.Timeout = TimeSpan.FromSeconds(20);
                    var rows = new List<object>();
                    try
                    {
                        async Task Request()
                        {
                            using var request = new HttpRequestMessage(bodySize > 0 ? HttpMethod.Post : HttpMethod.Get, url);
                            if (bodySize > 0) request.Content = new ByteArrayContent(requestPayload);
                            request.Headers.ConnectionClose = churn;
                            using var response = await client.SendAsync(request, stop.Token).ConfigureAwait(false);
                            response.EnsureSuccessStatusCode();
                            var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token).ConfigureAwait(false);
                            if (bytes.Length != payload.Length || !bytes.AsSpan().SequenceEqual(payload))
                                throw new InvalidOperationException("Response payload mismatch.");
                        }
                        await Task.WhenAll(Enumerable.Range(0, workers).Select(async _ =>
                        {
                            for (var index = 0; index < 8; index++) await Request().ConfigureAwait(false);
                        }));
                        await Task.Delay(50);
                        ports.Clear();
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        var retainedBefore = GC.GetTotalMemory(false);
                        for (var round = 0; round < rounds; round++)
                        {
                            var samples = new double[workers * requests];
                            var gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
                            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                            var start = Stopwatch.GetTimestamp();
                            await Task.WhenAll(Enumerable.Range(0, workers).Select(async worker =>
                            {
                                for (var index = 0; index < requests; index++)
                                {
                                    var requestStart = Stopwatch.GetTimestamp();
                                    await Request().ConfigureAwait(false);
                                    samples[worker * requests + index] = Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;
                                }
                            }));
                            var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                            var allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
                            Array.Sort(samples);
                            rows.Add(new
                            {
                                round,
                                requests = samples.Length,
                                errors = 0,
                                requestsPerSecond = samples.Length / seconds,
                                p50Milliseconds = Percentile(samples, 0.50),
                                p95Milliseconds = Percentile(samples, 0.95),
                                p99Milliseconds = Percentile(samples, 0.99),
                                processAllocatedBytesPerRequest = allocated / (double)samples.Length,
                                collections = Enumerable.Range(0, 3).Select(index => GC.CollectionCount(index) - gcBefore[index]).ToArray()
                            });
                        }
                        stop.Cancel();
                        await running.WaitAsync(TimeSpan.FromSeconds(10));
                        client.Dispose();
                        server.Dispose();
                        await Task.Delay(100);
                        var openStreams = 0;
                        var activeTimers = 0;
                        foreach (var connection in connections.Keys)
                        {
                            var type = connection.GetType();
                            if (((Stream)type.GetProperty("Stream")!.GetValue(connection)!).CanRead) openStreams++;
                            var timer = (Timer)type.GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(connection)!;
                            try { if (timer.Change(Timeout.Infinite, Timeout.Infinite)) activeTimers++; }
                            catch (ObjectDisposedException) { }
                        }
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        using var process = Process.GetCurrentProcess();
                        process.Refresh();
                        results.Add(new
                        {
                            protocol = secure ? "https" : "http",
                            connectionPolicy = churn ? "close-per-request" : "keep-alive",
                            workers,
                            payloadBytes = payload.Length,
                            requestBodyBytes = bodySize,
                            bodyConsumption = bodySize > 0 ? consumption : null,
                            retainConnections = retain,
                            distinctMeasuredPeerPorts = ports.Count,
                            retainedConnections = connections.Count,
                            openStreamsAfterShutdown = retain ? openStreams : (int?)null,
                            undisposedTimersAfterShutdown = retain ? activeTimers : (int?)null,
                            managedBytesBeforeMeasurement = retainedBefore,
                            managedBytesAfterShutdownGc = GC.GetTotalMemory(false),
                            processPrivateBytesAfterShutdown = process.PrivateMemorySize64,
                            processWorkingSetBytesAfterShutdown = process.WorkingSet64,
                            rows
                        });
                        if (verify && (openStreams != 0 || activeTimers != 0))
                            throw new InvalidOperationException($"Shutdown retained {openStreams} transport wrappers and {activeTimers} timers.");
                    }
                    finally
                    {
                        stop.Cancel();
                        await running.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = 1,
            framework = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            note = "Loopback client and server share this process. Allocations/memory include both. Timing is informational; optional retained connections intentionally keep diagnostic references alive.",
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Percentile(double[] values, double percentile)
        => values[Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1)];

    private static int Integer(string[] args, string option, int fallback)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return fallback;
        if (index + 1 == args.Length || !int.TryParse(args[index + 1], out var value) || value <= 0 || value > 1000000)
            throw new ArgumentException($"{option} needs an integer between 1 and 1000000.");
        return value;
    }
}
