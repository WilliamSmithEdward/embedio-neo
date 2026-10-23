using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbedIO.PlatformTests;

// Cold start: every measurement starts a fresh server process, and one long-lived client
// process, already warm from its first iteration, sends the requests. The first request on
// a new connection to a server that has served nothing shows the server's cold path:
// connection setup, the TLS handshake and the first request's JIT. The next requests on
// that connection show how quickly it warms, and a second connection to the same server
// separates the handshake from the first request.
internal static class ColdStart
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal sealed record Transport(string Name, Protocol Protocol, bool Tls);

    internal static readonly Transport[] Transports =
    [
        new("h1", Protocol.Http1, false),
        new("h1tls", Protocol.Http1, true),
        new("h2tls", Protocol.Http2, true),
        new("h3", Protocol.Http3, true),
    ];

    private static readonly string[] Metrics =
        ["readyMilliseconds", "uptimeAtListeningMilliseconds", "startMilliseconds", "firstMilliseconds",
            "secondMilliseconds", "warmMedianMilliseconds", "warmMaximumMilliseconds", "secondConnectionMilliseconds", "machineBusyCpus"];

    internal static async Task<int> RunAsync(CommandLine options)
    {
        var output = Path.GetFullPath(options.Required("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory so earlier evidence is never overwritten: " + output);
        Directory.CreateDirectory(output);
        var iterations = options.Integer("--iterations", 20);
        var requests = options.Integer("--requests", 32);
        if (iterations < 1 || requests < 2) throw new ArgumentException("Use at least one iteration and two requests per connection.");
        var names = options.Text("--transports", string.Join(',', Transports.Select(transport => transport.Name))).Split(',', StringSplitOptions.RemoveEmptyEntries);
        var transports = names.Select(name => Transports.SingleOrDefault(transport => transport.Name == name)
            ?? throw new ArgumentException("Unknown transport " + name)).ToList();
        var serverCpus = options.Optional("--server-cpus");
        var clientCpus = options.Optional("--client-cpus");
        if ((serverCpus is not null || clientCpus is not null) && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new InvalidOperationException("--server-cpus/--client-cpus are supported only on Windows and Linux.");
        if (transports.Any(transport => transport.Protocol == Protocol.Http3)
            && !((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && System.Net.Quic.QuicConnection.IsSupported))
            throw new InvalidOperationException("HTTP/3 was selected but the client cannot load MsQuic.");

        var candidate = Path.GetFullPath(options.Text("--candidate-dir", AppContext.BaseDirectory));
        var targets = new List<EngineTarget> { new("candidate", "embedio", candidate) };
        // The same runner published with ReadyToRun, which precompiles EmbedIO for the first request.
        if (options.Optional("--r2r-dir") is { } readyToRun) targets.Add(new("candidate-r2r", "embedio", Path.GetFullPath(readyToRun)));
        if (options.Optional("--baseline-dir") is { } baseline) targets.Add(new("baseline", "embedio", Path.GetFullPath(baseline)));
        targets.Add(new("kestrel", "kestrel", candidate));
        if (options.Optional("--engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries) is { } filter)
            targets = [.. targets.Where(target => filter.Contains(target.Name, StringComparer.Ordinal))];
        if (targets.Count == 0) throw new ArgumentException("No engine selected.");

        var certificateDirectory = Path.Combine(output, "certificate");
        Directory.CreateDirectory(certificateDirectory);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(certificateDirectory, "localhost.pfx");
        string thumbprint;
        using (var certificate = Orchestrator.CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var environment = Orchestrator.DescribeEnvironment(options, targets, serverCpus, clientCpus, 0, 0, 0, iterations, false);
        environment["mode"] = "cold";
        environment["requestsPerConnection"] = requests;
        environment["transports"] = new JsonArray([.. transports.Select(transport => (JsonNode)transport.Name)]);
        environment["method"] = "A fresh server process per measurement; one long-lived client process. Iteration 0 warms the client "
            + "and is excluded from the summary. Engine order alternates by iteration. Every response is validated.";
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), environment.ToJsonString(Indented)).ConfigureAwait(false);

        var timeout = TimeSpan.FromSeconds(60);
        var samples = new List<JsonObject>();
        var failures = 0;
        var client = ChildProcess.Start(AppContext.BaseDirectory,
            ["cold-client", "--certificate-thumbprint", thumbprint, "--requests", requests.ToString(CultureInfo.InvariantCulture)],
            clientCpus, Path.Combine(output, "client.stderr.log"));
        try
        {
            var ready = await client.ReadLineAsync(timeout).ConfigureAwait(false);
            if (ready != "READY") throw new InvalidOperationException("Cold client did not start: " + ready);
            await using var log = File.CreateText(Path.Combine(output, "samples.jsonl"));
            for (var iteration = 0; iteration <= iterations; iteration++)
            {
                foreach (var transport in transports)
                {
                    var engines = new List<EngineTarget>(targets);
                    if (iteration % 2 == 1) engines.Reverse();
                    foreach (var target in engines)
                    {
                        var sample = await MeasureAsync(target, transport, iteration, output, certificatePath, password, serverCpus, client, timeout).ConfigureAwait(false);
                        samples.Add(sample);
                        if (sample["error"] is not null) failures++;
                        await log.WriteLineAsync(sample.ToJsonString()).ConfigureAwait(false);
                        await log.FlushAsync().ConfigureAwait(false);
                        Console.Out.WriteLine(Describe(sample));
                    }
                }
            }

            await client.RequestAsync("exit", timeout).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            // The private key is a throwaway loopback certificate; do not leave it behind.
            File.Delete(certificatePath);
            Directory.Delete(certificateDirectory);
        }

        var (summary, markdown) = Summarize(samples, targets, transports);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), summary.ToJsonString(Indented)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), markdown).ConfigureAwait(false);
        Console.Out.WriteLine($"{samples.Count} measurements, {failures} failed. Output: {output}");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<JsonObject> MeasureAsync(EngineTarget target, Transport transport, int iteration, string output,
        string certificatePath, string password, string? serverCpus, ChildProcess client, TimeSpan timeout)
    {
        var directory = Path.Combine(output, "servers");
        Directory.CreateDirectory(directory);
        var sample = new JsonObject
        {
            ["engine"] = target.Name,
            ["transport"] = transport.Name,
            ["iteration"] = iteration,
            ["warmup"] = iteration == 0,
            ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        ChildProcess? server = null;
        try
        {
            var port = LoopbackPorts.Free();
            sample["port"] = port;
            var arguments = new List<string> { "server", "--engine", target.Engine, "--protocol", transport.Protocol.ToString(),
                "--port", port.ToString(CultureInfo.InvariantCulture) };
            if (transport.Tls) arguments.AddRange(["--tls", "--certificate", certificatePath, "--certificate-password", password]);
            var busy = SystemCpu.BusyTime();
            var launched = Stopwatch.StartNew();
            server = ChildProcess.Start(target.Directory, arguments, serverCpus,
                Path.Combine(directory, $"{target.Name}-{transport.Name}-i{iteration}.stderr.log"));
            var ready = await server.ReadLineAsync(timeout).ConfigureAwait(false);
            sample["readyMilliseconds"] = launched.Elapsed.TotalMilliseconds;
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            var serverReady = JsonNode.Parse(ready[6..])!.AsObject();
            sample["startMilliseconds"] = serverReady["startMilliseconds"]?.DeepClone();
            sample["uptimeAtListeningMilliseconds"] = serverReady["uptimeAtListeningMilliseconds"]?.DeepClone();
            sample["server"] = serverReady;
            var measured = await client.RequestJsonAsync($"measure {transport.Name} {port}", timeout).ConfigureAwait(false)
                ?? throw new IOException("The cold client returned nothing.");
            if (measured["error"] is { } error) throw new InvalidOperationException("Client: " + error);
            foreach (var property in measured.AsObject()) sample[property.Key] = property.Value?.DeepClone();
            // Busy CPUs across the whole machine while the server started and served, this harness included.
            sample["machineBusyCpus"] = (SystemCpu.BusyTime() - busy).TotalSeconds / launched.Elapsed.TotalSeconds;
            sample["serverStop"] = await server.RequestJsonAsync("exit", timeout).ConfigureAwait(false);
            sample["serverExitCode"] = await server.WaitForExitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or JsonException)
        {
            sample["error"] = exception.Message;
        }
        finally
        {
            if (server is not null) sample["serverKilled"] = await server.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }

        return sample;
    }

    private static string Describe(JsonObject sample) =>
        sample["error"] is { } error
            ? $"{sample["engine"]} {sample["transport"]} i{sample["iteration"]}: FAILED {error}"
            : string.Create(CultureInfo.InvariantCulture,
                $"{sample["engine"]} {sample["transport"]} i{sample["iteration"]}: ready {(double)sample["readyMilliseconds"]!:F1} ms, first {(double)sample["firstMilliseconds"]!:F2} ms, second {(double)sample["secondMilliseconds"]!:F2} ms, second connection {(double)sample["secondConnectionMilliseconds"]!:F2} ms");

    private static (JsonObject Json, string Markdown) Summarize(List<JsonObject> samples, List<EngineTarget> targets, List<Transport> transports)
    {
        var json = new JsonObject();
        var markdown = new StringBuilder();
        markdown.AppendLine("# Cold start").AppendLine();
        markdown.AppendLine("Medians over the measured iterations (iteration 0 excluded), with the 90th percentile and range in brackets. Milliseconds.").AppendLine();
        markdown.AppendLine("| Engine | Transport | n | Process to ready | Uptime at listening | Server start | First request | Second request | Warm median | Warm maximum | Second connection | Machine busy CPUs |");
        markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var transport in transports)
        {
            foreach (var target in targets)
            {
                var measured = samples.Where(sample => sample["error"] is null && !(bool)sample["warmup"]!
                    && (string?)sample["engine"] == target.Name && (string?)sample["transport"] == transport.Name).ToList();
                var group = new JsonObject { ["count"] = measured.Count };
                var cells = new List<string>();
                foreach (var metric in Metrics)
                {
                    var values = measured.Select(sample => sample[metric]).Where(value => value is not null).Select(value => (double)value!).Order().ToArray();
                    if (values.Length == 0)
                    {
                        group[metric] = null;
                        cells.Add("-");
                        continue;
                    }

                    var statistics = new JsonObject
                    {
                        ["median"] = Percentile(values, 0.5),
                        ["p90"] = Percentile(values, 0.9),
                        ["minimum"] = values[0],
                        ["maximum"] = values[^1],
                    };
                    group[metric] = statistics;
                    cells.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{(double)statistics["median"]!:F2} [{(double)statistics["p90"]!:F2}; {values[0]:F2}-{values[^1]:F2}]"));
                }

                json[$"{target.Name}/{transport.Name}"] = group;
                markdown.AppendLine(CultureInfo.InvariantCulture, $"| {target.Name} | {transport.Name} | {measured.Count} | {string.Join(" | ", cells)} |");
            }
        }

        return (json, markdown.ToString());
    }

    // Nearest-rank percentile of sorted values.
    private static double Percentile(double[] sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];

    internal static async Task<int> ClientAsync(CommandLine options)
    {
        var thumbprint = options.Required("--certificate-thumbprint");
        var requests = options.Integer("--requests", 32);
        Control.Write("READY");
        while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } command)
        {
            if (command == "exit")
            {
                Control.Write("BYE");
                return 0;
            }

            var parts = command.Split(' ');
            JsonObject result;
            try
            {
                if (parts.Length != 3 || parts[0] != "measure") throw new InvalidOperationException("Unknown cold client command: " + command);
                var transport = Transports.Single(candidate => candidate.Name == parts[1]);
                result = await MeasureOnceAsync(transport, int.Parse(parts[2], CultureInfo.InvariantCulture), thumbprint, requests).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
            {
                result = new JsonObject { ["error"] = exception.GetType().Name + ": " + exception.Message };
            }

            Control.Write(result.ToJsonString());
        }

        return 0;
    }

    private static async Task<JsonObject> MeasureOnceAsync(Transport transport, int port, string thumbprint, int requests)
    {
        var version = transport.Protocol switch
        {
            Protocol.Http3 => HttpVersion.Version30,
            Protocol.Http2 => HttpVersion.Version20,
            _ => HttpVersion.Version11,
        };
        var uri = new Uri($"{(transport.Tls ? "https" : "http")}://localhost:{port}/plaintext");
        var sequence = new double[requests];
        using (var first = Connect(thumbprint))
        {
            for (var index = 0; index < requests; index++) sequence[index] = await RequestAsync(first, uri, version).ConfigureAwait(false);
        }

        double secondConnection;
        using (var second = Connect(thumbprint))
        {
            secondConnection = await RequestAsync(second, uri, version).ConfigureAwait(false);
        }

        // "Warm" here means the second half of the first connection's sequence.
        var warm = sequence[(requests / 2)..].Order().ToArray();
        return new JsonObject
        {
            ["firstMilliseconds"] = sequence[0],
            ["secondMilliseconds"] = sequence[1],
            ["warmMedianMilliseconds"] = Percentile(warm, 0.5),
            ["warmMaximumMilliseconds"] = warm[^1],
            ["secondConnectionMilliseconds"] = secondConnection,
            ["sequenceMilliseconds"] = new JsonArray([.. sequence.Select(value => (JsonNode)value)]),
        };
    }

    private static HttpClient Connect(string thumbprint) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = LoadClient.Pinned(thumbprint) },
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static async Task<double> RequestAsync(HttpClient client, Uri uri, Version version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (response.StatusCode != HttpStatusCode.OK || response.Version != version || !body.AsSpan().SequenceEqual(Payloads.Plaintext))
            throw new InvalidOperationException($"Unexpected response: {(int)response.StatusCode} HTTP/{response.Version}, {body.Length} bytes");
        return elapsed;
    }
}
