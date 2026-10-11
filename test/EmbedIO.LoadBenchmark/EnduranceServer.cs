using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// Long-lived endurance server. One process hosts a cleartext managed listener
// (HTTP/1.1 and HTTP/2 prior knowledge) and a combined TLS listener (HTTP/1.1 and
// HTTP/2 by ALPN, HTTP/3 over QUIC) with the same handler as the comparison runs
// plus a WebSocket echo endpoint. A background sampler appends resource figures
// to a JSON Lines file; the orchestrator drives drain, restart and forced-GC
// snapshots over stdin/stdout.
internal static class EnduranceServer
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.Required("--certificate"), options.Required("--certificate-password"), X509KeyStorageFlags.Exportable);
        var plainPort = options.Integer("--plain-port", 0);
        var tlsPort = options.Integer("--tls-port", 0);
        var interval = TimeSpan.FromSeconds(options.Number("--sample-seconds", 5));
        Payloads.Prepare(options.Integer("--reference-bytes", 1 << 20));
        var counters = new EnduranceCounters();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => counters.Exception(e.Exception);
        EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Off;

        IEnduranceHost host = options.Text("--engine", "embedio") switch
        {
            "embedio" => new EnduranceHost(certificate, plainPort, tlsPort, counters),
            "kestrel" => new KestrelEnduranceHost(certificate, plainPort, tlsPort, counters),
            var other => throw new ArgumentException("Unknown engine " + other),
        };
        var started = await host.StartAsync().ConfigureAwait(false);
        await using var sampler = new EnduranceSampler(options.Required("--sample-file"), interval, counters, host, plainPort, tlsPort);
        Control.Write("READY " + JsonSerializer.Serialize(new
        {
            startMilliseconds = started,
            processId = Environment.ProcessId,
            processorCount = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            gcLatencyMode = GCSettings.LatencyMode.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            quicSupported = System.Net.Quic.QuicListener.IsSupported,
            assemblies = AssemblyIdentity.Describe(typeof(WebServer).Assembly, typeof(System.Net.Quic.QuicListener).Assembly),
        }));

        while (true)
        {
            var command = await Console.In.ReadLineAsync().ConfigureAwait(false);
            var parts = command?.Split(' ', 2) ?? ["exit"];
            switch (parts[0])
            {
                case "sample":
                    Control.Write(JsonSerializer.Serialize(sampler.Capture()));
                    break;
                case "quiesce":
                    // Forced, compacting collections first so the figures are retained state.
                    for (var pass = 0; pass < 2; pass++)
                    {
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                        GC.WaitForPendingFinalizers();
                    }

                    Control.Write(JsonSerializer.Serialize(sampler.Capture(forced: true)));
                    break;
                case "drain":
                    var timeout = TimeSpan.FromSeconds(double.Parse(parts[1], CultureInfo.InvariantCulture));
                    Control.Write(JsonSerializer.Serialize(await host.DrainAsync(timeout).ConfigureAwait(false)));
                    break;
                case "start":
                    Control.Write(JsonSerializer.Serialize(new { startMilliseconds = await host.StartAsync().ConfigureAwait(false) }));
                    break;
                case "exit":
                    var stopping = Stopwatch.StartNew();
                    await host.StopAsync().ConfigureAwait(false);
                    Control.Write(JsonSerializer.Serialize(new { stopMilliseconds = stopping.Elapsed.TotalMilliseconds, final = sampler.Capture() }));
                    return 0;
                default:
                    throw new InvalidOperationException("Unknown endurance server command: " + command);
            }
        }
    }
}

// Process-wide counters the handler, the WebSocket module and the sampler share.
internal sealed class EnduranceCounters
{
    private readonly ConcurrentDictionary<string, long> _exceptions = new(StringComparer.Ordinal);
    private long _inFlight;
    private long _requests;
    private long _handlerFailures;
    private long _webSocketsActive;
    private long _webSocketsOpened;
    private long _webSocketMessages;
    private long _staleRequests;
    private int _generation;

    internal long InFlight => Interlocked.Read(ref _inFlight);

    internal long Requests => Interlocked.Read(ref _requests);

    internal long HandlerFailures => Interlocked.Read(ref _handlerFailures);

    internal long WebSocketsActive => Interlocked.Read(ref _webSocketsActive);

    internal long WebSocketsOpened => Interlocked.Read(ref _webSocketsOpened);

    internal long WebSocketMessages => Interlocked.Read(ref _webSocketMessages);

