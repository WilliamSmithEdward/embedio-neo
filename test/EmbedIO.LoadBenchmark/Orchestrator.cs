using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed record EngineTarget(string Name, string Engine, string Directory);

// Runs every (scenario, engine, round) sample with fresh server and client processes,
// alternating engine order per round, and writes raw samples plus a summary.
internal static class Orchestrator
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
        var rounds = options.Integer("--rounds", 3);
        var warmup = options.Number("--warmup", 5);
        var duration = options.Number("--duration", 15);
        var idle = options.Number("--idle", 2);
        var serverCpus = options.Optional("--server-cpus");
        var clientCpus = options.Optional("--client-cpus");
        // Children are pinned only through Windows CPU-set inheritance or Linux taskset.
        // macOS has no process CPU pinning, so a requested set would be recorded but not applied.
        if ((serverCpus is not null || clientCpus is not null) && !OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new InvalidOperationException("--server-cpus/--client-cpus are supported only on Windows and Linux; this OS cannot pin processes. Omit them; environment.json then records cpuAffinity \"none\".");
        var profile = options.Has("--profile");
        var modernBaseline = options.Has("--modern-baseline") || options.Has("--baseline-all-protocols");
        var selected = SelectScenarios(options.Text("--scenarios", "all"));
        if (selected.Any(scenario => scenario.Protocol == Protocol.Http3)
            && !((OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && System.Net.Quic.QuicListener.IsSupported))
            throw new InvalidOperationException("HTTP/3 scenarios were selected but QUIC is unavailable (no loadable MsQuic). On macOS/Linux put libmsquic on DYLD_FALLBACK_LIBRARY_PATH / LD_LIBRARY_PATH, or exclude h3 scenarios.");
        var engineFilter = options.Optional("--engines")?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var targets = new List<EngineTarget> { new("candidate", "embedio", candidate) };
        if (baseline is not null) targets.Add(new("baseline", "embedio", baseline));
        targets.Add(new("kestrel", "kestrel", candidate));
        if (engineFilter is not null) targets = [.. targets.Where(target => engineFilter.Contains(target.Name, StringComparer.Ordinal))];

        var certificateDirectory = Path.Combine(output, "certificate");
        Directory.CreateDirectory(certificateDirectory);
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(certificateDirectory, "localhost.pfx");
        string thumbprint;
        using (var certificate = CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var environment = DescribeEnvironment(options, targets, serverCpus, clientCpus, warmup, duration, idle, rounds, profile);
        environment["modernBaseline"] = modernBaseline;
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), environment.ToJsonString(Indented)).ConfigureAwait(false);

        var samples = new List<JsonObject>();
        var failures = 0;
        try
        {
            for (var round = 1; round <= rounds; round++)
            {
                foreach (var scenario in selected)
                {
                    var engines = targets.Where(target => target.Name != "baseline" || scenario.BaselineApplies || modernBaseline).ToList();
                    if (round % 2 == 0) engines.Reverse();
                    foreach (var target in engines)
                    {
                        var sample = await RunSampleAsync(new SampleContext(scenario, target, round, output, certificatePath, password, thumbprint,
                            warmup, duration, idle, serverCpus, clientCpus, profile, options.Optional("--reference-bytes"),
                            options.Integer("--time-wait-limit", 4000), options.Optional("--trace-tool") is { } tool ? Path.GetFullPath(tool) : null)).ConfigureAwait(false);
                        samples.Add(sample);
                        if (sample["error"] is not null) failures++;
                        Console.Out.WriteLine(Describe(sample));
                    }
                }
            }
        }
        finally
        {
            // The private key is a throwaway loopback certificate; do not leave it behind.
            File.Delete(certificatePath);
            Directory.Delete(certificateDirectory);
        }

        var summary = Summary.Build(samples);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), summary.Json.ToJsonString(Indented)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), summary.Markdown).ConfigureAwait(false);
        Console.Out.WriteLine($"{samples.Count} samples, {failures} failed. Output: {output}");
        return failures == 0 ? 0 : 1;
    }

    private static List<Scenario> SelectScenarios(string selection)
    {
        if (selection == "all") return [.. Scenario.All];
        var names = selection.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var unknown = names.Where(name => !Scenario.All.Any(scenario => scenario.Name.StartsWith(name, StringComparison.Ordinal))).ToArray();
        if (unknown.Length > 0) throw new ArgumentException("Unknown scenarios: " + string.Join(", ", unknown));
        return [.. Scenario.All.Where(scenario => names.Any(name => scenario.Name.StartsWith(name, StringComparison.Ordinal)))];
    }

    private sealed record SampleContext(Scenario Scenario, EngineTarget Target, int Round, string Output, string CertificatePath, string Password,
        string Thumbprint, double Warmup, double Duration, double Idle, string? ServerCpus, string? ClientCpus, bool Profile, string? ReferenceBytes,
        int TimeWaitLimit, string? TraceTool);

    private static async Task<JsonObject> RunSampleAsync(SampleContext context)
    {
        var scenario = context.Scenario;
        var directory = Path.Combine(context.Output, "samples", scenario.Name);
        Directory.CreateDirectory(directory);
        var stem = Path.Combine(directory, $"{context.Target.Name}-r{context.Round}");
        var sample = new JsonObject
        {
            ["scenario"] = scenario.Name,
            ["description"] = scenario.Description,
            ["engine"] = context.Target.Name,
            ["round"] = context.Round,
            ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        // Churn workloads and per-connection request caps leave thousands of loopback
        // sockets in TIME_WAIT, which can exhaust ephemeral ports for the next sample.
        // OS limits stay untouched; the sample waits for the backlog to drain instead.
        var gate = Stopwatch.StartNew();
        int timeWait;
        // Server-side entries also exhaust the host's TCP table at tens of thousands.
        while (((timeWait = TimeWaitCount()) > context.TimeWaitLimit || TimeWaitCount(clientSide: false) > context.TimeWaitLimit * 5)
            && gate.Elapsed < TimeSpan.FromMinutes(5))
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        sample["clientTimeWaitAtStart"] = timeWait;
        sample["machineTimeWaitAtStart"] = TimeWaitCount(clientSide: false);
        sample["timeWaitGateSeconds"] = gate.Elapsed.TotalSeconds;
        int port;
        try
        {
            port = FreePort();
        }
        catch (InvalidOperationException exception)
        {
            sample["error"] = exception.Message;
            await File.WriteAllTextAsync(stem + ".json", sample.ToJsonString(Indented)).ConfigureAwait(false);
            return sample;
        }

        var protocol = scenario.Protocol.ToString();
        var serverArguments = new List<string> { "server", "--engine", context.Target.Engine, "--protocol", protocol, "--port", port.ToString(CultureInfo.InvariantCulture) };
        if (scenario.Tls) serverArguments.AddRange(["--tls", "--certificate", context.CertificatePath, "--certificate-password", context.Password]);
        if (context.Profile) serverArguments.Add("--profile");
        if (context.ReferenceBytes is not null) serverArguments.AddRange(["--reference-bytes", context.ReferenceBytes]);
        var clientArguments = new List<string>
        {
            "client", "--protocol", protocol, "--port", port.ToString(CultureInfo.InvariantCulture), "--path", scenario.Path,
            "--connections", scenario.Connections.ToString(CultureInfo.InvariantCulture), "--streams", scenario.Streams.ToString(CultureInfo.InvariantCulture),
            "--pipeline", scenario.Pipeline.ToString(CultureInfo.InvariantCulture), "--requests-per-connection", scenario.RequestsPerConnection.ToString(CultureInfo.InvariantCulture),
            "--upload-bytes", scenario.UploadBytes.ToString(CultureInfo.InvariantCulture), "--certificate-thumbprint", context.Thumbprint,
            "--warmup", Math.Min(context.Warmup, scenario.MaxSeconds).ToString(CultureInfo.InvariantCulture),
            "--duration", Math.Min(context.Duration, scenario.MaxSeconds).ToString(CultureInfo.InvariantCulture),
        };
        if (scenario.Tls) clientArguments.Add("--tls");
        var timeout = TimeSpan.FromSeconds(context.Warmup + context.Duration + 90);

        ChildProcess? server = null;
        ChildProcess? client = null;
        try
        {
            server = ChildProcess.Start(context.Target.Directory, serverArguments, context.ServerCpus, stem + ".server.stderr.log");
            var ready = await server.ReadLineAsync(timeout).ConfigureAwait(false);
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            sample["server"] = JsonNode.Parse(ready[6..]);

            // The client always runs from the candidate directory; it never loads EmbedIO.
            client = ChildProcess.Start(AppContext.BaseDirectory, clientArguments, context.ClientCpus, stem + ".client.stderr.log");
            var warm = await client.ReadLineAsync(timeout).ConfigureAwait(false);
            if (warm is null || !warm.StartsWith("WARM ", StringComparison.Ordinal)) throw new InvalidOperationException("Client warmup did not complete: " + warm);
            sample["warmup"] = JsonNode.Parse(warm[5..]);
            if (sample["warmup"]?["error"]?.GetValue<string>() is { } warmError) throw new InvalidOperationException("Warmup failed: " + warmError);

            await Task.Delay(TimeSpan.FromSeconds(context.Idle)).ConfigureAwait(false);
            sample["serverSocketsBefore"] = ServerSockets(port);
            sample["serverBefore"] = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
            var busyBefore = SystemCpu.BusyTime();
            using var trace = context.TraceTool is null ? null : StartTrace(context, sample, stem);
            if (await server.RequestAsync("start", timeout).ConfigureAwait(false) != "MEASURING") throw new InvalidOperationException("Server measurement handshake failed.");
            var done = await client.RequestAsync("start", timeout).ConfigureAwait(false);
            var window = await server.RequestJsonAsync("stop", timeout).ConfigureAwait(false);
            var busyAfter = SystemCpu.BusyTime();
            if (done != "DONE") throw new InvalidOperationException("Client measurement did not complete: " + done);
            sample["serverWindow"] = window;
            if (trace is not null) sample["trace"] = await FinishTraceAsync(context, trace, stem).ConfigureAwait(false);
            sample["client"] = await client.RequestJsonAsync("report", timeout).ConfigureAwait(false);
            sample["clientExitCode"] = await client.WaitForExitAsync(timeout).ConfigureAwait(false);
            sample["machineBusyCpuSeconds"] = (busyAfter - busyBefore).TotalSeconds;

            await Task.Delay(TimeSpan.FromSeconds(context.Idle)).ConfigureAwait(false);
            sample["serverSocketsAfter"] = ServerSockets(port);
            sample["serverAfter"] = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
            sample["serverStop"] = await server.RequestJsonAsync("exit", timeout).ConfigureAwait(false);
            sample["serverExitCode"] = await server.WaitForExitAsync(timeout).ConfigureAwait(false);
            Derive(sample, context);
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException)
        {
            sample["error"] = exception.Message;
        }
        finally
        {
            foreach (var child in new[] { client, server })
            {
                if (child is null) continue;
                sample[child == server ? "serverKilled" : "clientKilled"] = await child.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }

        if (sample["client"]?["error"]?.GetValue<string>() is { } clientError) sample["error"] = "Client: " + clientError;
        if (sample["error"] is not null)
        {
            // Bounded diagnostic for transport failures: machine-wide TCP states by side.
            var census = new JsonObject();
            foreach (var group in TcpConnections()
                .GroupBy(connection => (connection.LocalPort is >= ServerPortFirst and < ServerPortLast ? "server-" : "other-") + connection.State))
            {
                census[group.Key] = group.Count();
            }

            sample["tcpStatesAfterFailure"] = census;
        }

        else if (sample["error"] is null && (sample["clientExitCode"]?.GetValue<int>() != 0 || sample["serverExitCode"]?.GetValue<int>() != 0))
            sample["error"] = "A child process exited unsuccessfully.";
        await File.WriteAllTextAsync(stem + ".json", sample.ToJsonString(Indented)).ConfigureAwait(false);
        return sample;
    }

    // Optional stack sampling of the server during the measurement window, through a
    // separately installed dotnet-trace. Profiled samples are never comparison samples.
    private static Process StartTrace(SampleContext context, JsonObject sample, string stem)
    {
        var pid = sample["server"]?["processId"]?.GetValue<int>() ?? throw new InvalidOperationException("Server did not report its process ID.");
        var seconds = (int)Math.Max(1, Math.Min(context.Duration, context.Scenario.MaxSeconds) - 1);
        var info = new ProcessStartInfo(context.TraceTool ?? string.Empty) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "collect", "-p", pid.ToString(CultureInfo.InvariantCulture), "--profile", "dotnet-sampled-thread-time",
            "--duration", "00:00:00:" + seconds.ToString("D2", CultureInfo.InvariantCulture), "-o", stem + ".nettrace" })
            info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("dotnet-trace did not start.");
    }

    private static async Task<JsonObject> FinishTraceAsync(SampleContext context, Process trace, string stem)
    {
        var collected = await trace.StandardOutput.ReadToEndAsync().ConfigureAwait(false) + await trace.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await trace.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await File.WriteAllTextAsync(stem + ".trace-collect.log", collected).ConfigureAwait(false);
        foreach (var (name, extra) in new[] { ("exclusive", Array.Empty<string>()), ("inclusive", ["--inclusive"]) })
        {
            var info = new ProcessStartInfo(context.TraceTool ?? string.Empty) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "report", stem + ".nettrace", "topN", "-n", "60" }.Concat(extra)) info.ArgumentList.Add(argument);
            using var report = Process.Start(info) ?? throw new InvalidOperationException("dotnet-trace report did not start.");
            var text = await report.StandardOutput.ReadToEndAsync().ConfigureAwait(false) + await report.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await report.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            await File.WriteAllTextAsync($"{stem}.top-{name}.txt", text).ConfigureAwait(false);
        }

        return new JsonObject { ["collectExitCode"] = trace.ExitCode, ["file"] = Path.GetFileName(stem + ".nettrace") };
    }

    private static void Derive(JsonObject sample, SampleContext context)
    {
        var client = sample["client"] ?? throw new InvalidOperationException("Missing client report.");
        var window = sample["serverWindow"]?["window"] ?? throw new InvalidOperationException("Missing server window.");
        var requests = client["requests"]?.GetValue<long>() ?? 0;
        var seconds = window["elapsedSeconds"]?.GetValue<double>() ?? 0;
        var serverCpu = window["cpuSeconds"]?.GetValue<double>() ?? 0;
        var clientCpu = client["clientCpuSeconds"]?.GetValue<double>() ?? 0;
        var serverProcessors = sample["server"]?["processorCount"]?.GetValue<int>() ?? Environment.ProcessorCount;
        var clientProcessors = client["clientProcessorCount"]?.GetValue<int>() ?? Environment.ProcessorCount;
        // Null when the platform does not report the metric (macOS private bytes).
        long? Delta(string name) => sample["serverAfter"]?[name]?.GetValue<long>() - sample["serverBefore"]?[name]?.GetValue<long>();
        sample["derived"] = new JsonObject
        {
            ["requestsPerSecond"] = client["requestsPerSecond"]?.GetValue<double>(),
            ["serverCpuMicrosecondsPerRequest"] = requests == 0 ? null : serverCpu * 1_000_000 / requests,
            ["serverAllocatedBytesPerRequest"] = requests == 0 ? null : (window["allocatedBytes"]?.GetValue<long>() ?? 0) / (double)requests,
            ["serverCpuUtilization"] = seconds == 0 ? null : serverCpu / (seconds * serverProcessors),
            ["clientCpuUtilization"] = seconds == 0 ? null : clientCpu / (seconds * clientProcessors),
            ["backgroundCpuSeconds"] = (sample["machineBusyCpuSeconds"]?.GetValue<double>() ?? 0) - serverCpu - clientCpu,
            ["retainedManagedHeapBytes"] = Delta("managedHeapBytes"),
            ["retainedWorkingSetBytes"] = Delta("workingSetBytes"),
            ["retainedPrivateBytes"] = Delta("privateBytes"),
            ["retainedHandles"] = Delta("handles"),
            ["openServerSocketsAfter"] = (sample["serverSocketsAfter"] as JsonObject)?.Where(pair => pair.Key != "TimeWait").Sum(pair => pair.Value?.GetValue<int>() ?? 0) ?? 0,
            ["retainedThreads"] = Delta("threads"),
            ["serverCpus"] = context.ServerCpus,
            ["clientCpus"] = context.ClientCpus,
        };
    }

    private static string Describe(JsonObject sample)
    {
        var name = $"{sample["scenario"]} {sample["engine"]} r{sample["round"]}";
        if (sample["error"] is { } error) return name + " FAILED: " + error;
        var derived = sample["derived"];
        return string.Create(CultureInfo.InvariantCulture,
            $"{name}: {derived?["requestsPerSecond"]?.GetValue<double>():F0} req/s, p99 {sample["client"]?["p99Milliseconds"]?.GetValue<double>():F3} ms, cpu {derived?["serverCpuMicrosecondsPerRequest"]?.GetValue<double>():F1} us/req, alloc {derived?["serverAllocatedBytesPerRequest"]?.GetValue<double>():F0} B/req, client cpu {derived?["clientCpuUtilization"]?.GetValue<double>():P0}, open sockets {derived?["openServerSocketsAfter"]}, retained threads {derived?["retainedThreads"]}");
    }

    // Only client-side TIME_WAIT (local port outside the server port range) holds
    // ephemeral ports; server-side entries do not limit new client connections.
    private static int TimeWaitCount(bool clientSide = true)
        => TcpConnections().Count(connection => connection.State == TcpState.TimeWait
            && (!clientSide || connection.LocalPort is < ServerPortFirst or >= ServerPortLast));

    // Every non-listening TCP connection by local port and state. On macOS .NET's
    // GetActiveTcpConnections omits TIME_WAIT, so the TIME_WAIT gate and the socket
    // census read netstat, which lists every protocol control block.
    private static List<(int LocalPort, TcpState State)> TcpConnections()
    {
        if (!OperatingSystem.IsMacOS())
            return [.. IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Select(connection => (connection.LocalEndPoint.Port, connection.State))];
        var result = new List<(int LocalPort, TcpState State)>();
        foreach (var line in SystemCpu.Command("netstat", "-an -p tcp").Split('\n'))
        {
            // tcp4  0  0  127.0.0.1.20123  127.0.0.1.54321  TIME_WAIT
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6 || !fields[0].StartsWith("tcp", StringComparison.Ordinal)) continue;
            var local = fields[3];
            var dot = local.LastIndexOf('.');
            if (dot < 0 || !int.TryParse(local.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var localPort)) continue;
            if (!Enum.TryParse<TcpState>(fields[5].Replace("_", string.Empty, StringComparison.Ordinal), true, out var state) || state == TcpState.Listen) continue;
            result.Add((localPort, state));
        }

        return result;
    }

    private const int ServerPortFirst = 20000;
    private const int ServerPortLast = 30000;

    // Server-side TCP connections still known to the OS after the client has exited,
    // by state. TIME_WAIT is kernel bookkeeping; any other state is a socket the server
    // has not closed. QUIC connections are not visible here.
    private static JsonObject ServerSockets(int port)
    {
        var states = new JsonObject();
        foreach (var group in TcpConnections().Where(connection => connection.LocalPort == port).GroupBy(connection => connection.State))
        {
            states[group.Key.ToString()] = group.Count();
        }

        return states;
    }

    // TCP and UDP must both be free because HTTP/3 binds UDP on the same number. Ports
    // come from below the OS dynamic range so client sockets in TIME_WAIT never collide.
    private static int FreePort()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var port = Random.Shared.Next(ServerPortFirst, ServerPortLast);
            try
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
                using var udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                udp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                using var tcp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                tcp6.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                return port;
            }
            catch (SocketException)
            {
                // Try another ephemeral port.
            }
        }

        throw new InvalidOperationException("No free TCP/UDP port pair.");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static JsonObject DescribeEnvironment(CommandLine options, List<EngineTarget> targets, string? serverCpus, string? clientCpus,
        double warmup, double duration, double idle, int rounds, bool profile)
    {
        string Hash(string file) => File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "missing";
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var aspnetDirectory = Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions).Assembly.Location) ?? string.Empty;
        return new JsonObject
        {
            ["capturedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processor"] = SystemCpu.ProcessorName(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["totalMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["runtimeDirectory"] = runtimeDirectory,
            ["processorTopology"] = SystemCpu.Topology(),
            // HTTP/3 needs a loadable native MsQuic; macOS and Linux find it only on the library path.
            ["quicSupported"] = OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? System.Net.Quic.QuicListener.IsSupported : null,
            ["libraryPath"] = new JsonObject(new[] { "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "LD_LIBRARY_PATH" }
                .Where(key => Environment.GetEnvironmentVariable(key) is not null)
                .Select(key => KeyValuePair.Create(key, (JsonNode?)Environment.GetEnvironmentVariable(key)))),
            ["powerScheme"] = OperatingSystem.IsWindows() ? SystemCpu.Command("powercfg", "/getactivescheme") : null,
            ["powerSettings"] = OperatingSystem.IsMacOS()
                ? string.Join("\n", SystemCpu.Command("pmset", "-g"), SystemCpu.Command("pmset", "-g batt"), SystemCpu.Command("pmset", "-g therm"))
                : null,
            ["cpuAffinity"] = serverCpus is null && clientCpus is null ? "none"
                : OperatingSystem.IsWindows() ? "inherited CPU set (Windows)" : "taskset (Linux)",
            ["dotnetEnvironment"] = new JsonObject(Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(key => key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase))
                .Select(key => KeyValuePair.Create(key, (JsonNode?)Environment.GetEnvironmentVariable(key)))),
            ["candidateRevision"] = options.Optional("--candidate-revision"),
            ["baselineRevision"] = options.Optional("--baseline-revision"),
            ["baselineAllProtocols"] = options.Has("--modern-baseline") || options.Has("--baseline-all-protocols"),
            ["targets"] = new JsonArray([.. targets.Select(target => (JsonNode)new JsonObject
            {
                ["name"] = target.Name,
                ["engine"] = target.Engine,
                ["directory"] = target.Directory,
                ["runnerSha256"] = Hash(Path.Combine(target.Directory, "EmbedIO.LoadBenchmark.dll")),
                ["embedioSha256"] = Hash(Path.Combine(target.Directory, "EmbedIO.dll")),
            })]),
            ["kestrelCoreSha256"] = Hash(Path.Combine(aspnetDirectory, "Microsoft.AspNetCore.Server.Kestrel.Core.dll")),
            ["kestrelQuicSha256"] = Hash(Path.Combine(aspnetDirectory, "Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.dll")),
            ["systemNetQuicSha256"] = Hash(Path.Combine(runtimeDirectory, "System.Net.Quic.dll")),
            ["msquicSha256"] = Hash(Path.Combine(runtimeDirectory, "msquic.dll")),
            ["serverCpus"] = serverCpus,
            ["clientCpus"] = clientCpus,
            ["warmupSeconds"] = warmup,
            ["durationSeconds"] = duration,
            ["idleSeconds"] = idle,
            ["rounds"] = rounds,
            ["profile"] = profile,
            ["gc"] = "Server GC, concurrent (runtimeconfig), identical for every engine",
            ["method"] = "Fresh server and client processes per sample; engine order alternates by round; closed-loop client; every response validated; first error aborts the sample without retry.",
        };
    }
}

