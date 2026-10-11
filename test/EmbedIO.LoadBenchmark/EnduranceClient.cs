using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

internal enum Transport
{
    H1,
    H1Tls,
    H2c,
    H2Tls,
    H3,
}

internal enum WorkKind
{
    Small,
    Large,
    Upload,
    Stream,
    Churn,
    Cancel,
    Abrupt,
    SlowRead,
    SlowWrite,
    SlowHead,
    IdleHold,
    WebSocketChurn,
    WebSocketHold,
}

// One workload: a kind on a transport with a number of concurrent workers. Rate is
// requests (or connections) per second per worker; zero leaves the loop closed.
internal sealed record Workload(WorkKind Kind, Transport Transport, int Workers, int Streams = 1, double Rate = 0)
{
    internal string Name => $"{Kind.ToString().ToLowerInvariant()}-{Transport.ToString().ToLowerInvariant()}";
}

internal static class EnduranceMixes
{
    private static readonly Transport[] All = [Transport.H1, Transport.H1Tls, Transport.H2c, Transport.H2Tls, Transport.H3];

    // Steady load is paced so throughput stays constant across hours and its resource
    // trends are comparable; "saturate" removes pacing for short stress segments.
    internal static List<Workload> Select(string name, double scale) => name switch
    {
        "health" => [.. All.Select(transport => new Workload(WorkKind.Small, transport, 1)), new(WorkKind.WebSocketChurn, Transport.H1, 1, 1, 5), new(WorkKind.WebSocketChurn, Transport.H2Tls, 1, 1, 5)],
        "steady" => Steady(scale),
        "faults" => Faults(scale),
        "ws" => WebSockets(scale),
        "mixed" => [.. Steady(scale), .. Faults(scale), .. WebSockets(scale)],
        "saturate" => [.. All.Select(transport => new Workload(WorkKind.Small, transport, transport is Transport.H1 or Transport.H1Tls ? 16 : 4, transport is Transport.H1 or Transport.H1Tls ? 1 : 16))],
        _ => throw new ArgumentException("Unknown mix " + name),
    };

    private static List<Workload> Steady(double scale)
    {
        var list = new List<Workload>();
        foreach (var transport in All)
        {
            var multiplexed = transport is not (Transport.H1 or Transport.H1Tls);
            list.Add(new(WorkKind.Small, transport, multiplexed ? 2 : 8, multiplexed ? 8 : 1, 250 * scale));
            list.Add(new(WorkKind.Large, transport, 2, 1, 10 * scale));
            list.Add(new(WorkKind.Upload, transport, 2, 1, 10 * scale));
            list.Add(new(WorkKind.Stream, transport, 2, 1, 5 * scale));
        }

        // New connections are rate limited: TCP churn consumes TIME_WAIT entries.
        list.Add(new(WorkKind.Churn, Transport.H1, 2, 1, 5 * scale));
        list.Add(new(WorkKind.Churn, Transport.H1Tls, 2, 1, 4 * scale));
        list.Add(new(WorkKind.Churn, Transport.H2Tls, 1, 1, 4 * scale));
        list.Add(new(WorkKind.Churn, Transport.H3, 1, 1, 4 * scale));
        return list;
    }

    private static List<Workload> Faults(double scale)
    {
        var list = new List<Workload>();
        foreach (var transport in All)
        {
            list.Add(new(WorkKind.Cancel, transport, 1, 1, 4 * scale));
            list.Add(new(WorkKind.Abrupt, transport, 1, 1, 2 * scale));
            list.Add(new(WorkKind.SlowRead, transport, 1));
            list.Add(new(WorkKind.SlowWrite, transport, 1));
        }

        list.Add(new(WorkKind.SlowHead, Transport.H1, 1));
        list.Add(new(WorkKind.SlowHead, Transport.H1Tls, 1));
        list.Add(new(WorkKind.IdleHold, Transport.H1, 2));
        list.Add(new(WorkKind.IdleHold, Transport.H1Tls, 2));
        return list;
    }

    private static List<Workload> WebSockets(double scale)
    {
        var list = new List<Workload>();
        foreach (var transport in new[] { Transport.H1, Transport.H1Tls, Transport.H2c, Transport.H2Tls })
        {
            list.Add(new(WorkKind.WebSocketChurn, transport, 1, 1, 2 * scale));
            list.Add(new(WorkKind.WebSocketHold, transport, 2, 1, 2));
        }

        return list;
    }
}

// Per-workload counters and latency. Each workload has one lock; workers record
// under it, and the interval writer swaps the interval histogram under it.
internal sealed class WorkloadStats(Workload workload)
{
    private const int MaxErrorDetails = 20;
    private readonly Lock _gate = new();
    private readonly LatencyHistogram _total = new();
    private readonly List<object> _errors = [];
    private LatencyHistogram _interval = new();
    private long _ok;
    private long _intervalOk;
    private long _bytes;
    private long _connections;
    private long _clientAborts;
    private long _serverCloses;
    private long _disrupted;
    private long _errorCount;
    private long _intervalErrors;
    private long _intervalDisrupted;
    private readonly Dictionary<string, long> _outcomes = new(StringComparer.Ordinal);