    // Requests or WebSocket messages handled by a server instance that had already been
    // drained and replaced. Any nonzero value means a disposed server kept serving.
    internal long StaleRequests => Interlocked.Read(ref _staleRequests);

    internal int Generation
    {
        get => Volatile.Read(ref _generation);
        set => Volatile.Write(ref _generation, value);
    }

    internal void CheckGeneration(int generation)
    {
        if (generation != Generation) Interlocked.Increment(ref _staleRequests);
    }

    internal Dictionary<string, long> Exceptions => new(_exceptions, StringComparer.Ordinal);

    // Diagnostic opt-in, as in comparison runs: key by message too and log the first
    // stack per key (bounded). Leave unset for measured runs.
    private static readonly bool Detail = Environment.GetEnvironmentVariable("EMBEDIO_BENCH_EXCEPTION_DETAIL") == "1";

    internal void Exception(Exception exception)
    {
        var key = exception.GetType().FullName ?? "?";
        if (Detail)
        {
            key += ": " + exception.Message;
            if (!_exceptions.ContainsKey(key) && _exceptions.Count < 256)
                Console.Error.WriteLine("FIRST " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + key + Environment.NewLine + exception.StackTrace);
        }

        _exceptions.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    internal async Task TrackAsync(Func<Task> handler)
    {
        Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _requests);
        try
        {
            await handler().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _handlerFailures);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    internal async Task HandleAsync(IHttpContext context, int generation)
    {
        CheckGeneration(generation);
        Interlocked.Increment(ref _inFlight);
        Interlocked.Increment(ref _requests);
        try
        {
            await EmbedIOServer.HandleAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not HttpException)
        {
            // Cancelled or aborted exchanges surface here; the engine still owns cleanup.
            Interlocked.Increment(ref _handlerFailures);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    internal void WebSocketOpened()
    {
        Interlocked.Increment(ref _webSocketsActive);
        Interlocked.Increment(ref _webSocketsOpened);
    }

    internal void WebSocketClosed() => Interlocked.Decrement(ref _webSocketsActive);

    internal void WebSocketMessage() => Interlocked.Increment(ref _webSocketMessages);
}

// Echoes every complete text and binary message on the connection it arrived on.
internal sealed class EnduranceEchoModule : WebSocketMessageModule
{
    private readonly EnduranceCounters _counters;
    private readonly int _generation;

    internal EnduranceEchoModule(EnduranceCounters counters, int generation)
        : base("/ws", enableConnectionWatchdog: true)
    {
        _counters = counters;
        _generation = generation;
        MaxMessageSize = 1 << 20;
    }

    protected override Task OnClientConnectedAsync(IWebSocketContext context)
    {
        _counters.WebSocketOpened();
        return Task.CompletedTask;
    }

    protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
    {
        _counters.WebSocketClosed();
        return Task.CompletedTask;
    }

    protected override Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
    {
        _counters.WebSocketMessage();
        _counters.CheckGeneration(_generation);
        return SendAsync(context, text);
    }

    protected override Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data)
    {
        _counters.WebSocketMessage();
        _counters.CheckGeneration(_generation);
        return SendAsync(context, data);
    }
}

// Owns the two WebServer instances. Restart disposes them and creates fresh ones
// on the same ports, as an application replacing its server would.
internal interface IEnduranceHost
{
    int Generation { get; }

    string State { get; }

    Task<double> StartAsync();

    Task<object> DrainAsync(TimeSpan timeout);

    Task StopAsync();
}

internal sealed class EnduranceHost(X509Certificate2 certificate, int plainPort, int tlsPort, EnduranceCounters counters) : IEnduranceHost
{
    private readonly List<(WebServer Server, Task Running, CancellationTokenSource Stop)> _servers = [];

    public int Generation { get; private set; }

    public string State => _servers.Count == 0 ? "stopped" : string.Join(',', _servers.Select(server => server.Server.State.ToString()));

    public async Task<double> StartAsync()
    {
        if (_servers.Count != 0) throw new InvalidOperationException("Already started.");
        var clock = Stopwatch.StartNew();
        Generation++;
        counters.Generation = Generation;
        Add(new WebServerOptions().WithUrlPrefix($"http://localhost:{plainPort}/").WithMode(HttpListenerMode.EmbedIO));
        Add(new WebServerOptions().WithUrlPrefix($"https://localhost:{tlsPort}/").WithMode(HttpListenerMode.EmbedIOCombined).WithCertificate(certificate));
        foreach (var (server, running, _) in _servers)
        {
            while (server.State is not WebServerState.Listening)
            {
                if (running.IsCompleted) await running.ConfigureAwait(false);
                await Task.Delay(10).ConfigureAwait(false);
            }
        }

        return clock.Elapsed.TotalMilliseconds;
    }

    // Graceful drain of both listeners concurrently, then disposal. Reports how long
    // each phase took and whether anything failed, without hiding exceptions.
    public async Task<object> DrainAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        var inFlightAtStart = counters.InFlight;
        var drains = _servers.Select(async entry =>
        {
            var own = Stopwatch.StartNew();
            string? error = null;
            try
            {
                await entry.Server.DrainAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                error = exception.GetType().FullName + ": " + exception.Message;
            }

            var drained = own.Elapsed.TotalMilliseconds;
            string? runError = null;
            try
            {
                await entry.Running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                runError = exception.GetType().FullName + ": " + exception.Message;
            }

            return new { listener = entry.Server.Listener.Name, drainMilliseconds = drained, runCompletedMilliseconds = own.Elapsed.TotalMilliseconds, state = entry.Server.State.ToString(), error, runError };
        }).ToArray();
        var results = await Task.WhenAll(drains).ConfigureAwait(false);
        var drainedAt = clock.Elapsed.TotalMilliseconds;
        var inFlightAfter = counters.InFlight;
        Dispose();
        return new { timeoutSeconds = timeout.TotalSeconds, inFlightAtStart, inFlightAfterDrain = inFlightAfter, totalMilliseconds = drainedAt, disposedMilliseconds = clock.Elapsed.TotalMilliseconds, listeners = results };
    }

    public async Task StopAsync()
    {
        foreach (var entry in _servers) await entry.Stop.CancelAsync().ConfigureAwait(false);
        foreach (var entry in _servers)
        {
            try
            {
                await entry.Running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the requested stop.
            }
        }

        Dispose();
    }

    private void Add(WebServerOptions options)
    {
        var generation = Generation;
        var server = new WebServer(options.WithoutAutoLoadCertificate().WithoutAutoRegisterCertificate())
            .PreferNoCompressionFor("text/*")
            .PreferNoCompressionFor("application/octet-stream")
            .WithModule(new EnduranceEchoModule(counters, Generation))
            .WithModule(new ActionModule("/", HttpVerbs.Any, context => counters.HandleAsync(context, generation)));
        var stop = new CancellationTokenSource();
        _servers.Add((server, server.RunAsync(stop.Token), stop));
    }

    private void Dispose()
    {
        foreach (var entry in _servers)
        {
            entry.Server.Dispose();
            entry.Stop.Dispose();
        }

        _servers.Clear();
    }
}

// Control host: ASP.NET Core Kestrel from the shared framework with the same handler,
// cleartext HTTP/1.1 on the plain port and HTTP/1.1, HTTP/2 and HTTP/3 on the TLS port.
// It has no h2c prior knowledge or WebSocket endpoint, so plans for it must exclude those.
// Used only to tell engine retention apart from runtime, TLS or MsQuic retention.
internal sealed class KestrelEnduranceHost(X509Certificate2 certificate, int plainPort, int tlsPort, EnduranceCounters counters) : IEnduranceHost
{
    private WebApplication? _application;