// A child benchmark process with a line-oriented control channel.
internal sealed class ChildProcess
{
    private readonly Process _process;
    private readonly Task _stderr;

    private ChildProcess(Process process, string stderrPath)
    {
        _process = process;
        _stderr = Task.Run(async () =>
        {
            await using var log = File.CreateText(stderrPath);
            await log.WriteAsync(await process.StandardError.ReadToEndAsync().ConfigureAwait(false)).ConfigureAwait(false);
        });
    }

    internal static ChildProcess Start(string directory, IReadOnlyList<string> arguments, string? cpus, string stderrPath)
    {
        var host = Environment.ProcessPath ?? "dotnet";
        var runner = Path.Combine(directory, "EmbedIO.LoadBenchmark.dll");
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory,
        };
        var useTaskset = cpus is not null && OperatingSystem.IsLinux();
        info.FileName = useTaskset ? "taskset" : host;
        if (useTaskset) info.ArgumentList.Add("-c");
        if (useTaskset) info.ArgumentList.Add(cpus ?? string.Empty);
        if (useTaskset) info.ArgumentList.Add(host);
        info.ArgumentList.Add(runner);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        Process process;
        if (cpus is not null && OperatingSystem.IsWindows())
        {
            // A child inherits the creator's affinity, so the runtime sizes its GC
            // heaps and thread pool for the restricted set from the first instruction.
            lock (AffinityLockHolder.Value)
            {
                using var self = Process.GetCurrentProcess();
                var original = self.ProcessorAffinity;
                self.ProcessorAffinity = Mask(cpus);
                try
                {
                    process = Process.Start(info) ?? throw new InvalidOperationException("Process did not start.");
                }
                finally
                {
                    self.ProcessorAffinity = original;
                }
            }
        }
        else
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process did not start.");
        }

        return new ChildProcess(process, stderrPath);
    }

    internal async Task<string?> ReadLineAsync(TimeSpan timeout)
        => await _process.StandardOutput.ReadLineAsync().WaitAsync(timeout).ConfigureAwait(false);

    internal async Task<string?> RequestAsync(string command, TimeSpan timeout)
    {
        await _process.StandardInput.WriteLineAsync(command).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        return await ReadLineAsync(timeout).ConfigureAwait(false);
    }

    internal async Task<JsonNode?> RequestJsonAsync(string command, TimeSpan timeout)
    {
        var line = await RequestAsync(command, timeout).ConfigureAwait(false);
        return JsonNode.Parse(line ?? throw new IOException("Child closed its output after " + command));
    }

    internal async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        return _process.ExitCode;
    }

    // Returns true when the process had to be killed.
    internal async Task<bool> DisposeAsync(TimeSpan grace)
    {
        var killed = false;
        try
        {
            await _process.WaitForExitAsync().WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            killed = true;
        }

        await _stderr.ConfigureAwait(false);
        _process.Dispose();
        return killed;
    }

    private static IntPtr Mask(string cpus)
    {
        long mask = 0;
        foreach (var part in cpus.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var range = part.Split('-');
            var first = int.Parse(range[0], CultureInfo.InvariantCulture);
            var last = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : first;
            for (var cpu = first; cpu <= last; cpu++) mask |= 1L << cpu;
        }

        return new IntPtr(mask);
    }

    private static class AffinityLockHolder
    {
        internal static readonly Lock Value = new();
    }
}