    internal Workload Workload => workload;

    internal long Errors { get { lock (_gate) return _errorCount; } }

    internal void Completed(long ticks, long bytes)
    {
        lock (_gate)
        {
            _total.RecordTicks(ticks);
            _interval.RecordTicks(ticks);
            _ok++;
            _intervalOk++;
            _bytes += bytes;
        }
    }

    internal void Connected() => Interlocked.Increment(ref _connections);

    // A deliberate client-side cancellation or abort; not an error.
    internal void ClientAborted() => Interlocked.Increment(ref _clientAborts);

    // An outcome category for fault workloads, such as how the server ended an idle connection.
    internal void Outcome(string name, long ticks = 0)
    {
        lock (_gate)
        {
            _outcomes[name] = _outcomes.GetValueOrDefault(name) + 1;
            if (ticks > 0) _interval.RecordTicks(ticks);
        }
    }

    internal void ServerClosed() => Interlocked.Increment(ref _serverCloses);

    internal void Failed(Exception exception, bool disrupted, double atSeconds)
    {
        lock (_gate)
        {
            if (disrupted)
            {
                _disrupted++;
                _intervalDisrupted++;
                return;
            }

            _errorCount++;
            _intervalErrors++;
            if (_errors.Count < MaxErrorDetails) _errors.Add(new { atSeconds, utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), error = EnduranceRun.Describe(exception) });
        }
    }

    internal object Interval()
    {
        lock (_gate)
        {
            var result = new
            {
                ok = _intervalOk,
                errors = _intervalErrors,
                disrupted = _intervalDisrupted,
                p50 = _interval.PercentileMilliseconds(0.5),
                p99 = _interval.PercentileMilliseconds(0.99),
                p999 = _interval.PercentileMilliseconds(0.999),
                max = _interval.MaxUnits / 10_000.0,
            };
            _interval = new LatencyHistogram();
            _intervalOk = 0;
            _intervalErrors = 0;
            _intervalDisrupted = 0;
            return result;
        }
    }

    internal object Report(double seconds)
    {
        lock (_gate)
        {
            return new
            {
                name = workload.Name,
                kind = workload.Kind.ToString(),
                transport = workload.Transport.ToString(),
                workers = workload.Workers,
                streams = workload.Streams,
                ratePerWorker = workload.Rate,
                ok = _ok,
                perSecond = _ok / seconds,
                bytes = _bytes,
                connections = Interlocked.Read(ref _connections),
                clientAborts = Interlocked.Read(ref _clientAborts),
                serverCloses = Interlocked.Read(ref _serverCloses),
                disrupted = _disrupted,
                errors = _errorCount,
                firstErrors = _errors.ToArray(),
                outcomes = new Dictionary<string, long>(_outcomes, StringComparer.Ordinal),
                p50 = _total.PercentileMilliseconds(0.5),
                p90 = _total.PercentileMilliseconds(0.9),
                p99 = _total.PercentileMilliseconds(0.99),
                p999 = _total.PercentileMilliseconds(0.999),
                max = _total.MaxUnits / 10_000.0,
                histogramUnits = "100ns upper bounds",
                histogram = _total.NonEmptyBuckets().ToArray(),
            };
        }
    }
}

// Shared run state: stop signals, the disruption window and connection-rate limits.
internal sealed class EnduranceRun(int plainPort, int tlsPort, string thumbprint) : IDisposable
{
    private readonly CancellationTokenSource _hard = new();
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _stopping;
    private volatile bool _disrupting;
    private long _disruptionEndTicks;

    internal int PlainPort => plainPort;

    internal int TlsPort => tlsPort;

    internal string Thumbprint => thumbprint;

    internal Stopwatch Clock { get; } = Stopwatch.StartNew();

    internal CancellationToken Token => _hard.Token;

    // Cancelled when the phase ends; only waits that may be abandoned observe it.
    internal CancellationToken StopToken => _stop.Token;

    internal bool Running => !_stopping && !_hard.IsCancellationRequested;

    // Failures while the orchestrator drains or restarts the server, and briefly
    // after, are counted as disruption rather than errors. Validation failures never are.
    internal bool Disrupted => _disrupting || Stopwatch.GetElapsedTime(Interlocked.Read(ref _disruptionEndTicks)) < TimeSpan.FromSeconds(3);

    internal void BeginDisruption() => _disrupting = true;

    internal void EndDisruption()
    {
        Interlocked.Exchange(ref _disruptionEndTicks, Stopwatch.GetTimestamp());
        _disrupting = false;
    }

    internal void Stop()
    {
        _stopping = true;
        _stop.Cancel();
    }

    internal void Abort() => _hard.Cancel();

    public void Dispose()
    {
        _hard.Dispose();
        _stop.Dispose();
    }

    internal Uri Uri(Transport transport, string path) => transport switch
    {
        Transport.H1 or Transport.H2c => new Uri($"http://localhost:{plainPort}{path}"),
        _ => new Uri($"https://localhost:{tlsPort}{path}"),
    };

