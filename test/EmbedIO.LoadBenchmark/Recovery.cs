using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Cancellation and recovery against one long-lived server process:
// healthy load, an abort storm (clients reset mid-response and mid-upload),
// idle drain, healthy load again, optional sustained load with periodic
// snapshots, then a timed shutdown. Every healthy request is validated by the
// ordinary client; the storm only counts outcomes. Nothing is retried.
internal static class Recovery
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(CommandLine options)
    {
        var output = Path.GetFullPath(options.Required("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory: " + output);
        Directory.CreateDirectory(output);
        var serverDirectory = Path.GetFullPath(options.Text("--server-dir", AppContext.BaseDirectory));
        var engine = options.Text("--engine", "embedio");
        var protocol = Enum.Parse<Protocol>(options.Required("--protocol"), ignoreCase: true);
        var tls = options.Has("--tls") || protocol == Protocol.Http3;
        var connections = options.Integer("--connections", 16);
        var streams = options.Integer("--streams", protocol == Protocol.Http1 ? 1 : 8);
        var healthySeconds = options.Number("--healthy-seconds", 10);
        var stormSeconds = options.Number("--storm-seconds", 20);
        var stormWorkers = options.Integer("--storm-workers", 32);
        var idleSeconds = options.Number("--idle", 5);
        var sustainMinutes = options.Number("--sustain-minutes", 0);
        var snapshotSeconds = options.Number("--snapshot-interval", 60);
        var healthyPath = options.Text("--path", "/plaintext");
        var stormMode = options.Text("--storm-mode", "both");
        if (stormMode is not ("both" or "upload" or "download")) throw new ArgumentException("--storm-mode is both, upload or download.");

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(output, "localhost.pfx");
        string thumbprint;
        using (var certificate = Orchestrator.CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var port = Orchestrator.FreePort();
        var report = new JsonObject
        {
            ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["engine"] = engine,
            ["serverDirectory"] = serverDirectory,
            ["embedioSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(serverDirectory, "EmbedIO.dll")))),
            ["protocol"] = protocol.ToString(),
            ["tls"] = tls,
            ["port"] = port,
            ["settings"] = new JsonObject
            {
                ["connections"] = connections,
                ["streams"] = streams,
                ["healthyPath"] = healthyPath,
                ["healthySeconds"] = healthySeconds,
                ["stormSeconds"] = stormSeconds,
                ["stormWorkers"] = stormWorkers,
                ["idleSeconds"] = idleSeconds,
                ["sustainMinutes"] = sustainMinutes,
                ["snapshotIntervalSeconds"] = snapshotSeconds,
            },
        };
        var phases = new JsonArray();
        report["phases"] = phases;
        var timeout = TimeSpan.FromMinutes(2);
        var serverArguments = new List<string> { "server", "--engine", engine, "--protocol", protocol.ToString(), "--port", port.ToString(CultureInfo.InvariantCulture) };
        if (tls) serverArguments.AddRange(["--tls", "--certificate", certificatePath, "--certificate-password", password]);
        var server = ChildProcess.Start(serverDirectory, serverArguments, null, Path.Combine(output, "server.stderr.log"));
        var verdict = new JsonArray();
        report["verdict"] = verdict;
        try
        {
            var ready = await server.ReadLineAsync(timeout).ConfigureAwait(false);
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            report["server"] = JsonNode.Parse(ready[6..]);

            async Task<JsonNode?> Snapshot(string name)
            {
                await Task.Delay(TimeSpan.FromSeconds(idleSeconds)).ConfigureAwait(false);
                var snapshot = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
                if (OperatingSystem.IsMacOS())
                {
                    // Both ends of every loopback connection on the server port, for leak attribution.
                    var suffix = "." + port.ToString(CultureInfo.InvariantCulture) + " ";
                    var lines = SystemCpu.Command("netstat", "-an -p tcp").Split('\n').Where(line => line.Contains(suffix, StringComparison.Ordinal));
                    await File.WriteAllLinesAsync(Path.Combine(output, name + ".netstat.txt"), lines).ConfigureAwait(false);
                }
                phases.Add(new JsonObject
                {
                    ["phase"] = name,
                    ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["snapshot"] = snapshot?.DeepClone(),
                    ["serverSockets"] = Orchestrator.ServerSockets(port),
                });
                return snapshot;
            }

            async Task<JsonNode?> Healthy(string name, double seconds)
            {
                var arguments = new List<string>
                {
                    "client", "--protocol", protocol.ToString(), "--port", port.ToString(CultureInfo.InvariantCulture), "--path", healthyPath,
                    "--connections", connections.ToString(CultureInfo.InvariantCulture), "--streams", streams.ToString(CultureInfo.InvariantCulture),
                    "--certificate-thumbprint", thumbprint, "--warmup", "1", "--duration", seconds.ToString(CultureInfo.InvariantCulture),
                };
                if (tls) arguments.Add("--tls");
                var client = ChildProcess.Start(AppContext.BaseDirectory, arguments, null, Path.Combine(output, name + ".client.stderr.log"));
                var wait = TimeSpan.FromSeconds(seconds + 120);
                var warm = await client.ReadLineAsync(wait).ConfigureAwait(false);
                JsonNode? result = null;
                if (warm is not null && warm.StartsWith("WARM ", StringComparison.Ordinal) && JsonNode.Parse(warm[5..])?["error"] is null)
                {
                    if (await client.RequestAsync("start", wait).ConfigureAwait(false) == "DONE")
                        result = await client.RequestJsonAsync("report", wait).ConfigureAwait(false);
                }
                result ??= new JsonObject { ["error"] = "client did not complete: " + warm };
                result.AsObject().Remove("histogram");
                await client.WaitForExitAsync(wait).ConfigureAwait(false);
                await client.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                phases.Add(new JsonObject { ["phase"] = name, ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["client"] = result.DeepClone() });
                if (result["error"] is { } error) verdict.Add($"{name}: healthy client failed: {error}");
                return result;
            }

            var initial = await Snapshot("start").ConfigureAwait(false);
            var before = await Healthy("healthy-before", healthySeconds).ConfigureAwait(false);
            var afterHealthy = await Snapshot("after-healthy").ConfigureAwait(false);

            var storm = await Storm.RunAsync(protocol, tls, port, thumbprint, stormWorkers, TimeSpan.FromSeconds(stormSeconds), stormMode).ConfigureAwait(false);
            phases.Add(new JsonObject { ["phase"] = "abort-storm", ["utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["storm"] = storm });
            await Snapshot("after-storm-drain").ConfigureAwait(false);

            var after = await Healthy("healthy-after", healthySeconds).ConfigureAwait(false);
            var recovered = await Snapshot("after-recovery").ConfigureAwait(false);

            var sustained = new List<JsonNode?>();
            if (sustainMinutes > 0)
            {
                var deadline = Stopwatch.StartNew();
                for (var index = 1; deadline.Elapsed.TotalMinutes < sustainMinutes; index++)
                {
                    await Healthy("sustain-" + index.ToString(CultureInfo.InvariantCulture), snapshotSeconds).ConfigureAwait(false);
                    sustained.Add(await Snapshot("sustain-" + index.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false));
                }
            }

            double? Rate(JsonNode? client) => client?["requestsPerSecond"]?.GetValue<double>();
            if (Rate(before) is { } r0 && Rate(after) is { } r1)
            {
                report["recoveryThroughputRatio"] = r1 / r0;
                if (r1 < r0 * 0.8) verdict.Add(FormattableString.Invariant($"recovery throughput {r1:F0} req/s is below 80% of before ({r0:F0})"));
            }

            long? Get(JsonNode? snapshot, string name) => snapshot?[name]?.GetValue<long>();
            var growth = new JsonObject();
            foreach (var metric in new[] { "managedHeapBytes", "workingSetBytes", "handles", "threads", "threadPoolThreads", "gcCommittedBytes" })
                growth[metric] = Get(recovered, metric) - Get(afterHealthy, metric);
            report["retainedAfterStormAndRecovery"] = growth;
            if (Get(recovered, "handles") - Get(afterHealthy, "handles") > 64) verdict.Add("open descriptors grew by more than 64 across the storm");
            var openSockets = Orchestrator.ServerSockets(port).Where(pair => pair.Key != "TimeWait").Sum(pair => pair.Value?.GetValue<int>() ?? 0);
            report["openServerSocketsAfterRecovery"] = openSockets;
            if (openSockets != 0) verdict.Add($"{openSockets} non-TIME_WAIT server sockets remain after recovery");
            if (sustained.Count >= 3)
            {
                // Least-squares slope per snapshot of the retained managed heap and descriptors.
                report["sustainedSlopePerSnapshot"] = new JsonObject
                {
                    ["managedHeapBytes"] = Slope(sustained.Select(s => (double?)Get(s, "managedHeapBytes")).ToArray()),
                    ["workingSetBytes"] = Slope(sustained.Select(s => (double?)Get(s, "workingSetBytes")).ToArray()),
                    ["handles"] = Slope(sustained.Select(s => (double?)Get(s, "handles")).ToArray()),
                    ["threads"] = Slope(sustained.Select(s => (double?)Get(s, "threads")).ToArray()),
                };
                // Continuing growth under steady load, after GC: native (working set) or managed.
                if (Slope(sustained.Select(s => (double?)Get(s, "workingSetBytes")).ToArray()) is > 8.0 * (1 << 20) and var ws)
                    verdict.Add(FormattableString.Invariant($"working set grows {ws / (1 << 20):F1} MiB per snapshot under sustained load"));
                if (Slope(sustained.Select(s => (double?)Get(s, "managedHeapBytes")).ToArray()) is > 1.0 * (1 << 20) and var heap)
                    verdict.Add(FormattableString.Invariant($"managed heap grows {heap / (1 << 20):F1} MiB per snapshot under sustained load"));
            }

            _ = initial;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException)
        {
            report["error"] = exception.Message;
            verdict.Add("driver error: " + exception.Message);
        }
        finally
        {
            var stopping = Stopwatch.StartNew();
            try
            {
                var stop = await server.RequestJsonAsync("exit", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                report["stop"] = stop;
                report["serverExitCode"] = await server.WaitForExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or JsonException or InvalidOperationException)
            {
                report["stopError"] = exception.Message;
                verdict.Add(FormattableString.Invariant($"shutdown did not complete within {stopping.Elapsed.TotalSeconds:F0} s: {exception.Message}"));
            }

            report["serverKilled"] = await server.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            report["shutdownSeconds"] = stopping.Elapsed.TotalSeconds;
            report["endedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(Path.Combine(output, "recovery.json"), report.ToJsonString(Indented)).ConfigureAwait(false);
        }

        Control.Write(verdict.Count == 0 ? "RECOVERY OK " + output : "RECOVERY FINDINGS " + string.Join(" | ", verdict.Select(item => item?.ToString())));
        return verdict.Count == 0 ? 0 : 1;
    }

    private static double? Slope(double?[] values)
    {
        if (values.Any(value => value is null)) return null;
        var n = values.Length;
        var meanX = (n - 1) / 2.0;
        var meanY = values.Average(value => value.GetValueOrDefault());
        double numerator = 0, denominator = 0;
        for (var i = 0; i < n; i++)
        {
            numerator += (i - meanX) * (values[i].GetValueOrDefault() - meanY);
            denominator += (i - meanX) * (i - meanX);
        }

        return numerator / denominator;
    }
}

// Clients that abandon work midway. HTTP/1.1 resets the TCP connection (linger 0)
// after part of a 1 MiB response or part of a 1 MiB upload. HTTP/2 and HTTP/3 cancel
// individual streams the same way (RST_STREAM / STOP_SENDING + RESET_STREAM) and,
// every 16th iteration, drop the whole connection without a graceful close.
internal static class Storm
{
    private const int Mebibyte = 1 << 20;
    private const int Partial = 64 * 1024;

    internal static async Task<JsonObject> RunAsync(Protocol protocol, bool tls, int port, string thumbprint, int workers, TimeSpan duration, string mode)
    {
        Payloads.Prepare(Mebibyte);
        long attempts = 0, aborted = 0, connectFailures = 0, unexpected = 0;
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        void Unexpected(Exception error)
        {
            Interlocked.Increment(ref unexpected);
            errors.AddOrUpdate(error.GetType().Name + ": " + error.Message[..Math.Min(error.Message.Length, 120)], 1, static (_, count) => count + 1);
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(worker => Task.Run(async () =>
        {
            var iteration = 0;
            while (clock.Elapsed < duration)
            {
                iteration++;
                Interlocked.Increment(ref attempts);
                var upload = mode == "upload" || (mode == "both" && (iteration + worker) % 2 == 0);
                try
                {
                    if (protocol == Protocol.Http1) await Http1AbortAsync(tls, port, thumbprint, upload).ConfigureAwait(false);
                    else await MultiplexedAbortAsync(protocol, tls, port, thumbprint, upload, dropConnection: iteration % 16 == 0).ConfigureAwait(false);
                    Interlocked.Increment(ref aborted);
                }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    Interlocked.Increment(ref connectFailures);
                    Unexpected(error);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException))
                {
                    // Any client-side failure is counted by kind; the storm never aborts the run.
                    Unexpected(error);
                }
            }
        }))).ConfigureAwait(false);

        return new JsonObject
        {
            ["mode"] = mode,
            ["seconds"] = clock.Elapsed.TotalSeconds,
            ["attempts"] = attempts,
            ["abortedAsIntended"] = aborted,
            ["connectRefused"] = connectFailures,
            ["unexpectedErrors"] = unexpected,
            ["errorKinds"] = new JsonObject(errors.OrderByDescending(pair => pair.Value).Take(10).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value))),
        };
    }

    private static async Task Http1AbortAsync(bool tls, int port, string thumbprint, bool upload)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        try
        {
            await using var network = new NetworkStream(socket, ownsSocket: false);
            Stream stream = network;
            SslStream? secure = null;
            if (tls)
            {
                secure = new SslStream(network, leaveInnerStreamOpen: true, LoadClient.Pinned(thumbprint));
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", ApplicationProtocols = [SslApplicationProtocol.Http11] }).ConfigureAwait(false);
                stream = secure;
            }

            await using (secure)
            {
                var host = "localhost:" + port.ToString(CultureInfo.InvariantCulture);
                if (upload)
                {
                    var head = Encoding.ASCII.GetBytes($"POST /upload HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/octet-stream\r\nContent-Length: {Mebibyte}\r\n\r\n");
                    await stream.WriteAsync(head).ConfigureAwait(false);
                    await stream.WriteAsync(Payloads.Get(Mebibyte).AsMemory(0, Partial * 4)).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                else
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /bytes/{Mebibyte} HTTP/1.1\r\nHost: {host}\r\n\r\n")).ConfigureAwait(false);
                    var buffer = new byte[16384];
                    long received = 0;
                    while (received < Partial)
                    {
                        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0) throw new IOException("Server closed before the partial response.");
                        received += read;
                    }
                }
            }
        }
        finally
        {
            // Linger 0 sends RST instead of FIN: an abrupt client disappearance.
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
    }

    private static async Task MultiplexedAbortAsync(Protocol protocol, bool tls, int port, string thumbprint, bool upload, bool dropConnection)
    {
        var version = protocol == Protocol.Http3 ? HttpVersion.Version30 : HttpVersion.Version20;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = LoadClient.Pinned(thumbprint) },
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var target = $"{(tls ? "https" : "http")}://localhost:{port}" + (upload ? "/upload" : $"/bytes/{Mebibyte}");
        // Several streams per connection, each abandoned midway.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var cancel = new CancellationTokenSource();
            // One Uri per request: a shared instance raised NullReferenceException inside
            // HttpClient (Uri.EnsureHostString) under concurrent HTTP/3 sends on .NET 10.0.12.
            using var request = new HttpRequestMessage(upload ? HttpMethod.Post : HttpMethod.Get, new Uri(target)) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (upload)
            {
                request.Content = new StallingContent(Payloads.Get(Mebibyte).AsMemory(0, Partial * 4), cancel);
                try { using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (HttpRequestException) when (cancel.IsCancellationRequested) { }
                return;
            }

            using var download = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).ConfigureAwait(false);
            if (download.StatusCode != HttpStatusCode.OK || download.Version != version) throw new InvalidDataException($"Unexpected {(int)download.StatusCode} HTTP/{download.Version}");
            await using var body = await download.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
            var buffer = new byte[16384];
            long received = 0;
            while (received < Partial)
            {
                var read = await body.ReadAsync(buffer, cancel.Token).ConfigureAwait(false);
                if (read == 0) throw new IOException("Stream ended before the partial response.");
                received += read;
            }

            await cancel.CancelAsync().ConfigureAwait(false); // Abandon the rest of the stream.
        })).ConfigureAwait(false);
        if (dropConnection) handler.Dispose();
    }

    // Sends part of a body, then cancels the request while the server still expects more.
    private sealed class StallingContent(ReadOnlyMemory<byte> prefix, CancellationTokenSource cancel) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(prefix).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false);
            await Task.Delay(Timeout.Infinite, cancel.Token).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Mebibyte;
            return true;
        }
    }
}