internal static class Summary
{
    internal static (JsonObject Json, string Markdown) Build(List<JsonObject> samples)
    {
        var groups = samples.GroupBy(sample => (Scenario: sample["scenario"]?.GetValue<string>() ?? "?", Engine: sample["engine"]?.GetValue<string>() ?? "?"));
        var rows = new JsonArray();
        var markdown = new StringBuilder();
        markdown.AppendLine("| Scenario | Engine | Valid/total | Requests/s median (min-max) | p50 ms | p99 ms | Server CPU us/req | Server B/req | Server CPU util | Client CPU util | Open server sockets after (max) | Handle growth (max) | Retained heap KB (max) |");
        markdown.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var group in groups)
        {
            var valid = group.Where(sample => sample["error"] is null).ToList();
            double? Median(Func<JsonObject, double?> select)
            {
                var values = valid.Select(select).Where(value => value.HasValue).Select(value => value.GetValueOrDefault()).Order().ToArray();
                return values.Length == 0 ? null : values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
            }

            double? Derived(JsonObject sample, string name) => sample["derived"]?[name]?.GetValue<double>();
            double? Client(JsonObject sample, string name) => sample["client"]?[name]?.GetValue<double>();
            var rps = valid.Select(sample => Derived(sample, "requestsPerSecond") ?? 0).ToArray();
            var row = new JsonObject
            {
                ["scenario"] = group.Key.Scenario,
                ["engine"] = group.Key.Engine,
                ["validSamples"] = valid.Count,
                ["totalSamples"] = group.Count(),
                ["errors"] = new JsonArray([.. group.Where(sample => sample["error"] is not null).Select(sample => (JsonNode?)sample["error"]?.GetValue<string>())]),
                ["requestsPerSecondMedian"] = Median(sample => Derived(sample, "requestsPerSecond")),
                ["requestsPerSecondMin"] = rps.Length == 0 ? null : rps.Min(),
                ["requestsPerSecondMax"] = rps.Length == 0 ? null : rps.Max(),
                ["p50MillisecondsMedian"] = Median(sample => Client(sample, "p50Milliseconds")),
                ["p95MillisecondsMedian"] = Median(sample => Client(sample, "p95Milliseconds")),
                ["p99MillisecondsMedian"] = Median(sample => Client(sample, "p99Milliseconds")),
                ["serverCpuMicrosecondsPerRequestMedian"] = Median(sample => Derived(sample, "serverCpuMicrosecondsPerRequest")),
                ["serverAllocatedBytesPerRequestMedian"] = Median(sample => Derived(sample, "serverAllocatedBytesPerRequest")),
                ["serverCpuUtilizationMedian"] = Median(sample => Derived(sample, "serverCpuUtilization")),
                ["clientCpuUtilizationMedian"] = Median(sample => Derived(sample, "clientCpuUtilization")),
                ["retainedHandlesMax"] = valid.Count == 0 ? null : valid.Max(sample => sample["derived"]?["retainedHandles"]?.GetValue<long>() ?? 0),
                ["openServerSocketsAfterMax"] = valid.Count == 0 ? null : valid.Max(sample => sample["derived"]?["openServerSocketsAfter"]?.GetValue<int>() ?? 0),
                ["retainedManagedHeapBytesMax"] = valid.Count == 0 ? null : valid.Max(sample => sample["derived"]?["retainedManagedHeapBytes"]?.GetValue<long>() ?? 0),
            };
            rows.Add(row);
            string Format(double? value, string format) => value?.ToString(format, CultureInfo.InvariantCulture) ?? "-";
            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key.Scenario} | {group.Key.Engine} | {valid.Count}/{group.Count()} | {Format(row["requestsPerSecondMedian"]?.GetValue<double>(), "N0")} ({Format(rps.Length == 0 ? null : rps.Min(), "N0")}-{Format(rps.Length == 0 ? null : rps.Max(), "N0")}) | {Format(row["p50MillisecondsMedian"]?.GetValue<double>(), "F3")} | {Format(row["p99MillisecondsMedian"]?.GetValue<double>(), "F3")} | {Format(row["serverCpuMicrosecondsPerRequestMedian"]?.GetValue<double>(), "F1")} | {Format(row["serverAllocatedBytesPerRequestMedian"]?.GetValue<double>(), "N0")} | {Format(row["serverCpuUtilizationMedian"]?.GetValue<double>(), "P0")} | {Format(row["clientCpuUtilizationMedian"]?.GetValue<double>(), "P0")} | {row["openServerSocketsAfterMax"]?.ToString() ?? "-"} | {row["retainedHandlesMax"]?.ToString() ?? "-"} | {Format(row["retainedManagedHeapBytesMax"]?.GetValue<long>() / 1024.0, "N0")} |");
        }

        return (new JsonObject { ["rows"] = rows }, markdown.ToString());
    }
}