    public int Generation { get; private set; }

    public string State => _application is null ? "stopped" : "running";

    public async Task<double> StartAsync()
    {
        var clock = Stopwatch.StartNew();
        Generation++;
        counters.Generation = Generation;
        var generation = Generation;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.ListenLocalhost(plainPort, listen => listen.Protocols = HttpProtocols.Http1);
            options.ListenLocalhost(tlsPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                listen.UseHttps(certificate);
            });
        });
        builder.WebHost.UseQuic();
        _application = builder.Build();
        _application.Run(async context =>
        {
            counters.CheckGeneration(generation);
            await counters.TrackAsync(() => KestrelServer.HandleAsync(context)).ConfigureAwait(false);
        });
        await _application.StartAsync().ConfigureAwait(false);
        return clock.Elapsed.TotalMilliseconds;
    }

    public async Task<object> DrainAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        var inFlightAtStart = counters.InFlight;
        string? error = null;
        using (var deadline = new CancellationTokenSource(timeout))
        {
            try
            {
                await (_application?.StopAsync(deadline.Token) ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                error = exception.GetType().FullName + ": " + exception.Message;
            }
        }

        var drained = clock.Elapsed.TotalMilliseconds;
        var inFlightAfter = counters.InFlight;
        await StopAsync().ConfigureAwait(false);
        return new
        {
            timeoutSeconds = timeout.TotalSeconds,
            inFlightAtStart,
            inFlightAfterDrain = inFlightAfter,
            totalMilliseconds = drained,
            disposedMilliseconds = clock.Elapsed.TotalMilliseconds,
            listeners = new[] { new { listener = "Kestrel", drainMilliseconds = drained, runCompletedMilliseconds = drained, state = "Stopped", error, runError = (string?)null } },
        };
    }

    public async Task StopAsync()
    {
        if (_application is null) return;
        await _application.DisposeAsync().ConfigureAwait(false);
        _application = null;
    }
}

