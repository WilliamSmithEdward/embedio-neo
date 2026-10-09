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
        var chunkSize = args.Contains("--chunk-bytes", StringComparer.Ordinal) ? Integer(args, "--chunk-bytes", 1024) : 0;
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
                                connectionProperty ??= context.GetType().GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic);
                                connections.TryAdd(((((connectionProperty) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetValue(context)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")), 0);
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
                            if (chunkSize == 0)
                            {
                                context.Response.ContentLength64 = payload.Length;
                                await context.Response.OutputStream.WriteAsync(payload, context.CancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                context.Response.SendChunked = true;
                                for (var offset = 0; offset < payload.Length; offset += chunkSize)
                                    await context.Response.OutputStream.WriteAsync(payload, offset,
                                        Math.Min(chunkSize, payload.Length - offset), context.CancellationToken).ConfigureAwait(false);
                            }
                        }));
                    using var stop = new CancellationTokenSource();
                    var running = server.RunAsync(stop.Token);
                    using var client = secure ? HttpsSmoke.CreateClient(((certificate) ?? throw new System.InvalidOperationException("Expected a non-null test value."))) : new HttpClient();
                    client.DefaultRequestVersion = HttpVersion.Version11;
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                    client.Timeout = TimeSpan.FromSeconds(20);
                    var rows = new List<object>();
                    var phase = "warmup";
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
                            phase = $"measurement round {round}";
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
                        phase = "shutdown verification";
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
                            if (((Stream)((((type.GetProperty("Stream")) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetValue(connection)) ?? throw new System.InvalidOperationException("Expected a non-null test value."))).CanRead) openStreams++;
                            var timer = (Timer)((((type.GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).GetValue(connection)) ?? throw new System.InvalidOperationException("Expected a non-null test value."));
                            try { if (((timer) ?? throw new System.InvalidOperationException("Expected a non-null test value.")).Change(Timeout.Infinite, Timeout.Infinite)) activeTimers++; }
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
                            responseChunkBytes = chunkSize,
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
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(
                            $"HTTP workload failed: url={url}, phase={phase}, closePerRequest={churn}, workers={workers}, "
                            + $"requestBodyBytes={bodySize}, bodyConsumption={consumption}, responseBytes={payloadSize}, "
                            + $"responseChunkBytes={chunkSize}, observedPeerPorts={ports.Count}, retainedConnections={connections.Count}, "
                            + $"serverState={server.State}, serverTask={running.Status}, diagnostics={CaptureFailureState(connections)}.", error);
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

    private static string CaptureFailureState(ConcurrentDictionary<object, byte> retained)
    {
        // Run only after failure. Never read payloads, alter timers or hold a registry
        // lock indefinitely while diagnosing a possible stalled transport.
        try
        {
            static object? Field(object value, string name)
                => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value);
            var connections = new HashSet<object>(retained.Take(32).Select(pair => pair.Key));
            var endpoints = new List<object>();
            var registry = typeof(EmbedIO.Net.EndPointManager).GetField("IPToEndpoints", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                as System.Collections.IDictionary;
            if (registry != null)
                foreach (System.Collections.IDictionary ports in registry.Values)
                    foreach (var endpoint in ports.Values)
                    {
                        if (endpoint == null || endpoints.Count == 32) continue;
                        var owner = endpoint.GetType().GetProperty("Listener", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint);
                        if (owner != null && Field(owner, "_connections") is System.Collections.IEnumerable registered)
                        {
                            var sampled = 0;
                            foreach (var entry in registered)
                            {
                                var connection = entry?.GetType().GetProperty("Key")?.GetValue(entry);
                                if (connection != null) connections.Add(connection);
                                if (++sampled == 32) break;
                            }
                        }
                        var pending = Field(endpoint, "_unregistered");
                        var busy = false;
                        if (pending != null)
                        {
                            var entered = false;
                            try
                            {
                                entered = Monitor.TryEnter(pending, TimeSpan.FromMilliseconds(5));
                                if (entered && pending is System.Collections.IEnumerable items)
                                {
                                    var sampled = 0;
                                    foreach (var connection in items)
                                    {
                                        if (connection != null) connections.Add(connection);
                                        if (++sampled == 32) break;
                                    }
                                }
                                busy = !entered;
                            }
                            finally { if (entered) Monitor.Exit(pending); }
                        }
                        endpoints.Add(new
                        {
                            worker = (Field(endpoint, "_acceptWorker") as Task)?.Status.ToString(),
                            admissionStopped = Field(endpoint, "_acceptingStopped"),
                            pendingRegistryBusy = busy
                        });
                    }
            var live = connections.Where(connection => Field(connection, "_resourcesDisposed") is not 1).Take(32)
                .Select(connection =>
                {
                    var stream = connection.GetType().GetProperty("Stream")?.GetValue(connection) as Stream;
                    var input = Field(connection, "_iStream");
                    return new
                    {
                        resourcesDisposed = Field(connection, "_resourcesDisposed"),
                        contextBound = Field(connection, "_contextBound"),
                        readBufferAllocated = Field(connection, "_buffer") is byte[],
                        reuses = connection.GetType().GetProperty("Reuses")?.GetValue(connection),
                        tlsAuthenticated = (stream as System.Net.Security.SslStream)?.IsAuthenticated,
                        remainingBody = input == null ? null : Field(input, "_remainingBody"),
                        responseFinishing = Field(connection, "_responseFinishing")
                    };
                }).ToArray();
            return JsonSerializer.Serialize(new
            {
                threadPoolThreads = ThreadPool.ThreadCount,
                pendingWorkItems = ThreadPool.PendingWorkItemCount,
                completedWorkItems = ThreadPool.CompletedWorkItemCount,
                retainedConnections = retained.Count,
                sampledConnections = connections.Count,
                liveConnectionSample = live,
                endpoints
            });
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or TargetInvocationException or InvalidCastException)
        { return $"Snapshot unavailable: {error.GetType().FullName}"; }
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
