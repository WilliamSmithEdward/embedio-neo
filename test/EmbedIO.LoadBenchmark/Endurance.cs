using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

// Endurance orchestrator. One server process lives for the whole plan, so retained
// state accumulates exactly as in a long-running application. Each step runs a
// fresh client process; after every load step traffic stops, the server drains to
// zero in-flight work, a forced-GC snapshot is taken and a validated health check
// runs. Nothing is retried; every step's outcome is appended to steps.jsonl.
//
// Plan steps (comma separated):
//   idle:<seconds>                    wait, then snapshot (baseline)
//   load:<mix>:<seconds>              client mix (steady|faults|ws|mixed|saturate)
//   drain:<mix>:<seconds>:<cycles>    load with graceful drain + restart at mid-point, per cycle
//   restart:<cycles>                  idle drain + restart cycles
internal static class Endurance
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(CommandLine options)
    {
        var output = Path.GetFullPath(options.Required("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory so earlier evidence is never overwritten: " + output);
        Directory.CreateDirectory(output);
        var plan = options.Required("--plan").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var settle = options.Number("--settle", 20);
        var drainTimeout = options.Number("--drain-timeout", 10);
        var rateScale = options.Text("--rate-scale", "1");
        var serverCpus = options.Optional("--server-cpus");
        var clientCpus = options.Optional("--client-cpus");
        var interval = options.Text("--interval-seconds", "60");

        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(output, "localhost.pfx");
        string thumbprint;
        using (var certificate = Orchestrator.CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var plainPort = Orchestrator.FreePort();
        int tlsPort;
        while ((tlsPort = Orchestrator.FreePort()) == plainPort)
        {
            // Two distinct ports.
        }

        var environment = new JsonObject
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["os"] = RuntimeInformation.OSDescription,
            ["processor"] = SystemCpu.ProcessorName(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["totalPhysicalBytes"] = GuestMemory.TotalPhysicalBytes(),
            ["availablePhysicalBytes"] = GuestMemory.AvailablePhysicalBytes(),
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["runtimeDirectory"] = RuntimeEnvironment.GetRuntimeDirectory(),
            ["powerScheme"] = OperatingSystem.IsWindows() ? SystemCpu.Command("powercfg", "/getactivescheme") : null,
            ["revision"] = options.Optional("--revision"),
            ["engine"] = options.Text("--engine", "embedio"),
            ["transports"] = options.Optional("--transports"),
            ["kinds"] = options.Optional("--kinds"),
            ["runnerSha256"] = Hash(Path.Combine(AppContext.BaseDirectory, "EmbedIO.LoadBenchmark.dll")),
            ["embedioSha256"] = Hash(Path.Combine(AppContext.BaseDirectory, "EmbedIO.dll")),
            ["msquicSha256"] = Hash(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "msquic.dll")),
            ["systemNetQuicSha256"] = Hash(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Net.Quic.dll")),
            ["plan"] = new JsonArray([.. plan.Select(step => (JsonNode)step)]),
            ["settleSeconds"] = settle,
            ["drainTimeoutSeconds"] = drainTimeout,
            ["rateScale"] = rateScale,
            ["serverCpus"] = serverCpus,
            ["clientCpus"] = clientCpus,
            ["plainPort"] = plainPort,
            ["tlsPort"] = tlsPort,
            ["dotnetEnvironment"] = new JsonObject(Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(key => key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
                .Select(key => KeyValuePair.Create(key, (JsonNode?)Environment.GetEnvironmentVariable(key)))),
        };
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), environment.ToJsonString(Indented)).ConfigureAwait(false);

        var steps = new StreamWriter(Path.Combine(output, "steps.jsonl")) { AutoFlush = true };
        var failures = 0;
        ChildProcess? server = null;
        try
        {
            server = ChildProcess.Start(AppContext.BaseDirectory, [
                "endurance-server", "--plain-port", plainPort.ToString(CultureInfo.InvariantCulture), "--tls-port", tlsPort.ToString(CultureInfo.InvariantCulture),
                "--certificate", certificatePath, "--certificate-password", password, "--sample-file", Path.Combine(output, "server-samples.jsonl"),
                "--sample-seconds", options.Text("--sample-seconds", "5"), "--engine", options.Text("--engine", "embedio")], serverCpus, Path.Combine(output, "server.stderr.log"));
            var ready = await server.ReadLineAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            File.Delete(certificatePath);
            await Write(new JsonObject { ["step"] = "server-start", ["server"] = JsonNode.Parse(ready[6..]) }).ConfigureAwait(false);

            var filters = new List<string>();
            if (options.Optional("--transports") is { } transports) filters.AddRange(["--transports", transports]);
            if (options.Optional("--kinds") is { } kinds) filters.AddRange(["--kinds", kinds]);
            // The Kestrel control has no WebSocket endpoint, so its health checks use plain requests only.
            var healthKinds = options.Text("--engine", "embedio") == "kestrel" ? "small" : null;
            var context = new StepContext(server, output, plainPort, tlsPort, thumbprint, clientCpus, rateScale, interval, settle, drainTimeout, filters, healthKinds);
            var index = 0;
            foreach (var step in plan)
            {
                index++;
                var parts = step.Split(':');
                var record = new JsonObject { ["index"] = index, ["step"] = step, ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
                var clock = Stopwatch.StartNew();
                try
                {
                    switch (parts[0])
                    {
                        case "idle":
                            await Task.Delay(TimeSpan.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture))).ConfigureAwait(false);
                            await QuiesceAsync(context, record).ConfigureAwait(false);
                            break;
                        case "load":
                            await LoadAsync(context, record, index, parts[1], double.Parse(parts[2], CultureInfo.InvariantCulture), drainCycles: 0).ConfigureAwait(false);
                            break;
                        case "drain":
                            for (var cycle = 1; cycle <= int.Parse(parts[3], CultureInfo.InvariantCulture); cycle++)
                            {
                                var nested = new JsonObject { ["cycle"] = cycle };
                                await LoadAsync(context, nested, index * 1000 + cycle, parts[1], double.Parse(parts[2], CultureInfo.InvariantCulture), drainCycles: 1).ConfigureAwait(false);
                                (record["cycles"] ??= new JsonArray()).AsArray().Add(nested);
                                if (nested["failed"]?.GetValue<bool>() == true) record["failed"] = true;
                            }

                            break;
                        case "restart":
                            for (var cycle = 1; cycle <= int.Parse(parts[1], CultureInfo.InvariantCulture); cycle++)
                            {
                                var nested = new JsonObject { ["cycle"] = cycle };
                                nested["drain"] = await server.RequestJsonAsync("drain " + drainTimeout.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(drainTimeout + 90)).ConfigureAwait(false);
                                nested["restart"] = await server.RequestJsonAsync("start", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                                await QuiesceAsync(context, nested).ConfigureAwait(false);
                                (record["cycles"] ??= new JsonArray()).AsArray().Add(nested);
                                if (nested["failed"]?.GetValue<bool>() == true) record["failed"] = true;
                            }

                            break;
                        default:
                            throw new ArgumentException("Unknown plan step " + step);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException)
                {
                    record["failed"] = true;
                    record["error"] = exception.GetType().Name + ": " + exception.Message;
                }

                record["seconds"] = clock.Elapsed.TotalSeconds;
                if (record["failed"]?.GetValue<bool>() == true) failures++;
                await Write(record).ConfigureAwait(false);
                Console.Out.WriteLine(Describe(record));
                if (server.HasExited) throw new InvalidOperationException("The endurance server exited unexpectedly.");
            }

            var stop = await server.RequestJsonAsync("exit", TimeSpan.FromSeconds(120)).ConfigureAwait(false);
            await Write(new JsonObject { ["step"] = "server-exit", ["result"] = stop, ["exitCode"] = await server.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false) }).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(certificatePath)) File.Delete(certificatePath);
            if (server is not null) await Write(new JsonObject { ["step"] = "server-dispose", ["killed"] = await server.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false) }).ConfigureAwait(false);
            await steps.DisposeAsync().ConfigureAwait(false);
        }

        Console.Out.WriteLine($"{plan.Length} steps, {failures} failed. Output: {output}");
        return failures == 0 ? 0 : 1;

        Task Write(JsonObject record) => steps.WriteLineAsync(record.ToJsonString());

        static string Hash(string file) => File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "missing";
    }

    private sealed record StepContext(ChildProcess Server, string Output, int PlainPort, int TlsPort, string Thumbprint, string? ClientCpus,
        string RateScale, string Interval, double Settle, double DrainTimeout, IReadOnlyList<string> Filters, string? HealthKinds);

    private static async Task LoadAsync(StepContext context, JsonObject record, int index, string mix, double seconds, int drainCycles)
    {
        record["mix"] = mix;
        record["clientTimeWaitAtStart"] = Orchestrator.TimeWaitCount();
        record["machineTimeWaitAtStart"] = Orchestrator.TimeWaitCount(clientSide: false);
        record["serverBefore"] = await context.Server.RequestJsonAsync("sample", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        var report = await RunClientAsync(context, record, index, mix, seconds, drainCycles).ConfigureAwait(false);
        record["client"] = report;
        var errors = report?["errors"]?.GetValue<long>() ?? -1;
        if (errors != 0) record["failed"] = true;
        // Immediately after traffic stops, before any settling: what is still in flight.
        record["serverAtStop"] = await context.Server.RequestJsonAsync("sample", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await QuiesceAsync(context, record).ConfigureAwait(false);
        var health = await RunClientAsync(context, null, index, "health", 10, 0).ConfigureAwait(false);
        record["health"] = Brief(health);
        if ((health?["errors"]?.GetValue<long>() ?? -1) != 0 || health?["workloads"]?.AsArray().Any(workload => (workload?["ok"]?.GetValue<long>() ?? 0) == 0) != false)
            record["failed"] = true;
    }

    // Waits for zero in-flight requests and WebSockets plus a settle period, then takes
    // a forced-GC snapshot. A server that does not quiesce within two minutes is a finding.
    private static async Task QuiesceAsync(StepContext context, JsonObject record)
    {
        var clock = Stopwatch.StartNew();
        JsonNode? sample;
        do
        {
            sample = await context.Server.RequestJsonAsync("sample", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if ((sample?["inFlightRequests"]?.GetValue<long>() ?? 1) == 0 && (sample?["webSocketsActive"]?.GetValue<long>() ?? 1) == 0) break;
            await Task.Delay(1000).ConfigureAwait(false);
        }
        while (clock.Elapsed < TimeSpan.FromMinutes(2));
        record["quiesceMilliseconds"] = clock.Elapsed.TotalMilliseconds;
        if ((sample?["inFlightRequests"]?.GetValue<long>() ?? 1) != 0 || (sample?["webSocketsActive"]?.GetValue<long>() ?? 1) != 0)
        {
            record["failed"] = true;
            record["quiesceStuck"] = sample;
        }

        await Task.Delay(TimeSpan.FromSeconds(context.Settle)).ConfigureAwait(false);
        record["quiesced"] = await context.Server.RequestJsonAsync("quiesce", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        if ((record["quiesced"]?["staleGenerationRequests"]?.GetValue<long>() ?? 0) != 0)
        {
            record["failed"] = true;
            record["staleGeneration"] = "A drained and replaced server instance handled requests.";
        }

        var census = new JsonObject();
        foreach (var group in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().GroupBy(connection => connection.State))
            census[group.Key.ToString()] = group.Count();
        record["machineTcpAfterQuiesce"] = census;
    }

    private static async Task<JsonNode?> RunClientAsync(StepContext context, JsonObject? record, int index, string mix, double seconds, int drainCycles)
    {
        var stem = Path.Combine(context.Output, "clients", $"{index:D4}-{mix}");
        Directory.CreateDirectory(Path.GetDirectoryName(stem) ?? context.Output);
        var client = ChildProcess.Start(AppContext.BaseDirectory, [
            "endurance-client", "--plain-port", context.PlainPort.ToString(CultureInfo.InvariantCulture), "--tls-port", context.TlsPort.ToString(CultureInfo.InvariantCulture),
            "--certificate-thumbprint", context.Thumbprint, "--mix", mix, "--duration", seconds.ToString(CultureInfo.InvariantCulture), "--rate-scale", context.RateScale,
            "--interval-seconds", context.Interval, "--interval-file", stem + ".intervals.jsonl", .. mix == "health" ? HealthFilters(context.Filters, context.HealthKinds) : context.Filters], context.ClientCpus, stem + ".stderr.log");
        try
        {
            var started = await client.ReadLineAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (started != "STARTED") throw new InvalidOperationException("Client did not start: " + started);
            if (drainCycles > 0 && record is not null)
            {
                // Mid-phase: mark the disruption window, drain under load, restart.
                await Task.Delay(TimeSpan.FromSeconds(seconds / 2)).ConfigureAwait(false);
                if (await client.RequestAsync("disrupt-begin", TimeSpan.FromSeconds(30)).ConfigureAwait(false) != "OK") throw new InvalidOperationException("Client did not acknowledge disruption.");
                record["serverBeforeDrain"] = await context.Server.RequestJsonAsync("sample", TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                record["drain"] = await context.Server.RequestJsonAsync("drain " + context.DrainTimeout.ToString(CultureInfo.InvariantCulture), TimeSpan.FromSeconds(context.DrainTimeout + 90)).ConfigureAwait(false);
                record["restart"] = await context.Server.RequestJsonAsync("start", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                if (await client.RequestAsync("disrupt-end", TimeSpan.FromSeconds(30)).ConfigureAwait(false) != "OK") throw new InvalidOperationException("Client did not acknowledge disruption end.");
            }

            var done = await client.ReadLineAsync(TimeSpan.FromSeconds(seconds + 180)).ConfigureAwait(false);
            if (done is null || !done.StartsWith("DONE ", StringComparison.Ordinal)) throw new InvalidOperationException("Client did not report: " + done);
            var report = JsonNode.Parse(done[5..]);
            await File.WriteAllTextAsync(stem + ".report.json", report?.ToJsonString(Indented)).ConfigureAwait(false);
            var exitCode = await client.WaitForExitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            if (exitCode != 0) throw new InvalidOperationException("Client exited with " + exitCode);
            return report;
        }
        finally
        {
            await client.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    // Health checks keep the transport filter (a control engine may not serve every
    // protocol) but always run their own workload kinds.
    private static List<string> HealthFilters(IReadOnlyList<string> filters, string? kinds)
    {
        var index = filters.ToList().IndexOf("--transports");
        List<string> result = index < 0 ? [] : ["--transports", filters[index + 1]];
        if (kinds is not null) result.AddRange(["--kinds", kinds]);
        return result;
    }

    private static JsonObject Brief(JsonNode? report) => new()
    {
        ["errors"] = report?["errors"]?.GetValue<long>(),
        ["workloads"] = new JsonArray([.. (report?["workloads"]?.AsArray() ?? []).Select(workload => (JsonNode)new JsonObject
        {
            ["name"] = workload?["name"]?.GetValue<string>(),
            ["ok"] = workload?["ok"]?.GetValue<long>(),
            ["errors"] = workload?["errors"]?.GetValue<long>(),
            ["p99"] = workload?["p99"]?.GetValue<double>(),
            ["firstErrors"] = workload?["firstErrors"]?.DeepClone(),
        })]),
    };

    private static string Describe(JsonObject record)
    {
        var quiesced = record["quiesced"] ?? record["cycles"]?.AsArray().LastOrDefault()?["quiesced"];
        var client = record["client"] ?? record["cycles"]?.AsArray().LastOrDefault()?["client"];
        var heap = quiesced?["managedHeapBytes"]?.GetValue<long>() / 1024 ?? 0;
        var workingSet = quiesced?["workingSetBytes"]?.GetValue<long>() / 1048576 ?? 0;
        var state = record["failed"]?.GetValue<bool>() == true ? "FAILED" : "ok";
        var line = string.Format(CultureInfo.InvariantCulture, "[{0:HH:mm:ss}] #{1} {2} {3} in {4:F0}s | client errors {5} | heap {6} KB, ws {7} MB, handles {8}, threads {9}, timers {10}",
            DateTime.Now, record["index"], record["step"], state, record["seconds"]?.GetValue<double>(), client?["errors"], heap, workingSet, quiesced?["handles"], quiesced?["threads"], quiesced?["timers"]);
        return record["error"] is { } error ? line + " | " + error : line;
    }
}