    internal static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (text.Length != 0) text.Append(" ---> ");
            text.Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (current is HttpProtocolException protocol) text.Append(" (protocol error code ").Append(protocol.ErrorCode).Append(')');
            if (current is SocketException socket) text.Append(" (socket ").Append(socket.SocketErrorCode).Append(')');
            if (current is System.Net.Quic.QuicException quic && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                text.Append(" (quic ").Append(quic.QuicError).Append(')');
        }

        return text.ToString();
    }
}

internal static class EnduranceClient
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        using var run = new EnduranceRun(options.Integer("--plain-port", 0), options.Integer("--tls-port", 0), options.Required("--certificate-thumbprint"));
        var duration = TimeSpan.FromSeconds(options.Number("--duration", 60));
        var intervalSeconds = options.Number("--interval-seconds", 60);
        var mix = EnduranceMixes.Select(options.Text("--mix", "steady"), options.Number("--rate-scale", 1));
        // Optional filters for attribution runs, for example --transports h3 --kinds small,churn.
        if (options.Optional("--transports") is { } transports)
        {
            var allowed = transports.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => Enum.Parse<Transport>(name, ignoreCase: true)).ToHashSet();
            mix = [.. mix.Where(workload => allowed.Contains(workload.Transport))];
        }

        if (options.Optional("--kinds") is { } kinds)
        {
            var allowed = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(name => Enum.Parse<WorkKind>(name, ignoreCase: true)).ToHashSet();
            mix = [.. mix.Where(workload => allowed.Contains(workload.Kind))];
        }

        if (mix.Count == 0) throw new ArgumentException("The selected mix and filters leave no workloads.");
        Payloads.Prepare(1 << 20);
        var stats = mix.Select(workload => new WorkloadStats(workload)).ToList();
        await using var intervals = new StreamWriter(options.Required("--interval-file"), append: false) { AutoFlush = true };

        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes(true);
        var workers = stats.SelectMany(stat => Enumerable.Range(0, stat.Workload.Workers).Select(index => Task.Run(() => Workers.RunAsync(stat, run, index)))).ToArray();
        Control.Write("STARTED");

        // Control: stop early, or mark a disruption window around server drain/restart.
        var control = Task.Run(async () =>
        {
            while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                switch (line)
                {
                    case "disrupt-begin": run.BeginDisruption(); Control.Write("OK"); break;
                    case "disrupt-end": run.EndDisruption(); Control.Write("OK"); break;
                    case "stop": run.Stop(); return;
                    default: break;
                }
            }

            run.Stop();
        });

        var next = TimeSpan.FromSeconds(intervalSeconds);
        while (run.Running && run.Clock.Elapsed < duration)
        {
            var wait = TimeSpan.FromMilliseconds(Math.Min(500, Math.Max(1, Math.Min((next - run.Clock.Elapsed).TotalMilliseconds, (duration - run.Clock.Elapsed).TotalMilliseconds))));
            await Task.WhenAny(control, Task.Delay(wait)).ConfigureAwait(false);
            if (run.Clock.Elapsed >= next)
            {
                await WriteIntervalAsync().ConfigureAwait(false);
                next += TimeSpan.FromSeconds(intervalSeconds);
            }
        }

        var measured = run.Clock.Elapsed.TotalSeconds;
        run.Stop();
        // Workers finish the exchange in progress; slow workloads need a few seconds.
        var drained = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60)).ContinueWith(task => task.IsCompletedSuccessfully, TaskScheduler.Default).ConfigureAwait(false);
        if (!drained)
        {
            run.Abort();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        }

        await WriteIntervalAsync().ConfigureAwait(false);
        process.Refresh();
        var report = new
        {
            measuredSeconds = measured,
            workersDrainedWithin60Seconds = drained,
            errors = stats.Sum(stat => stat.Errors),
            clientCpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds,
            clientAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
            clientProcessorCount = Environment.ProcessorCount,
            clientWorkingSetBytes = process.WorkingSet64,
            workloads = stats.Select(stat => stat.Report(measured)).ToArray(),
        };
        Control.Write("DONE " + JsonSerializer.Serialize(report));
        return 0;

        async Task WriteIntervalAsync()
        {
            var line = JsonSerializer.Serialize(new
            {
                seconds = run.Clock.Elapsed.TotalSeconds,
                utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                workloads = stats.ToDictionary(stat => stat.Workload.Name, stat => stat.Interval(), StringComparer.Ordinal),
            });
            await intervals.WriteLineAsync(line).ConfigureAwait(false);
        }
    }
}

internal static class Workers
{
    private const int Large = 1 << 20;
    private const int SlowBytes = 256 * 1024;

    internal static async Task RunAsync(WorkloadStats stats, EnduranceRun run, int index)
    {
        var random = new Random(HashCode.Combine(stats.Workload.Name, index));
        var pacer = new Pacer(stats.Workload.Rate);
        // Stagger start so workers do not move in lockstep.
        await Task.Delay(random.Next(0, 500)).ConfigureAwait(false);
        while (run.Running)
        {
            try
            {
                await IterateAsync(stats, run, random, pacer).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                stats.Failed(exception, run.Disrupted, run.Clock.Elapsed.TotalSeconds);
                // Back off without spinning; the next iteration opens a fresh connection.
                await Task.Delay(run.Disrupted ? 250 : 100).ConfigureAwait(false);
            }
        }
    }

