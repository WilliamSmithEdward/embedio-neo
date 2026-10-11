using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// Paced (open-loop) QUIC connection churn against one long-lived HTTP/3 server
// process. Each attempt is timed in three separate phases on a raw QuicConnection:
// handshake (ConnectAsync), one validated GET on a new request stream, and the
// graceful connection close. Optional mixed work runs at the same time over warm
// HttpClient HTTP/3 connections, so stream-service latency on established
// connections is measured separately from handshake establishment. Every failure
// is recorded with its phase and timing; nothing is retried and no deadline is
// changed from the runtime default unless --handshake-timeout is given.
internal static class QuicChurn
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(CommandLine options)
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !QuicConnection.IsSupported)
            throw new InvalidOperationException("QUIC is unavailable (no loadable MsQuic).");
        var output = Path.GetFullPath(options.Required("--output"));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("Use a fresh output directory: " + output);
        Directory.CreateDirectory(output);

        var engines = options.Text("--engines", "embedio,kestrel").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rates = options.Text("--rates", "4,16,64").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
        var settings = new ChurnSettings(
            rates,
            options.Number("--phase-seconds", 30),
            options.Number("--idle", 5),
            options.Integer("--max-outstanding", 512),
            options.Optional("--handshake-timeout") is { } handshake ? TimeSpan.FromSeconds(double.Parse(handshake, NumberStyles.Float, CultureInfo.InvariantCulture)) : null,
            options.Number("--mixed-rate", 0),
            options.Number("--large-rate", 0),
            options.Integer("--mixed-connections", 4));
        var rounds = options.Integer("--rounds", 1);
        if (engines.Length == 0 || engines.Any(engine => engine is not ("embedio" or "kestrel")))
            throw new ArgumentException("--engines takes embedio and/or kestrel.");
        if (rates.Length == 0 || rates.Any(rate => !double.IsFinite(rate) || rate <= 0)
            || !double.IsFinite(settings.PhaseSeconds) || settings.PhaseSeconds <= 0
            || !double.IsFinite(settings.IdleSeconds) || settings.IdleSeconds < 0
            || settings.MaxOutstanding <= 0 || rounds <= 0 || settings.MixedConnections <= 0
            || !double.IsFinite(settings.MixedRate) || settings.MixedRate < 0
            || !double.IsFinite(settings.LargeRate) || settings.LargeRate < 0
            || settings.HandshakeTimeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentException("Rates, durations, rounds and limits must be positive and finite; idle and mixed rates may be zero.");

        var serverDirectory = Path.GetFullPath(options.Text("--server-dir", AppContext.BaseDirectory));
        var serverCpus = options.Optional("--server-cpus");
        // The client runs in this process; pin it here (the server child gets its own set).
        if (options.Optional("--client-cpus") is { } clientCpus)
        {
            using var self = Process.GetCurrentProcess();
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux()) self.ProcessorAffinity = ChildProcess.Mask(clientCpus);
            else throw new ArgumentException("--client-cpus needs Windows or Linux.");
        }
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var certificatePath = Path.Combine(output, "localhost.pfx");
        string thumbprint;
        using (var certificate = Orchestrator.CreateCertificate())
        {
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pkcs12, password)).ConfigureAwait(false);
            thumbprint = certificate.Thumbprint;
        }

        var environment = DescribeEnvironment(options, serverDirectory, settings, rounds, serverCpus);
        environment["hostIdleBaseline"] = await IdleBaselineAsync(options.Number("--baseline-seconds", 10)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "environment.json"), environment.ToJsonString(Indented)).ConfigureAwait(false);

        // Contention control: a separate process spinning this many threads for the whole
        // run, so the overload hypothesis can be tested rather than inferred.
        var burnThreads = options.Integer("--burn-threads", 0);
        if (burnThreads < 0) throw new ArgumentException("--burn-threads must not be negative.");
        var burner = burnThreads == 0 ? null
            : ChildProcess.Start(AppContext.BaseDirectory, ["burn", "--threads", burnThreads.ToString(CultureInfo.InvariantCulture)], null, Path.Combine(output, "burn.stderr.log"));
        var runs = new JsonArray();
        var failures = 0L;
        try
        {
            if (burner is not null && await burner.ReadLineAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false) != "BURNING")
                throw new InvalidOperationException("CPU burner did not start.");
            for (var round = 1; round <= rounds; round++)
            {
                // Engine order alternates by round.
                var order = round % 2 == 1 ? engines : [.. engines.Reverse()];
                foreach (var engine in order)
                {
                    var label = FormattableString.Invariant($"{engine}-r{round}");
                    var run = await RunEngineAsync(engine, label, output, serverDirectory, serverCpus, certificatePath, password, thumbprint, settings).ConfigureAwait(false);
                    run["burnThreads"] = burnThreads;
                    failures += run["failureCount"]?.GetValue<long>() ?? 0;
                    runs.Add(run);
                    await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), run.ToJsonString(Indented)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (burner is not null)
            {
                await burner.RequestAsync("stop", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                await burner.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }

        var summary = Summarize(runs);
        await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), summary).ConfigureAwait(false);
        Control.Write(summary);
        Control.Write((failures == 0 ? "QUIC CHURN OK " : FormattableString.Invariant($"QUIC CHURN FAILURES {failures} ")) + output);
        return failures == 0 ? 0 : 1;
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<JsonObject> RunEngineAsync(string engine, string label, string output, string serverDirectory, string? serverCpus,
        string certificatePath, string password, string thumbprint, ChurnSettings settings)
    {
        var port = Orchestrator.FreePort();
        var timeout = TimeSpan.FromMinutes(2);
        var arguments = new List<string>
        {
            "server", "--engine", engine, "--protocol", nameof(Protocol.Http3), "--port", port.ToString(CultureInfo.InvariantCulture),
            "--tls", "--certificate", certificatePath, "--certificate-password", password,
        };
        var server = ChildProcess.Start(serverDirectory, arguments, serverCpus, Path.Combine(output, label + ".server.stderr.log"));
        var run = new JsonObject { ["label"] = label, ["engine"] = engine, ["port"] = port, ["startedUtc"] = Now() };
        var phases = new JsonArray();
        run["phases"] = phases;
        var failureCount = 0L;
        try
        {
            var ready = await server.ReadLineAsync(timeout).ConfigureAwait(false);
            if (ready is null || !ready.StartsWith("READY ", StringComparison.Ordinal)) throw new InvalidOperationException("Server did not start: " + ready);
            var readyJson = JsonNode.Parse(ready[6..]);
            run["server"] = readyJson;
            var serverProcessId = readyJson?["processId"]?.GetValue<int>() ?? throw new InvalidOperationException("Server did not report its process id.");
            using var serverProcess = Process.GetProcessById(serverProcessId);
            var endpoint = new IPEndPoint(IPAddress.Loopback, port);

            // Health check: one churn attempt must succeed before measuring.
            var health = await ChurnAttempt.RunAsync(endpoint, thumbprint, settings.HandshakeTimeout, CancellationToken.None).ConfigureAwait(false);
            run["health"] = health.ToJson();
            if (health.Error is not null) throw new InvalidOperationException("Health attempt failed: " + health.Error);

            foreach (var rate in settings.Rates)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IdleSeconds)).ConfigureAwait(false);
                var before = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
                if (await server.RequestAsync("start", timeout).ConfigureAwait(false) != "MEASURING") throw new InvalidOperationException("Server did not start measuring.");
                var phase = await PhaseAsync(endpoint, thumbprint, rate, settings, serverProcess).ConfigureAwait(false);
                var window = await server.RequestJsonAsync("stop", timeout).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(settings.IdleSeconds)).ConfigureAwait(false);
                var after = await server.RequestJsonAsync("snapshot", timeout).ConfigureAwait(false);
                phase["serverWindow"] = window?["window"]?.DeepClone();
                phase["serverSnapshotBefore"] = before;
                phase["serverSnapshotAfter"] = after;
                failureCount += phase["failureCount"]?.GetValue<long>() ?? 0;
                phases.Add(phase);
                Control.Write(FormattableString.Invariant(
                    $"{label} rate {rate}/s: {phase["completed"]} completed, {phase["failureCount"]} failed, handshake p99 {phase["handshake"]?["p99Milliseconds"]} ms, max {phase["handshake"]?["maxMilliseconds"]} ms"));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or IOException or JsonException)
        {
            run["error"] = exception.Message;
            failureCount++;
        }
        finally
        {
            try
            {
                run["stop"] = await server.RequestJsonAsync("exit", TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                run["serverExitCode"] = await server.WaitForExitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or JsonException or InvalidOperationException)
            {
                run["stopError"] = exception.Message;
                failureCount++;
            }

            run["serverKilled"] = await server.DisposeAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            run["endedUtc"] = Now();
        }

        run["failureCount"] = failureCount;
        return run;
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<JsonObject> PhaseAsync(IPEndPoint endpoint, string thumbprint, double rate, ChurnSettings settings, Process serverProcess)
    {
        var duration = TimeSpan.FromSeconds(settings.PhaseSeconds);
        var handshake = new SharedHistogram();
        var stream = new SharedHistogram();
        var close = new SharedHistogram();
        var lag = new SharedHistogram();
        var failures = new ConcurrentQueue<JsonObject>();
        var counters = new PhaseCounters();
        using var mixed = settings.MixedRate > 0 || settings.LargeRate > 0
            ? new MixedLoad(endpoint.Port, thumbprint, settings.MixedConnections)
            : null;
        if (mixed is not null) await mixed.WarmAsync().ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        var startedUtc = Now();
        using var stop = new CancellationTokenSource();
        var timeline = Task.Run(() => TimelineAsync(clock, counters, mixed, serverProcess, stop.Token));
        var mixedTasks = new List<Task>();
        if (mixed is not null)
        {
            if (settings.MixedRate > 0) mixedTasks.Add(mixed.RunAsync(Payloads.Plaintext.Length, "/plaintext", settings.MixedRate, duration, clock));
            if (settings.LargeRate > 0) mixedTasks.Add(mixed.RunAsync(Scenario.Mebibyte, "/bytes/1048576", settings.LargeRate, duration, clock));
        }

        var attempts = new ConcurrentDictionary<long, Task>();
        var interval = Stopwatch.Frequency / rate;
        long index = 0;
        while (true)
        {
            var due = (long)(index * interval);
            if (due >= duration.TotalSeconds * Stopwatch.Frequency) break;
            var wait = due - clock.ElapsedTicks;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency)).ConfigureAwait(false);
            var number = index++;
            if (counters.InFlight >= settings.MaxOutstanding)
            {
                // Open-loop pacing: a missed slot is a saturation signal, recorded as such.
                Interlocked.Increment(ref counters.SkippedAtCap);
                continue;
            }

            var lagTicks = Math.Max(0, clock.ElapsedTicks - due);
            lag.Record(lagTicks);
            counters.Start();
            var offset = clock.Elapsed.TotalSeconds;
            attempts[number] = Task.Run(async () =>
            {
                var result = await ChurnAttempt.RunAsync(endpoint, thumbprint, settings.HandshakeTimeout, CancellationToken.None).ConfigureAwait(false);
                if (result.HandshakeTicks is { } h)
                {
                    handshake.Record(h);
                    counters.RecordHandshake(h);
                }
                if (result.StreamTicks is { } s) stream.Record(s);
                if (result.CloseTicks is { } c) close.Record(c);
                if (result.Error is null)
                {
                    counters.Complete();
                }
                else
                {
                    counters.Fail();
                    var record = result.ToJson();
                    record["attempt"] = number;
                    record["startedAtSeconds"] = offset;
                    record["scheduleLagMilliseconds"] = lagTicks * 1000.0 / Stopwatch.Frequency;
                    failures.Enqueue(record);
                }

                attempts.TryRemove(number, out _);
            });
        }

        var scheduled = clock.Elapsed;
        await Task.WhenAll(attempts.Values.ToArray()).ConfigureAwait(false);
        await Task.WhenAll(mixedTasks).ConfigureAwait(false);
        var elapsed = clock.Elapsed;
        await stop.CancelAsync().ConfigureAwait(false);
        var samples = await timeline.ConfigureAwait(false);

        var phase = new JsonObject
        {
            ["rate"] = rate,
            ["startedUtc"] = startedUtc,
            ["scheduledSeconds"] = scheduled.TotalSeconds,
            ["elapsedSeconds"] = elapsed.TotalSeconds,
            ["started"] = counters.Started,
            ["completed"] = counters.Completed,
            ["failureCount"] = counters.Failed + (mixed?.Failures ?? 0),
            ["churnFailures"] = counters.Failed,
            ["skippedAtCap"] = counters.SkippedAtCap,
            ["peakInFlight"] = counters.PeakInFlight,
            ["handshake"] = handshake.ToJson(),
            ["firstStream"] = stream.ToJson(),
            ["close"] = close.ToJson(),
            ["scheduleLag"] = lag.ToJson(),
            ["failures"] = new JsonArray([.. failures.OrderBy(item => item["attempt"]?.GetValue<long>())]),
            ["timeline"] = samples,
        };
        if (mixed is not null) phase["mixed"] = mixed.ToJson();
        return phase;
    }

    // Per-second host, server and client CPU with the churn counters, so any failure
    // can be read against the load at that moment.
    private static async Task<JsonArray> TimelineAsync(Stopwatch clock, PhaseCounters counters, MixedLoad? mixed, Process server, CancellationToken stop)
    {
        var samples = new JsonArray();
        using var self = Process.GetCurrentProcess();
        var busy = SystemCpu.BusyTime();
        server.Refresh();
        var serverCpu = server.TotalProcessorTime;
        var clientCpu = self.TotalProcessorTime;
        var last = clock.Elapsed;
        long started = 0, completed = 0, failed = 0, mixedDone = 0;
        while (true)
        {
            try
            {
                await Task.Delay(1000, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = clock.Elapsed;
            var busyNow = SystemCpu.BusyTime();
            server.Refresh();
            self.Refresh();
            var serverNow = server.HasExited ? serverCpu : server.TotalProcessorTime;
            var clientNow = self.TotalProcessorTime;
            var seconds = (now - last).TotalSeconds;
            var host = (busyNow - busy).TotalSeconds;
            var serverUsed = (serverNow - serverCpu).TotalSeconds;
            var clientUsed = (clientNow - clientCpu).TotalSeconds;
            samples.Add(new JsonObject
            {
                ["t"] = Math.Round(now.TotalSeconds, 3),
                ["hostBusyCpus"] = Math.Round(host / seconds, 3),
                ["serverCpus"] = Math.Round(serverUsed / seconds, 3),
                ["clientCpus"] = Math.Round(clientUsed / seconds, 3),
                ["backgroundCpus"] = Math.Round((host - serverUsed - clientUsed) / seconds, 3),
                ["started"] = counters.Started - started,
                ["completed"] = counters.Completed - completed,
                ["failed"] = counters.Failed - failed,
                ["inFlight"] = counters.InFlight,
                ["mixedCompleted"] = (mixed?.Completed ?? 0) - mixedDone,
                ["handshakeMaxMilliseconds"] = Math.Round(counters.TakeIntervalHandshakeMax() * 1000.0 / Stopwatch.Frequency, 3),
            });
            started = counters.Started;
            completed = counters.Completed;
            failed = counters.Failed;
            mixedDone = mixed?.Completed ?? 0;
            busy = busyNow;
            serverCpu = serverNow;
            clientCpu = clientNow;
            last = now;
        }

        return samples;
    }

    // Child process for --burn-threads: busy-spins until "stop" arrives on stdin.
    internal static async Task<int> BurnAsync(CommandLine options)
    {
        var threads = options.Integer("--threads", Environment.ProcessorCount);
        using var stop = new CancellationTokenSource();
        var spinners = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            var value = 0UL;
            while (!stop.IsCancellationRequested) value = unchecked((value * 6364136223846793005UL) + 1442695040888963407UL);
            GC.KeepAlive(value);
        })
        { IsBackground = true }).ToArray();
        foreach (var spinner in spinners) spinner.Start();
        Control.Write("BURNING");
        await Console.In.ReadLineAsync().ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        foreach (var spinner in spinners) spinner.Join();
        Control.Write("STOPPED");
        return 0;
    }

    private static async Task<JsonObject> IdleBaselineAsync(double seconds)
    {
        var busy = SystemCpu.BusyTime();
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        var used = (SystemCpu.BusyTime() - busy).TotalSeconds;
        return new JsonObject
        {
            ["seconds"] = clock.Elapsed.TotalSeconds,
            ["busyCpus"] = Math.Round(used / clock.Elapsed.TotalSeconds, 3),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["foreignDotnetProcesses"] = Process.GetProcesses().Count(process =>
            {
                using (process) return process.Id != Environment.ProcessId && process.ProcessName.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.StartsWith("EmbedIO", StringComparison.OrdinalIgnoreCase);
            }),
        };
    }

    private static JsonObject DescribeEnvironment(CommandLine options, string serverDirectory, ChurnSettings settings, int rounds, string? serverCpus)
    {
        static string Hash(string file) => File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : "missing";
        var runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory();
        var aspnetDirectory = Path.GetDirectoryName(typeof(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions).Assembly.Location) ?? string.Empty;
        var msquic = new[] { "msquic.dll", "libmsquic.so", "libmsquic.dylib" }.Select(name => Path.Combine(runtimeDirectory, name)).FirstOrDefault(File.Exists);
        return new JsonObject
        {
            ["capturedUtc"] = Now(),
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processor"] = SystemCpu.ProcessorName(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["totalMemoryBytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["runtimeDirectory"] = runtimeDirectory,
            ["sourceRevision"] = options.Optional("--source-revision"),
            ["serverDirectory"] = serverDirectory,
            ["serverCpus"] = serverCpus,
            ["clientCpus"] = options.Optional("--client-cpus"),
            ["burnThreads"] = options.Integer("--burn-threads", 0),
            ["runnerSha256"] = Hash(Path.Combine(serverDirectory, "EmbedIO.LoadBenchmark.dll")),
            ["embedioSha256"] = Hash(Path.Combine(serverDirectory, "EmbedIO.dll")),
            ["kestrelCoreSha256"] = Hash(Path.Combine(aspnetDirectory, "Microsoft.AspNetCore.Server.Kestrel.Core.dll")),
            ["kestrelQuicSha256"] = Hash(Path.Combine(aspnetDirectory, "Microsoft.AspNetCore.Server.Kestrel.Transport.Quic.dll")),
            ["systemNetQuicSha256"] = Hash(Path.Combine(runtimeDirectory, "System.Net.Quic.dll")),
            ["msquicPath"] = msquic,
            ["msquicSha256"] = msquic is null ? "not in runtime directory (system library)" : Hash(msquic),
            ["msquicVersion"] = msquic is null ? null : FileVersionInfo.GetVersionInfo(msquic).FileVersion,
            ["rates"] = new JsonArray([.. settings.Rates.Select(rate => (JsonNode)rate)]),
            ["phaseSeconds"] = settings.PhaseSeconds,
            ["idleSeconds"] = settings.IdleSeconds,
            ["rounds"] = rounds,
            ["maxOutstanding"] = settings.MaxOutstanding,
            ["handshakeTimeout"] = settings.HandshakeTimeout is { } t ? t.TotalSeconds : "runtime default",
            ["mixedRate"] = settings.MixedRate,
            ["largeRate"] = settings.LargeRate,
            ["mixedConnections"] = settings.MixedConnections,
            ["method"] = "One server process per engine run; paced open-loop QUIC churn (connect, one validated GET on a new request stream, graceful close) per rate phase; optional paced warm-connection HttpClient HTTP/3 traffic; every failure recorded with its phase; no retries.",
        };
    }

    private static string Summarize(JsonArray runs)
    {
        var text = new StringBuilder();
        text.AppendLine("| run | rate/s | started | completed | failed | skipped | handshake p50/p99/max ms | first stream p50/p99/max ms | close p99 ms | mixed p99 ms | host busy CPUs mean/max | background CPUs mean/max | server CPUs mean |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---|---|---:|---:|---:|---:|---:|");
        foreach (var run in runs)
        {
            if (run is null) continue;
            if (run["error"] is { } error) text.AppendLine(CultureInfo.InvariantCulture, $"| {run["label"]} | error: {error} |");
            foreach (var phase in run["phases"]?.AsArray() ?? [])
            {
                if (phase is null) continue;
                var timeline = phase["timeline"]?.AsArray() ?? [];
                var hostMax = timeline.Select(sample => sample?["hostBusyCpus"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max();
                var serverMean = timeline.Select(sample => sample?["serverCpus"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Average();
                var background = timeline.Select(sample => sample?["backgroundCpus"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).ToArray();
                var hostMean = timeline.Select(sample => sample?["hostBusyCpus"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Average();
                static string Triple(JsonNode? node) => FormattableString.Invariant($"{node?["p50Milliseconds"]}/{node?["p99Milliseconds"]}/{node?["maxMilliseconds"]}");
                var mixedP99 = phase["mixed"]?["small"]?["p99Milliseconds"]?.ToString() ?? "-";
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"| {run["label"]} | {phase["rate"]} | {phase["started"]} | {phase["completed"]} | {phase["failureCount"]} | {phase["skippedAtCap"]} | {Triple(phase["handshake"])} | {Triple(phase["firstStream"])} | {phase["close"]?["p99Milliseconds"]} | {mixedP99} | {hostMean:F1}/{hostMax:F1} | {background.Average():F1}/{background.Max():F1} | {serverMean:F2} |");
            }
        }

        return text.ToString();
    }

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private sealed record ChurnSettings(double[] Rates, double PhaseSeconds, double IdleSeconds, int MaxOutstanding, TimeSpan? HandshakeTimeout,
        double MixedRate, double LargeRate, int MixedConnections);

    private sealed class PhaseCounters
    {
        internal long SkippedAtCap;
        private long _started;
        private long _completed;
        private long _failed;
        private long _inFlight;
        private long _peak;
        private long _intervalHandshakeMax;

        internal long Started => Interlocked.Read(ref _started);

        internal long Completed => Interlocked.Read(ref _completed);

        internal long Failed => Interlocked.Read(ref _failed);

        internal long InFlight => Interlocked.Read(ref _inFlight);

        internal long PeakInFlight => Interlocked.Read(ref _peak);

        internal void Start()
        {
            Interlocked.Increment(ref _started);
            var now = Interlocked.Increment(ref _inFlight);
            long peak;
            while (now > (peak = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, peak) != peak)
            {
                // Retry until the peak is at least the current count.
            }
        }

        internal void Complete()
        {
            Interlocked.Increment(ref _completed);
            Interlocked.Decrement(ref _inFlight);
        }

        // Largest handshake since the previous timeline sample.
        internal void RecordHandshake(long ticks)
        {
            long current;
            while (ticks > (current = Interlocked.Read(ref _intervalHandshakeMax)) && Interlocked.CompareExchange(ref _intervalHandshakeMax, ticks, current) != current)
            {
                // Retry until the stored maximum is at least this value.
            }
        }

        internal long TakeIntervalHandshakeMax() => Interlocked.Exchange(ref _intervalHandshakeMax, 0);

        internal void Fail()
        {
            Interlocked.Increment(ref _failed);
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

// Thread-safe wrapper over the harness's full-resolution latency histogram.
internal sealed class SharedHistogram
{
    private readonly Lock _gate = new();
    private readonly LatencyHistogram _histogram = new();

    internal long Count
    {
        get
        {
            lock (_gate) return _histogram.Count;
        }
    }

    internal void Record(long ticks)
    {
        lock (_gate) _histogram.RecordTicks(ticks);
    }

    internal JsonObject ToJson()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["count"] = _histogram.Count,
                ["p50Milliseconds"] = _histogram.PercentileMilliseconds(0.50),
                ["p90Milliseconds"] = _histogram.PercentileMilliseconds(0.90),
                ["p99Milliseconds"] = _histogram.PercentileMilliseconds(0.99),
                ["p999Milliseconds"] = _histogram.PercentileMilliseconds(0.999),
                ["maxMilliseconds"] = _histogram.MaxUnits / 10_000.0,
                ["histogramUnits"] = "100ns upper bounds",
                ["histogram"] = new JsonArray([.. _histogram.NonEmptyBuckets().Select(bucket => (JsonNode)new JsonArray(bucket[0], bucket[1]))]),
            };
        }
    }
}

// One churn attempt on a raw QUIC connection with a minimal HTTP/3 client:
// a control stream carrying an empty SETTINGS frame, then one GET /plaintext whose
// header block uses only the QPACK static table (the client advertises no dynamic
// table, so a conforming server must not reference one). The response must carry
// :status 200 as a static indexed field and exactly the plaintext body.
internal sealed record ChurnResult(long? HandshakeTicks, long? StreamTicks, long? CloseTicks, string? Phase, string? Error, double TotalMilliseconds)
{
    internal JsonObject ToJson() => new()
    {
        ["handshakeMilliseconds"] = Milliseconds(HandshakeTicks),
        ["streamMilliseconds"] = Milliseconds(StreamTicks),
        ["closeMilliseconds"] = Milliseconds(CloseTicks),
        ["totalMilliseconds"] = TotalMilliseconds,
        ["phase"] = Phase,
        ["error"] = Error,
    };

    private static double? Milliseconds(long? ticks) => ticks is { } value ? value * 1000.0 / Stopwatch.Frequency : null;
}

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal static class ChurnAttempt
{
    private const long H3NoError = 0x100;

    // Static table indices from RFC 9204 Appendix A.
    private static readonly Dictionary<int, int> StaticStatus = new()
    {
        [24] = 103,
        [25] = 200,
        [26] = 304,
        [27] = 404,
        [28] = 503,
        [63] = 100,
        [64] = 204,
        [65] = 206,
        [66] = 302,
        [67] = 400,
        [68] = 403,
        [69] = 421,
        [70] = 425,
        [71] = 500,
    };

    internal static async Task<ChurnResult> RunAsync(IPEndPoint endpoint, string thumbprint, TimeSpan? handshakeTimeout, CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        long? handshakeTicks = null, streamTicks = null, closeTicks = null;
        var phase = "handshake";
        QuicConnection? connection = null;
        try
        {
            var options = new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultStreamErrorCode = 0x10C, // H3_REQUEST_CANCELLED
                DefaultCloseErrorCode = H3NoError,
                MaxInboundUnidirectionalStreams = 8, // server control and QPACK streams
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [SslApplicationProtocol.Http3],
                    RemoteCertificateValidationCallback = LoadClient.Pinned(thumbprint),
                },
            };
            if (handshakeTimeout is { } timeout) options.HandshakeTimeout = timeout;
            var started = Stopwatch.GetTimestamp();
            connection = await QuicConnection.ConnectAsync(options, cancellation).ConfigureAwait(false);
            handshakeTicks = Stopwatch.GetTimestamp() - started;

            phase = "stream";
            started = Stopwatch.GetTimestamp();
            var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellation).ConfigureAwait(false);
            await using (control.ConfigureAwait(false))
            {
                // Stream type 0x00 (control), SETTINGS frame (0x04) with no settings.
                await control.WriteAsync(new byte[] { 0x00, 0x04, 0x00 }, cancellation).ConfigureAwait(false);
                var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellation).ConfigureAwait(false);
                await using (request.ConfigureAwait(false))
                {
                    await request.WriteAsync(RequestFrame(endpoint.Port), completeWrites: true, cancellation).ConfigureAwait(false);
                    await ReadResponseAsync(request, cancellation).ConfigureAwait(false);
                }

                streamTicks = Stopwatch.GetTimestamp() - started;
                phase = "close";
                started = Stopwatch.GetTimestamp();
                await connection.CloseAsync(H3NoError, cancellation).ConfigureAwait(false);
                closeTicks = Stopwatch.GetTimestamp() - started;
            }

            return new ChurnResult(handshakeTicks, streamTicks, closeTicks, null, null, total.Elapsed.TotalMilliseconds);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException))
        {
            var detail = error is QuicException quic
                ? FormattableString.Invariant($"QuicException {quic.QuicError} app={quic.ApplicationErrorCode} transport={quic.TransportErrorCode}: {quic.Message}")
                : error.GetType().Name + ": " + error.Message;
            return new ChurnResult(handshakeTicks, streamTicks, closeTicks, phase, detail, total.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static byte[] RequestFrame(int port)
    {
        var authority = Encoding.ASCII.GetBytes("localhost:" + port.ToString(CultureInfo.InvariantCulture));
        var path = "/plaintext"u8;
        var block = new List<byte>
        {
            0x00, 0x00, // Required Insert Count 0, Delta Base 0
            0xD1, // :method GET (static 17)
            0xD7, // :scheme https (static 23)
            0x50, // :authority (static 0), literal value
        };
        AppendLiteral(block, authority);
        block.Add(0x51); // :path (static 1), literal value
        AppendLiteral(block, path);
        var frame = new List<byte> { 0x01 }; // HEADERS
        AppendVarint(frame, block.Count);
        frame.AddRange(block);
        return [.. frame];
    }

    private static void AppendLiteral(List<byte> block, ReadOnlySpan<byte> value)
    {
        // H = 0, 7-bit prefix length. The values here are short.
        if (value.Length >= 127) throw new ArgumentOutOfRangeException(nameof(value));
        block.Add((byte)value.Length);
        foreach (var b in value) block.Add(b);
    }

    private static void AppendVarint(List<byte> buffer, long value)
    {
        if (value < 64)
        {
            buffer.Add((byte)value);
        }
        else if (value < 16384)
        {
            buffer.Add((byte)(0x40 | (value >> 8)));
            buffer.Add((byte)value);
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static async Task ReadResponseAsync(QuicStream stream, CancellationToken cancellation)
    {
        // Read the whole response stream (it is small), then parse the frames.
        var data = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read == 0) break;
            data.Write(buffer, 0, read);
            if (data.Length > 65536) throw new InvalidDataException("Response stream exceeded 64 KiB.");
        }

        var bytes = data.GetBuffer().AsSpan(0, (int)data.Length);
        int? status = null;
        var body = new MemoryStream();
        while (!bytes.IsEmpty)
        {
            var type = ReadVarint(ref bytes);
            var length = ReadVarint(ref bytes);
            if (length > bytes.Length) throw new InvalidDataException("Truncated HTTP/3 frame.");
            var payload = bytes[..(int)length];
            bytes = bytes[(int)length..];
            switch (type)
            {
                case 0x01 when status is null:
                    status = ReadStatus(payload);
                    break;
                case 0x01:
                    break; // Trailers are not expected but are permitted.
                case 0x00 when status is not null:
                    body.Write(payload);
                    break;
                case 0x00:
                    throw new InvalidDataException("DATA before HEADERS.");
                default:
                    break; // Unknown and reserved frame types are ignored (RFC 9114 9).
            }
        }

        if (status != 200) throw new InvalidDataException("Unexpected status " + (status?.ToString(CultureInfo.InvariantCulture) ?? "missing"));
        if (!body.ToArray().AsSpan().SequenceEqual(Payloads.Plaintext)) throw new InvalidDataException("Unexpected response body of " + body.Length.ToString(CultureInfo.InvariantCulture) + " bytes.");
    }

    private static int ReadStatus(ReadOnlySpan<byte> block)
    {
        // Field section prefix: Required Insert Count (8-bit prefix) and Delta Base (7-bit prefix).
        if (block.Length < 3) throw new InvalidDataException("Short header block.");
        if (block[0] != 0) throw new InvalidDataException("Response references a dynamic table the client never offered.");
        var first = block[2];
        if ((first & 0xC0) == 0xC0 && (first & 0x3F) != 0x3F && StaticStatus.TryGetValue(first & 0x3F, out var status)) return status;
        throw new InvalidDataException(FormattableString.Invariant($"First field line 0x{first:X2} is not a static indexed :status; this probe does not decode other forms."));
    }

    private static long ReadVarint(ref Span<byte> bytes)
    {
        if (bytes.IsEmpty) throw new InvalidDataException("Truncated varint.");
        var length = 1 << (bytes[0] >> 6);
        if (bytes.Length < length) throw new InvalidDataException("Truncated varint.");
        long value = bytes[0] & 0x3F;
        for (var i = 1; i < length; i++) value = (value << 8) | bytes[i];
        bytes = bytes[length..];
        return value;
    }
}

// Paced warm-connection HTTP/3 traffic through HttpClient, validated like the load client.
internal sealed class MixedLoad(int port, string thumbprint, int connections) : IDisposable
{
    private readonly HttpClient[] _clients = [.. Enumerable.Range(0, connections).Select(_ => new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = LoadClient.Pinned(thumbprint) },
    }) { Timeout = Timeout.InfiniteTimeSpan })];

    private readonly string _origin = "https://localhost:" + port.ToString(CultureInfo.InvariantCulture);
    private readonly SharedHistogram _small = new();
    private readonly SharedHistogram _large = new();
    private readonly ConcurrentQueue<JsonObject> _errors = new();
    private long _completed;
    private long _failures;

    internal long Completed => Interlocked.Read(ref _completed);

    internal long Failures => Interlocked.Read(ref _failures);

    internal async Task WarmAsync()
    {
        Payloads.Prepare(Scenario.Mebibyte);
        foreach (var client in _clients) await SendAsync(client, "/plaintext", Payloads.Plaintext.Length).ConfigureAwait(false);
    }

    internal async Task RunAsync(int length, string path, double rate, TimeSpan duration, Stopwatch clock)
    {
        var histogram = length == Payloads.Plaintext.Length ? _small : _large;
        var interval = Stopwatch.Frequency / rate;
        var pending = new List<Task>();
        long index = 0;
        while (true)
        {
            var due = (long)(index * interval);
            if (due >= duration.TotalSeconds * Stopwatch.Frequency) break;
            var wait = due - clock.ElapsedTicks;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency)).ConfigureAwait(false);
            var client = _clients[index++ % _clients.Length];
            var offset = clock.Elapsed.TotalSeconds;
            pending.Add(Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await SendAsync(client, path, length).ConfigureAwait(false);
                    histogram.Record(Stopwatch.GetTimestamp() - started);
                    Interlocked.Increment(ref _completed);
                }
                catch (Exception error) when (error is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException))
                {
                    Interlocked.Increment(ref _failures);
                    _errors.Enqueue(new JsonObject
                    {
                        ["path"] = path,
                        ["startedAtSeconds"] = offset,
                        ["elapsedMilliseconds"] = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency,
                        ["error"] = error.GetType().Name + ": " + error.Message,
                    });
                }
            }));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    internal JsonObject ToJson() => new()
    {
        ["completed"] = Completed,
        ["failures"] = Failures,
        ["small"] = _small.ToJson(),
        ["large"] = _large.Count == 0 ? null : _large.ToJson(),
        ["errors"] = new JsonArray([.. _errors]),
    };

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
    }

    private async Task SendAsync(HttpClient client, string path, int length)
    {
        // One Uri per request (see Storm: a shared instance raced inside HttpClient on .NET 10.0.12).
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_origin + path))
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK || response.Version != HttpVersion.Version30)
            throw new InvalidDataException(FormattableString.Invariant($"Unexpected {(int)response.StatusCode} HTTP/{response.Version}"));
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var expected = length == Payloads.Plaintext.Length ? Payloads.Plaintext : Payloads.Get(length);
        if (!body.AsSpan().SequenceEqual(expected)) throw new InvalidDataException("Response body mismatch.");
    }
}
