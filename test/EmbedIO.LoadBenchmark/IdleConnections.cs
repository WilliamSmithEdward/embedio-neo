using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Per-connection memory of idle keep-alive connections: each engine serves one
// request per connection, then holds the connections idle while the server
// reports collected heap, working set, private bytes, handles and threads.
// Any protocol and engine the server host supports; TLS uses the throwaway
// loopback certificate like the load comparison.
internal static class IdleConnections
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(CommandLine options)
    {
        var output = Path.GetFullPath(options.Required("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory so earlier evidence is never overwritten: " + output);
        Directory.CreateDirectory(output);
        var candidate = Path.GetFullPath(options.Text("--candidate-dir", AppContext.BaseDirectory));
        var baseline = options.Optional("--baseline-dir") is { } path ? Path.GetFullPath(path) : null;
        var protocol = Enum.Parse<Protocol>(options.Text("--protocol", "Http1"), ignoreCase: true);
        var tls = options.Has("--tls") || protocol == Protocol.Http3;
        var connections = options.Integer("--connections", 1000);
        var hold = options.Number("--hold", 5);
        var settle = options.Number("--settle", 2);
        var rounds = options.Integer("--rounds", 1);
        var serverCpus = options.Optional("--server-cpus");
        if (connections < 1 || hold < 0 || settle < 0 || rounds < 1) throw new ArgumentOutOfRangeException(nameof(options), "Invalid idle settings.");
        if (serverCpus is not null && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new InvalidOperationException("--server-cpus is supported only on Windows and Linux.");
        var engineFilter = options.Optional("--engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var targets = new List<EngineTarget> { new("candidate", "embedio", candidate) };
        if (baseline is not null) targets.Add(new("baseline", "embedio", baseline));
        targets.Add(new("kestrel", "kestrel", candidate));
        if (engineFilter is not null) targets = [.. targets.Where(target => engineFilter.Contains(target.Name, StringComparer.Ordinal))];
        if (targets.Count == 0) throw new ArgumentException("No engine selected.", nameof(options));

        var certificateDirectory = Path.Combine(output, "certificate");
        Directory.CreateDirectory(certificateDirectory);
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(certificateDirectory, "localhost.pfx");
        string thumbprint;
        using (var certificate = Orchestrator.CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var samples = new List<JsonObject>();
        var failures = 0;
        try
        {
            for (var round = 1; round <= rounds; round++)
            {
                var engines = targets.ToList();
                if (round % 2 == 0) engines.Reverse();
                foreach (var target in engines)
                {
                    var sample = await SampleAsync(target, round, output, protocol, tls, connections, hold, settle, serverCpus, certificatePath, password, thumbprint).ConfigureAwait(false);
                    samples.Add(sample);
                    if (sample["error"] is not null) failures++;
                    Console.Out.WriteLine(Describe(sample));
                }
            }
        }
        finally
        {
            File.Delete(certificatePath);
            Directory.Delete(certificateDirectory);
        }

        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), new JsonArray([.. samples.Select(sample => (JsonNode)sample.DeepClone())]).ToJsonString(Indented)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), Markdown(samples)).ConfigureAwait(false);
        Console.Out.WriteLine($"{samples.Count} idle samples, {failures} failed. Output: {output}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<JsonObject> SampleAsync(EngineTarget target, int round, string output, Protocol protocol, bool tls, int connections,
        double hold, double settle, string? serverCpus, string certificatePath, string password, string thumbprint)
    {
        var stem = Path.Combine(output, $"{target.Name}-{protocol}{(tls ? "-tls" : "")}-c{connections}-r{round}");
        var sample = new JsonObject
        {
            ["engine"] = target.Name,
            ["round"] = round,
            ["protocol"] = protocol.ToString(),
            ["tls"] = tls,
            ["requestedConnections"] = connections,
            ["holdSeconds"] = hold,
            ["settleSeconds"] = settle,
            ["serverCpus"] = serverCpus,
            ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        var port = Orchestrator.FreePort();
        var arguments = new List<string> { "server", "--engine", target.Engine, "--protocol", protocol.ToString(), "--port", port.ToString(CultureInfo.InvariantCulture) };
        if (tls) arguments.AddRange(["--tls", "--certificate", certificatePath, "--certificate-password", password]);
        var timeout = TimeSpan.FromSeconds(hold + settle + 120);
        ChildProcess? server = null;
        var clients = new List<HttpClient>(connections);
        try
        {
            server = ChildProcess.Start(target.Directory, arguments, serverCpus, stem + ".server.stderr.log");
            var ready = await server.ReadLineAsync(timeout).ConfigureAwait(false);
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            sample["server"] = JsonNode.Parse(ready[6..]);
            sample["before"] = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);

            var scheme = tls ? "https" : "http";
            var url = new Uri($"{scheme}://localhost:{port.ToString(CultureInfo.InvariantCulture)}/plaintext");
            var version = protocol switch { Protocol.Http1 => HttpVersion.Version11, Protocol.Http2 => HttpVersion.Version20, _ => HttpVersion.Version30 };
            var opened = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            // One client per connection: each pool holds exactly one connection that
            // stays open and idle after its single request.
            foreach (var batch in Enumerable.Range(0, connections).Chunk(32))
            {
                var batchClients = batch.Select(_ => CreateClient(tls, thumbprint, version)).ToArray();
                clients.AddRange(batchClients);
                await Task.WhenAll(batchClients.Select(async client =>
                {
                    using var response = await client.GetAsync(url).ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.OK || response.Version != version)
                        throw new InvalidOperationException($"Unexpected response {(int)response.StatusCode} {response.Version}.");
                    _ = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref opened);
                })).ConfigureAwait(false);
            }
            sample["openedConnections"] = opened;
            sample["openSeconds"] = clock.Elapsed.TotalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(hold)).ConfigureAwait(false);
            sample["serverSocketsIdle"] = Orchestrator.ServerSockets(port);
            sample["idle"] = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);

            foreach (var client in clients) client.Dispose();
            clients.Clear();
            await Task.Delay(TimeSpan.FromSeconds(settle)).ConfigureAwait(false);
            sample["serverSocketsAfterClose"] = Orchestrator.ServerSockets(port);
            sample["after"] = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
            sample["serverStop"] = await server.RequestJsonAsync("exit", timeout).ConfigureAwait(false);
            sample["serverExitCode"] = await server.WaitForExitAsync(timeout).ConfigureAwait(false);
            Derive(sample, opened);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException or HttpRequestException or TaskCanceledException)
        {
            sample["error"] = exception.ToString();
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            if (server is not null) await server.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        await File.WriteAllTextAsync(stem + ".json", sample.ToJsonString(Indented)).ConfigureAwait(false);
        return sample;
    }

    private static HttpClient CreateClient(bool tls, string thumbprint, Version version)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 1,
            EnableMultipleHttp2Connections = false,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        };
        if (tls) handler.SslOptions.RemoteCertificateValidationCallback = LoadClient.Pinned(thumbprint);
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    private static void Derive(JsonObject sample, int opened)
    {
        if (opened == 0) throw new InvalidOperationException("No connection was opened.");
        var derived = new JsonObject();
        foreach (var field in new[] { "managedHeapBytes", "gcCommittedBytes", "workingSetBytes", "privateBytes", "handles", "threads", "threadPoolThreads" })
        {
            var before = sample["before"]?[field]?.GetValue<double?>();
            var idle = sample["idle"]?[field]?.GetValue<double?>();
            var after = sample["after"]?[field]?.GetValue<double?>();
            if (before is null || idle is null || after is null) continue;
            derived[field + "PerIdleConnection"] = (idle - before) / opened;
            derived[field + "RetainedAfterClose"] = after - before;
        }
        sample["derived"] = derived;
    }

    private static string Describe(JsonObject sample)
    {
        if (sample["error"] is { } error) return $"{sample["engine"]} r{sample["round"]}: FAILED {error.ToString().Split('\n')[0]}";
        var derived = sample["derived"]!;
        return string.Format(CultureInfo.InvariantCulture, "{0} r{1}: {2} idle {3} connections: {4:F0} B heap, {5:F0} B working set, {6:F0} B private, {7:F2} handles per connection; threads {8} -> {9}",
            sample["engine"], sample["round"], sample["protocol"], sample["openedConnections"],
            derived["managedHeapBytesPerIdleConnection"]?.GetValue<double>(), derived["workingSetBytesPerIdleConnection"]?.GetValue<double>(),
            derived["privateBytesPerIdleConnection"]?.GetValue<double>(), derived["handlesPerIdleConnection"]?.GetValue<double>(),
            sample["before"]?["threads"], sample["idle"]?["threads"]);
    }

    private static string Markdown(List<JsonObject> samples)
    {
        var text = new StringBuilder();
        text.AppendLine("| Engine | Round | Protocol | Idle connections | Heap B/conn | Working set B/conn | Private B/conn | Handles/conn | Threads before -> idle -> after | Pool threads before -> idle | Heap retained after close B | Working set retained after close B | Server sockets idle / after close |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var sample in samples)
        {
            if (sample["error"] is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {sample["engine"]} | {sample["round"]} | {sample["protocol"]} | failed | | | | | | | | | |");
                continue;
            }
            var derived = sample["derived"]!;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {sample["engine"]} | {sample["round"]} | {sample["protocol"]}{(sample["tls"]!.GetValue<bool>() ? " TLS" : "")} | {sample["openedConnections"]} | {Round(derived["managedHeapBytesPerIdleConnection"])} | {Round(derived["workingSetBytesPerIdleConnection"])} | {Round(derived["privateBytesPerIdleConnection"])} | {derived["handlesPerIdleConnection"]?.GetValue<double>().ToString("F2", CultureInfo.InvariantCulture)} | {sample["before"]?["threads"]} -> {sample["idle"]?["threads"]} -> {sample["after"]?["threads"]} | {sample["before"]?["threadPoolThreads"]} -> {sample["idle"]?["threadPoolThreads"]} | {Round(derived["managedHeapBytesRetainedAfterClose"])} | {Round(derived["workingSetBytesRetainedAfterClose"])} | {Sockets(sample["serverSocketsIdle"])} / {Sockets(sample["serverSocketsAfterClose"])} |");
        }
        return text.ToString();

        static string Round(JsonNode? node) => node is null ? "" : Math.Round(node.GetValue<double>()).ToString("N0", CultureInfo.InvariantCulture);
        static string Sockets(JsonNode? node) => node?["total"]?.ToString() ?? node?.ToString() ?? "";
    }
}