    private static Task IterateAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer)
    {
        var workload = stats.Workload;
        var http1 = workload.Transport is Transport.H1 or Transport.H1Tls;
        return workload.Kind switch
        {
            WorkKind.Small => http1 ? KeepAliveAsync(stats, run, pacer, "/plaintext", Payloads.Plaintext, 0) : MultiplexedAsync(stats, run, pacer, "/plaintext", Payloads.Plaintext, 0),
            WorkKind.Large => http1 ? KeepAliveAsync(stats, run, pacer, "/bytes/1048576", Payloads.Get(Large), 0) : MultiplexedAsync(stats, run, pacer, "/bytes/1048576", Payloads.Get(Large), 0),
            WorkKind.Upload => http1 ? KeepAliveAsync(stats, run, pacer, "/upload", Payloads.UploadAcknowledgement(Large), Large) : MultiplexedAsync(stats, run, pacer, "/upload", Payloads.UploadAcknowledgement(Large), Large),
            WorkKind.Stream => http1 ? KeepAliveAsync(stats, run, pacer, "/stream/1048576/16384", Payloads.Get(Large), 0) : MultiplexedAsync(stats, run, pacer, "/stream/1048576/16384", Payloads.Get(Large), 0),
            WorkKind.Churn => http1 ? Http1ChurnAsync(stats, run, pacer) : MultiplexedChurnAsync(stats, run, pacer),
            WorkKind.Cancel => http1 ? Http1CancelAsync(stats, run, random, pacer) : MultiplexedCancelAsync(stats, run, random, pacer),
            WorkKind.Abrupt => http1 || workload.Transport == Transport.H2c ? RawAbruptAsync(stats, run, random, pacer) : MultiplexedAbruptAsync(stats, run, random, pacer),
            WorkKind.SlowRead => http1 ? Http1SlowReadAsync(stats, run) : MultiplexedSlowReadAsync(stats, run),
            WorkKind.SlowWrite => http1 ? Http1SlowWriteAsync(stats, run) : MultiplexedSlowWriteAsync(stats, run),
            WorkKind.SlowHead => SlowHeadAsync(stats, run),
            WorkKind.IdleHold => IdleHoldAsync(stats, run, random),
            WorkKind.WebSocketChurn => WebSocketAsync(stats, run, random, pacer, hold: false),
            WorkKind.WebSocketHold => WebSocketAsync(stats, run, random, pacer, hold: true),
            _ => throw new ArgumentOutOfRangeException(nameof(stats)),
        };
    }

    // ---- HTTP/1.1 over raw sockets ----

    private static async Task<(Socket Socket, Stream Stream)> OpenHttp1Async(EnduranceRun run, bool tls, CancellationToken cancellation)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, tls ? run.TlsPort : run.PlainPort, cancellation).ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: false);
            if (tls)
            {
                var secure = new SslStream(stream, leaveInnerStreamOpen: false, LoadClient.Pinned(run.Thumbprint));
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    EnabledSslProtocols = SslProtocols.None,
                }, cancellation).ConfigureAwait(false);
                stream = secure;
            }

            return (socket, stream);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static byte[] Request(EnduranceRun run, bool tls, string path, int uploadBytes, bool close)
    {
        var host = "localhost:" + (tls ? run.TlsPort : run.PlainPort).ToString(CultureInfo.InvariantCulture);
        var connection = close ? "Connection: close\r\n" : string.Empty;
        var head = uploadBytes > 0
            ? $"POST {path} HTTP/1.1\r\nHost: {host}\r\nContent-Type: application/octet-stream\r\nContent-Length: {uploadBytes}\r\n{connection}\r\n"
            : $"GET {path} HTTP/1.1\r\nHost: {host}\r\n{connection}\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. uploadBytes > 0 ? Payloads.Get(uploadBytes) : []];
    }

    // One keep-alive connection serving paced requests until the phase ends.
    private static async Task KeepAliveAsync(WorkloadStats stats, EnduranceRun run, Pacer pacer, string path, byte[] expected, int uploadBytes)
    {
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        var request = Request(run, tls, path, uploadBytes, close: false);
        var reader = new Http1ResponseReader(stream);
        while (run.Running)
        {
            await pacer.WaitAsync(run.Token).ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            await stream.WriteAsync(request, run.Token).ConfigureAwait(false);
            var response = await reader.ReadAsync(expected, run.Token).ConfigureAwait(false);
            stats.Completed(Stopwatch.GetTimestamp() - started, response.BodyBytes);
            if (response.Close)
            {
                stats.ServerClosed();
                await reader.ExpectEndAsync(run.Token).ConfigureAwait(false);
                return;
            }
        }

        socket.Shutdown(SocketShutdown.Both);
    }

    private static async Task Http1ChurnAsync(WorkloadStats stats, EnduranceRun run, Pacer pacer)
    {
        await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        await stream.WriteAsync(Request(run, tls, "/plaintext", 0, close: true), run.Token).ConfigureAwait(false);
        var reader = new Http1ResponseReader(stream);
        var response = await reader.ReadAsync(Payloads.Plaintext, run.Token).ConfigureAwait(false);
        if (!response.Close) throw new InvalidDataException("Server ignored Connection: close.");
        await reader.ExpectEndAsync(run.Token).ConfigureAwait(false);
        stats.Completed(Stopwatch.GetTimestamp() - started, response.BodyBytes);
    }

    // Reads part of a 1 MiB response, then resets the connection.
    private static async Task Http1CancelAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer)
    {
        await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        await stream.WriteAsync(Request(run, tls, random.Next(2) == 0 ? "/bytes/1048576" : "/stream/1048576/16384", 0, close: false), run.Token).ConfigureAwait(false);
        var wanted = random.Next(0, 512 * 1024);
        var buffer = new byte[16384];
        var read = 0;
        while (read < wanted)
        {
            var count = await stream.ReadAsync(buffer, run.Token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Server closed before the client cancelled.");
            read += count;
        }

        socket.LingerState = new LingerOption(true, 0);
        stats.ClientAborted();
        stats.Outcome(wanted == 0 ? "reset-before-read" : "reset-mid-body");
    }

    // Raw abrupt disconnects: partial heads, partial uploads, pre-handshake resets and
    // (for h2c) a partial HEADERS frame, each ended by an RST.
    private static async Task RawAbruptAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer)
    {
        await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var transport = stats.Workload.Transport;
        var tls = transport == Transport.H1Tls;
        var variant = random.Next(3);
        if (tls && variant == 0)
        {
            // Reset before the TLS handshake completes.
            using var bare = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await bare.ConnectAsync(IPAddress.Loopback, run.TlsPort, run.Token).ConfigureAwait(false);
            stats.Connected();
            await bare.SendAsync("\u0016\u0003\u0001\u0002\u0000\u0001"u8.ToArray(), run.Token).ConfigureAwait(false);
            bare.LingerState = new LingerOption(true, 0);
            stats.ClientAborted();
            stats.Outcome("reset-during-handshake");
            return;
        }

        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        if (transport == Transport.H2c)
        {
            // Preface, empty SETTINGS, then a HEADERS frame header promising 64 bytes that never arrive.
            byte[] partial = [.. "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 64, 1, 4, 0, 0, 0, 1, 0x82];
            await stream.WriteAsync(partial, run.Token).ConfigureAwait(false);
            stats.Outcome("reset-mid-h2-frame");
        }
        else if (variant == 1)
        {
            var head = Request(run, tls, "/plaintext", 0, close: false);
            await stream.WriteAsync(head.AsMemory(0, random.Next(1, head.Length - 1)), run.Token).ConfigureAwait(false);
            stats.Outcome("reset-mid-head");
        }
        else
        {
            var upload = Request(run, tls, "/upload", Large, close: false);
            await stream.WriteAsync(upload.AsMemory(0, random.Next(upload.Length - Large + 1, upload.Length - 1)), run.Token).ConfigureAwait(false);
            stats.Outcome("reset-mid-upload");
        }

        await stream.FlushAsync(run.Token).ConfigureAwait(false);
        // Give the server a moment to start on the partial request, then reset.
        await Task.Delay(random.Next(0, 20), run.Token).ConfigureAwait(false);
        socket.LingerState = new LingerOption(true, 0);
        stats.ClientAborted();
    }

    // Reads a 1 MiB body at about 160 KiB/s and validates every byte.
    private static async Task Http1SlowReadAsync(WorkloadStats stats, EnduranceRun run)
    {
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        socket.ReceiveBufferSize = 16384;
        stats.Connected();
        var started = Stopwatch.GetTimestamp();
        await stream.WriteAsync(Request(run, tls, "/bytes/1048576", 0, close: true), run.Token).ConfigureAwait(false);
        var reader = new Http1ResponseReader(new ThrottledStream(stream, 16384, TimeSpan.FromMilliseconds(100), run.Token));
        var response = await reader.ReadAsync(Payloads.Get(Large), run.Token).ConfigureAwait(false);
        stats.Completed(Stopwatch.GetTimestamp() - started, response.BodyBytes);
    }

    // Uploads 256 KiB in 4 KiB writes every 25 ms, then validates the acknowledgement.
    private static async Task Http1SlowWriteAsync(WorkloadStats stats, EnduranceRun run)
    {
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        var started = Stopwatch.GetTimestamp();
        var request = Request(run, tls, "/upload", SlowBytes, close: true);
        for (var offset = 0; offset < request.Length; offset += 4096)
        {
            await stream.WriteAsync(request.AsMemory(offset, Math.Min(4096, request.Length - offset)), run.Token).ConfigureAwait(false);
            await stream.FlushAsync(run.Token).ConfigureAwait(false);
            await Task.Delay(25, run.Token).ConfigureAwait(false);
        }

        var response = await new Http1ResponseReader(stream).ReadAsync(Payloads.UploadAcknowledgement(SlowBytes), run.Token).ConfigureAwait(false);
        stats.Completed(Stopwatch.GetTimestamp() - started, response.BodyBytes);
    }

    // Sends the request head one byte every 50 ms (about 2 s), then expects a normal response.
    private static async Task SlowHeadAsync(WorkloadStats stats, EnduranceRun run)
    {
        var tls = stats.Workload.Transport == Transport.H1Tls;
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        var started = Stopwatch.GetTimestamp();
        var request = Request(run, tls, "/plaintext", 0, close: true);
        for (var offset = 0; offset < request.Length; offset++)
        {
            await stream.WriteAsync(request.AsMemory(offset, 1), run.Token).ConfigureAwait(false);
            await stream.FlushAsync(run.Token).ConfigureAwait(false);
            await Task.Delay(50, run.Token).ConfigureAwait(false);
        }

        var response = await new Http1ResponseReader(stream).ReadAsync(Payloads.Plaintext, run.Token).ConfigureAwait(false);
        stats.Completed(Stopwatch.GetTimestamp() - started, response.BodyBytes);
    }

    // Holds a connection idle (before any request, or after one keep-alive response)
    // and records when and how the server ends it. The managed listener documents
    // 90 s before the first request and 15 s between requests.
    private static async Task IdleHoldAsync(WorkloadStats stats, EnduranceRun run, Random random)
    {
        var tls = stats.Workload.Transport == Transport.H1Tls;
        // The server's initial timer starts at accept, before the TLS handshake, so the
        // client's clock for that case starts before connecting.
        var connecting = Stopwatch.GetTimestamp();
        var (socket, stream) = await OpenHttp1Async(run, tls, run.Token).ConfigureAwait(false);
        using var owned = socket;
        await using var _ = stream;
        stats.Connected();
        var afterRequest = random.Next(2) == 0;
        var limit = TimeSpan.FromSeconds(afterRequest ? 15 : 90);
        if (afterRequest)
        {
            await stream.WriteAsync(Request(run, tls, "/plaintext", 0, close: false), run.Token).ConfigureAwait(false);
            var reader = new Http1ResponseReader(stream);
            await reader.ReadAsync(Payloads.Plaintext, run.Token).ConfigureAwait(false);
        }

        var started = afterRequest ? Stopwatch.GetTimestamp() : connecting;
        var buffer = new byte[1];
        // A hard deadline well beyond the documented limit; the phase may also end first.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(run.Token, run.StopToken);
        deadline.CancelAfter(limit + TimeSpan.FromSeconds(30));
        int read;
        var reset = string.Empty;
        try
        {
            read = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.StopToken.IsCancellationRequested)
        {
            // The phase ended first; this hold has no outcome.
            return;
        }
        catch (OperationCanceledException) when (!run.Token.IsCancellationRequested)
        {
            throw new TimeoutException($"Server kept an idle connection open beyond {limit.TotalSeconds + 30} s ({(afterRequest ? "after a response" : "before any request")}).");
        }
        catch (IOException exception)
        {
            read = -1;
            reset = exception.InnerException is SocketException socketError ? socketError.SocketErrorCode.ToString() : exception.GetType().Name;
        }

        var elapsed = Stopwatch.GetTimestamp() - started;
        if (read > 0) throw new InvalidDataException("Server sent data on an idle connection.");
        if (Stopwatch.GetElapsedTime(started) < limit - TimeSpan.FromSeconds(2) && !run.Disrupted)
            throw new InvalidDataException($"Server closed an idle connection after {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s, before its {limit.TotalSeconds} s timeout.");
        stats.ServerClosed();
        stats.Outcome((afterRequest ? "keepalive-idle-" : "initial-idle-") + (read == 0 ? "fin" : "reset-" + reset), elapsed);
        stats.Completed(elapsed, 0);
    }

    // ---- HTTP/2 and HTTP/3 through SocketsHttpHandler ----

    private static SocketsHttpHandler Handler(EnduranceRun run) => new()
    {
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        EnableMultipleHttp2Connections = false,
        EnableMultipleHttp3Connections = false,
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = LoadClient.Pinned(run.Thumbprint) },
    };

    private static Version VersionOf(Transport transport) => transport == Transport.H3 ? HttpVersion.Version30 : HttpVersion.Version20;

    private static HttpRequestMessage Message(EnduranceRun run, Transport transport, string path, HttpContent? content) => new(content is null ? HttpMethod.Get : HttpMethod.Post, run.Uri(transport, path))
    {
        Version = VersionOf(transport),
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Content = content,
    };

    private static async Task<long> SendAndValidateAsync(HttpMessageInvoker client, EnduranceRun run, Transport transport, string path, HttpContent? content, byte[] expected, byte[] buffer, CancellationToken cancellation, TimeSpan readDelay = default)
    {
        using var request = Message(run, transport, path, content);
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("Unexpected status " + (int)response.StatusCode);
        if (response.Version != VersionOf(transport)) throw new InvalidDataException("Negotiated HTTP/" + response.Version);
        await using var body = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        long offset = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (offset + read > expected.Length || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan((int)offset, read)))
                throw new InvalidDataException("Response body mismatch.");
            offset += read;
            if (readDelay > TimeSpan.Zero) await Task.Delay(readDelay, cancellation).ConfigureAwait(false);
        }

        return offset == expected.Length ? offset : throw new InvalidDataException($"Body {offset} bytes, expected {expected.Length}.");
    }

    // One connection with several paced concurrent streams until the phase ends.
    private static async Task MultiplexedAsync(WorkloadStats stats, EnduranceRun run, Pacer pacer, string path, byte[] expected, int uploadBytes)
    {
        using var handler = Handler(run);
        // HttpMessageInvoker avoids HttpClient's response buffering defaults.
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        stats.Connected();
        var upload = uploadBytes > 0 ? Payloads.Get(uploadBytes) : null;
        using var failed = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        await Task.WhenAll(Enumerable.Range(0, stats.Workload.Streams).Select(async _ =>
        {
            var buffer = new byte[65536];
            try
            {
                while (run.Running)
                {
                    await pacer.WaitAsync(failed.Token).ConfigureAwait(false);
                    var started = Stopwatch.GetTimestamp();
                    var bytes = await SendAndValidateAsync(client, run, stats.Workload.Transport, path, upload is null ? null : new ByteArrayContent(upload), expected, buffer, failed.Token).ConfigureAwait(false);
                    stats.Completed(Stopwatch.GetTimestamp() - started, bytes);
                }
            }
            catch
            {
                // One stream failing ends the connection's iteration; siblings stop too.
                await failed.CancelAsync().ConfigureAwait(false);
                throw;
            }
        })).ConfigureAwait(false);
    }

    private static async Task MultiplexedChurnAsync(WorkloadStats stats, EnduranceRun run, Pacer pacer)
    {
        await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        using var handler = Handler(run);
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        stats.Connected();
        var bytes = await SendAndValidateAsync(client, run, stats.Workload.Transport, "/plaintext", null, Payloads.Plaintext, new byte[256], run.Token).ConfigureAwait(false);
        stats.Completed(Stopwatch.GetTimestamp() - started, bytes);
    }

    // Long-lived connection: cancel streams at random points (before headers, mid-body)
    // and confirm a healthy request on the same connection after each cancellation.
    private static async Task MultiplexedCancelAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer)
    {
        using var handler = Handler(run);
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        stats.Connected();
        var buffer = new byte[16384];
        while (run.Running)
        {
            await pacer.WaitAsync(run.Token).ConfigureAwait(false);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
            var early = random.Next(3) == 0;
            if (early) cancel.CancelAfter(random.Next(0, 3));
            var wanted = random.Next(1, 512 * 1024);
            try
            {
                using var request = Message(run, stats.Workload.Transport, random.Next(2) == 0 ? "/bytes/1048576" : "/stream/1048576/16384", null);
                using var response = await client.SendAsync(request, cancel.Token).ConfigureAwait(false);
                await using var body = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
                var read = 0;
                while (read < wanted)
                {
                    var count = await body.ReadAsync(buffer, cancel.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("Response ended before the client cancelled.");
                    read += count;
                }

                // Disposing an unfinished response stream resets the stream.
                stats.Outcome("cancel-mid-body");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested && !run.Token.IsCancellationRequested)
            {
                stats.Outcome("cancel-before-body");
            }

            stats.ClientAborted();
            var started = Stopwatch.GetTimestamp();
            var bytes = await SendAndValidateAsync(client, run, stats.Workload.Transport, "/plaintext", null, Payloads.Plaintext, buffer, run.Token).ConfigureAwait(false);
            stats.Completed(Stopwatch.GetTimestamp() - started, bytes);
        }
    }

    // Abandons whole connections with streams in flight by disposing the handler.
    private static async Task MultiplexedAbruptAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer)
    {
        await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var handler = Handler(run);
        var client = new HttpMessageInvoker(handler, disposeHandler: true);
        stats.Connected();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var inFlight = Enumerable.Range(0, 4).Select(async index =>
        {
            using var request = Message(run, stats.Workload.Transport, index % 2 == 0 ? "/stream/1048576/16384" : "/upload", index % 2 == 0 ? null : new SlowContent(Large, 16384, TimeSpan.FromMilliseconds(5)));
            using var response = await client.SendAsync(request, cancel.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(cancel.Token).ConfigureAwait(false);
            await body.CopyToAsync(Stream.Null, cancel.Token).ConfigureAwait(false);
        }).ToArray();
        await Task.Delay(random.Next(1, 40), run.Token).ConfigureAwait(false);
        client.Dispose();
        await cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException)
        {
            // Expected: the client abandoned these exchanges.
        }

        stats.ClientAborted();
        stats.Outcome("connection-abandoned");
    }

    private static async Task MultiplexedSlowReadAsync(WorkloadStats stats, EnduranceRun run)
    {
        using var handler = Handler(run);
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        stats.Connected();
        var buffer = new byte[16384];
        while (run.Running)
        {
            var started = Stopwatch.GetTimestamp();
            var bytes = await SendAndValidateAsync(client, run, stats.Workload.Transport, "/bytes/1048576", null, Payloads.Get(Large), buffer, run.Token, TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            stats.Completed(Stopwatch.GetTimestamp() - started, bytes);
        }
    }

    private static async Task MultiplexedSlowWriteAsync(WorkloadStats stats, EnduranceRun run)
    {
        using var handler = Handler(run);
        using var client = new HttpMessageInvoker(handler, disposeHandler: false);
        stats.Connected();
        var buffer = new byte[256];
        while (run.Running)
        {
            var started = Stopwatch.GetTimestamp();
            var bytes = await SendAndValidateAsync(client, run, stats.Workload.Transport, "/upload", new SlowContent(SlowBytes, 4096, TimeSpan.FromMilliseconds(25)), Payloads.UploadAcknowledgement(SlowBytes), buffer, run.Token).ConfigureAwait(false);
            stats.Completed(Stopwatch.GetTimestamp() - started, bytes);
        }
    }

    // ---- WebSockets ----

    // Churn: connect, echo a few validated text/binary messages, close. Hold: one
    // connection for the whole phase with paced echoes.
    private static async Task WebSocketAsync(WorkloadStats stats, EnduranceRun run, Random random, Pacer pacer, bool hold)
    {
        if (!hold) await pacer.WaitAsync(run.Token).ConfigureAwait(false);
        var transport = stats.Workload.Transport;
        var started = Stopwatch.GetTimestamp();
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        var scheme = transport is Transport.H1 or Transport.H2c ? "ws" : "wss";
        var port = transport is Transport.H1 or Transport.H2c ? run.PlainPort : run.TlsPort;
        var uri = new Uri($"{scheme}://localhost:{port}/ws");
        if (transport is Transport.H2c or Transport.H2Tls)
        {
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            using var handler = Handler(run);
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            await socket.ConnectAsync(uri, invoker, run.Token).ConfigureAwait(false);
            await ExchangeAsync().ConfigureAwait(false);
        }
        else
        {
            socket.Options.RemoteCertificateValidationCallback = LoadClient.Pinned(run.Thumbprint);
            await socket.ConnectAsync(uri, run.Token).ConfigureAwait(false);
            await ExchangeAsync().ConfigureAwait(false);
        }

        async Task ExchangeAsync()
        {
            stats.Connected();
            var buffer = new byte[(64 * 1024) + 16];
            var messages = hold ? int.MaxValue : random.Next(1, 8);
            for (var message = 0; message < messages && run.Running; message++)
            {
                if (hold) await pacer.WaitAsync(run.Token).ConfigureAwait(false);
                var each = Stopwatch.GetTimestamp();
                var size = random.Next(1, 64 * 1024);
                var text = random.Next(2) == 0;
                var payload = text ? Encoding.UTF8.GetBytes(new string('a', size / 2) + "é中" + new string('z', size / 2)) : Payloads.Get(size);
                await socket.SendAsync(payload, text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, run.Token).ConfigureAwait(false);
                var received = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (received == buffer.Length) throw new InvalidDataException("Echo longer than the message sent.");
                    result = await socket.ReceiveAsync(buffer.AsMemory(received), run.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) throw new InvalidDataException("Server closed during an echo: " + socket.CloseStatus + " " + socket.CloseStatusDescription);
                    received += result.Count;
                }
                while (!result.EndOfMessage);
                if (result.MessageType != (text ? WebSocketMessageType.Text : WebSocketMessageType.Binary) || !buffer.AsSpan(0, received).SequenceEqual(payload))
                    throw new InvalidDataException("WebSocket echo mismatch.");
                if (hold) stats.Completed(Stopwatch.GetTimestamp() - each, received);
            }

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", run.Token).ConfigureAwait(false);
            if (socket.CloseStatus != WebSocketCloseStatus.NormalClosure) throw new InvalidDataException("Unexpected close status " + socket.CloseStatus);
            if (!hold) stats.Completed(Stopwatch.GetTimestamp() - started, 0);
        }
    }
}