// Periodic, non-forcing resource samples written as JSON Lines. Values are the raw
// cumulative counters; trends are computed afterwards from the file.
internal sealed class EnduranceSampler : IAsyncDisposable
{
    private readonly StreamWriter _file;
    private readonly EnduranceCounters _counters;
    private readonly IEnduranceHost _host;
    private readonly int _plainPort;
    private readonly int _tlsPort;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Lock _gate = new();

    internal EnduranceSampler(string path, TimeSpan interval, EnduranceCounters counters, IEnduranceHost host, int plainPort, int tlsPort)
    {
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
        _counters = counters;
        _host = host;
        _plainPort = plainPort;
        _tlsPort = tlsPort;
        _loop = LoopAsync(interval, _stop.Token);
    }

    internal object Capture(bool forced = false)
    {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var sockets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var connection in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
        {
            var port = connection.LocalEndPoint.Port;
            if (port != _plainPort && port != _tlsPort) continue;
            var key = (port == _plainPort ? "plain-" : "tls-") + connection.State;
            sockets[key] = sockets.GetValueOrDefault(key) + 1;
        }

        return new
        {
            seconds = _clock.Elapsed.TotalSeconds,
            utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            forced,
            generation = _host.Generation,
            state = _host.State,
            cpuSeconds = process.TotalProcessorTime.TotalSeconds,
            userCpuSeconds = process.UserProcessorTime.TotalSeconds,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            managedHeapBytes = GC.GetTotalMemory(false),
            gcHeapSizeBytes = info.HeapSizeBytes,
            gcCommittedBytes = info.TotalCommittedBytes,
            gcFragmentedBytes = info.FragmentedBytes,
            gcPromotedBytes = info.PromotedBytes,
            gcPinnedObjects = info.PinnedObjectsCount,
            gcGenerationSizesAfterBytes = info.GenerationInfo.ToArray().Select(generation => generation.SizeAfterBytes).ToArray(),
            gcPauseTimePercentage = info.PauseTimePercentage,
            gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
            gcPauseMilliseconds = GC.GetTotalPauseDuration().TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(false),
            handles = process.HandleCount,
            threads = process.Threads.Count,
            threadPoolThreads = ThreadPool.ThreadCount,
            threadPoolPending = ThreadPool.PendingWorkItemCount,
            threadPoolCompleted = ThreadPool.CompletedWorkItemCount,
            lockContentions = Monitor.LockContentionCount,
            timers = Timer.ActiveCount,
            inFlightRequests = _counters.InFlight,
            requests = _counters.Requests,
            handlerFailures = _counters.HandlerFailures,
            webSocketsActive = _counters.WebSocketsActive,
            webSocketsOpened = _counters.WebSocketsOpened,
            webSocketMessages = _counters.WebSocketMessages,
            staleGenerationRequests = _counters.StaleRequests,
            firstChanceExceptions = _counters.Exceptions,
            serverTcp = sockets,
            availablePhysicalBytes = GuestMemory.AvailablePhysicalBytes(),
            machineBusyCpuSeconds = SystemCpu.BusyTime().TotalSeconds,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        await _file.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                var line = JsonSerializer.Serialize(Capture());
                lock (_gate) _file.WriteLine(line);
            }
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Sampling ends with the process.
        }
    }
}

// Available physical memory of the (possibly dynamically sized) host, so a working-set
// change caused by memory pressure is not mistaken for server retention.
internal static class GuestMemory
{
    internal static long AvailablePhysicalBytes()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var status = new NativeMethods.MemoryStatus { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatus>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : -1;
    }

    internal static long TotalPhysicalBytes()
    {
        if (!OperatingSystem.IsWindows()) return -1;
        var status = new NativeMethods.MemoryStatus { Length = (uint)Marshal.SizeOf<NativeMethods.MemoryStatus>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : -1;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct MemoryStatus
        {
            internal uint Length;
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}