// Paces one worker to a fixed rate. Missed slots are skipped rather than replayed
// so a stall is followed by normal load, not a burst.
internal sealed class Pacer(double rate)
{
    private readonly long _interval = rate > 0 ? (long)(Stopwatch.Frequency / rate) : 0;
    private readonly Lock _gate = new();
    private long _next = Stopwatch.GetTimestamp();

    internal async ValueTask WaitAsync(CancellationToken cancellation)
    {
        if (_interval == 0) return;
        long slot;
        long now;
        lock (_gate)
        {
            now = Stopwatch.GetTimestamp();
            if (_next < now - _interval) _next = now;
            slot = _next;
            _next += _interval;
        }

        var wait = slot - now;
        if (wait > 0) await Task.Delay(TimeSpan.FromTicks(wait * TimeSpan.TicksPerSecond / Stopwatch.Frequency), cancellation).ConfigureAwait(false);
    }
}

// A read-only wrapper returning at most a fixed amount per read, with a delay between reads.
internal sealed class ThrottledStream(Stream inner, int maximum, TimeSpan delay, CancellationToken cancellation) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        await Task.Delay(delay, cancellation).ConfigureAwait(false);
        return await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximum)], token).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Request content written in fixed chunks with a delay between them.
internal sealed class SlowContent(int length, int chunk, TimeSpan delay) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var body = Payloads.Get(length);
        for (var offset = 0; offset < length; offset += chunk)
        {
            await stream.WriteAsync(body.AsMemory(offset, Math.Min(chunk, length - offset)), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
