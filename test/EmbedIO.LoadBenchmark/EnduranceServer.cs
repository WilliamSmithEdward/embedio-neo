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
// HTTP/2 by ALPN, HTTP/3 over QUIC), or explicitly TCP-only TLS. Both use the
// comparison handler plus a WebSocket echo endpoint. A background sampler appends resource figures
// to a JSON Lines file; the orchestrator drives drain, restart and forced-GC
// snapshots over stdin/stdout.
internal static class EnduranceServer
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        var profile = EnduranceProfile.Parse(options);
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.Required("--certificate"), options.Required("--certificate-password"), X509KeyStorageFlags.Exportable);
        var plainPort = options.Integer("--plain-port", 0);
        var tlsPort = options.Integer("--tls-port", 0);
        var interval = TimeSpan.FromSeconds(options.Number("--sample-seconds", 5));
        var bodyReadSeconds = options.Number("--request-body-read-timeout", 0);
        TimeSpan? bodyReadTimeout = bodyReadSeconds > 0 ? TimeSpan.FromSeconds(bodyReadSeconds) : null;
        Payloads.Prepare(options.Integer("--reference-bytes", 1 << 20));
        var counters = new EnduranceCounters();
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => counters.Exception(e.Exception);
        EmbedIO.Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Off;

        IEnduranceHost host = profile.Engine switch
        {
            "embedio" => new EnduranceHost(certificate, plainPort, tlsPort, counters, profile, bodyReadTimeout),
            "kestrel" => new KestrelEnduranceHost(certificate, plainPort, tlsPort, counters),
            var other => throw new ArgumentException("Unknown engine " + other),
        };
        try
        {
            var started = await host.StartAsync().ConfigureAwait(false);
            await using var sampler = new EnduranceSampler(options.Required("--sample-file"), interval, counters, host, plainPort, tlsPort, profile);
            Control.Write("READY " + JsonSerializer.Serialize(new
            {
                startMilliseconds = started,
                processId = Environment.ProcessId,
                processorCount = Environment.ProcessorCount,
                serverGc = GCSettings.IsServerGC,
                gcLatencyMode = GCSettings.LatencyMode.ToString(),
                runtime = RuntimeInformation.FrameworkDescription,
                listenerProfile = profile.Name,
                tlsListenerMode = profile.TlsListenerDisplay,
                nativeDiagnostics = profile.TcpOnly ? "not-probed-tcp-only" : "enabled",
                assemblies = AssemblyIdentity.Describe(typeof(WebServer).Assembly),
                // EmbedIO.Quic serves HTTP/3 without MsQuic, so the server must not load it; only
                // the clients' HttpClient does. Asking System.Net.Quic whether QUIC is supported
                // would itself load MsQuic, so this only reports a module already loaded.
                msquic = profile.TcpOnly ? null : LoadedModule("msquic"),
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
        catch
        {
            try { await host.StopAsync().ConfigureAwait(false); }
            catch (Exception cleanup) when (cleanup is not (OutOfMemoryException or AccessViolationException or InsufficientExecutionStackException))
            {
                Console.Error.WriteLine("Endurance host cleanup after failure: " + cleanup);
            }
            throw;
        }
    }

    // Matches the library's name on each platform: msquic.dll, libmsquic.so.2,
    // libmsquic.2.dylib and the like.
    private static object? LoadedModule(string stem)
    {
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            var name = module.ModuleName;
            if (name.StartsWith("lib", StringComparison.Ordinal)) name = name[3..];
            if (!name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)) continue;
            var path = module.FileName;
            return new
            {
                path,
                fileVersion = module.FileVersionInfo.FileVersion,
                sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            };
        }

        return null;
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

internal sealed class EnduranceHost(X509Certificate2 certificate, int plainPort, int tlsPort, EnduranceCounters counters, EnduranceProfile profile, TimeSpan? bodyReadTimeout) : IEnduranceHost
{
    private readonly List<(WebServer Server, Task Running, CancellationTokenSource Stop)> _servers = [];

    public int Generation { get; private set; }

    public string State
    {
        get
        {
            lock (_servers) return _servers.Count == 0 ? "stopped" : string.Join(',', _servers.Select(server => server.Server.State.ToString()));
        }
    }

    public async Task<double> StartAsync()
    {
        if (_servers.Count != 0) throw new InvalidOperationException("Already started.");
        var clock = Stopwatch.StartNew();
        Generation++;
        counters.Generation = Generation;
        Add(new WebServerOptions().WithUrlPrefix($"http://localhost:{plainPort}/").WithMode(HttpListenerMode.EmbedIO));
        Add(new WebServerOptions().WithUrlPrefix($"https://localhost:{tlsPort}/").WithMode(profile.TlsMode).WithCertificate(certificate));
        foreach (var (server, running, _) in _servers)
        {
            while (server.State is not WebServerState.Listening)
            {
                if (running.IsCompleted)
                {
                    await running.ConfigureAwait(false);
                    throw new InvalidOperationException("Server stopped before reaching Listening.");
                }
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
        try
        {
            foreach (var entry in _servers) await entry.Stop.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_servers.Select(async entry =>
            {
                try
                {
                    await entry.Running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (entry.Stop.IsCancellationRequested)
                {
                    // Cancellation is the requested stop.
                }
            })).ConfigureAwait(false);
        }
        finally { Dispose(); }
    }

    private void Add(WebServerOptions options)
    {
        var generation = Generation;
        options.RequestBodyReadTimeout = bodyReadTimeout;
        var server = new WebServer(options.WithoutAutoLoadCertificate().WithoutAutoRegisterCertificate())
            .PreferNoCompressionFor("text/*")
            .PreferNoCompressionFor("application/octet-stream")
            .WithModule(new EnduranceEchoModule(counters, Generation))
            .WithModule(new ActionModule("/", HttpVerbs.Any, context => counters.HandleAsync(context, generation)));
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        lock (_servers) _servers.Add((server, running, stop));
    }

    private void Dispose()
    {
        foreach (var entry in _servers)
        {
            entry.Server.Dispose();
            entry.Stop.Dispose();
        }

        lock (_servers) _servers.Clear();
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
    private readonly bool _nativeDiagnostics;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Lock _gate = new();

    internal EnduranceSampler(string path, TimeSpan interval, EnduranceCounters counters, IEnduranceHost host, int plainPort, int tlsPort, EnduranceProfile profile)
    {
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
        _counters = counters;
        _host = host;
        _plainPort = plainPort;
        _tlsPort = tlsPort;
        _nativeDiagnostics = !profile.TcpOnly;
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
            // Only quiesced snapshots walk the process heaps; periodic samples stay cheap.
            nativeHeaps = forced && _nativeDiagnostics ? NativeHeaps.Summarize() : null,
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

// Allocated versus committed bytes of every Win32 heap in the process (HeapSummary).
// Committed memory the GC does not own but a heap holds as free space is allocator
// retention; growth in allocated bytes is live native state. No contents are read.
internal static class NativeHeaps
{
    internal static object? Summarize()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var count = NativeMethods.GetProcessHeaps(0, null);
        var handles = new IntPtr[count + 16];
        count = NativeMethods.GetProcessHeaps((uint)handles.Length, handles);
        if (count == 0 || count > handles.Length) return null;
        var defaultHeap = NativeMethods.GetProcessHeap();
        var heaps = new List<HeapRow>();
        for (var i = 0; i < count; i++)
        {
            var summary = new NativeMethods.HeapSummaryData { Size = (uint)Marshal.SizeOf<NativeMethods.HeapSummaryData>() };
            if (!NativeMethods.HeapSummary(handles[i], 0, ref summary)) continue;
            heaps.Add(new HeapRow(handles[i] == defaultHeap, (long)summary.Allocated, (long)summary.Committed, (long)summary.Reserved));
        }

        return new
        {
            count = heaps.Count,
            allocatedBytes = heaps.Sum(heap => heap.AllocatedBytes),
            committedBytes = heaps.Sum(heap => heap.CommittedBytes),
            largest = heaps.OrderByDescending(heap => heap.CommittedBytes).Take(6).ToArray(),
            defaultHeapBlocks = BusyBlockSizes(defaultHeap),
        };
    }

    // Busy-block counts by size in one heap, largest totals first. A size whose count grows
    // with traffic across quiesced snapshots fingerprints the retained allocation; block
    // contents are never read. Only quiesced snapshots call this.
    private static object[]? BusyBlockSizes(IntPtr heap)
        => WalkBusyBlocks(heap) is { } sizes
            ? [.. sizes.OrderByDescending(pair => pair.Key * pair.Value).Take(80)
                .Select(pair => (object)new long[] { pair.Key, pair.Value })]
            : null;

    internal static long DefaultHeapBusyBlocks(uint size)
        => WalkBusyBlocks(NativeMethods.GetProcessHeap()) is { } sizes ? sizes.GetValueOrDefault(size) : -1;

    internal static int CheckCounter()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Heap counter check requires Windows.");
        var heap = NativeMethods.GetProcessHeap();
        var allocations = new IntPtr[64];
        // Warm census and allocation stubs before the measured baseline.
        var warm = NativeMethods.HeapAlloc(heap, 0, 16);
        if (warm == IntPtr.Zero) throw new OutOfMemoryException();
        if (!NativeMethods.HeapFree(heap, 0, warm)) throw new InvalidOperationException("Warm allocation was not freed.");
        _ = DefaultHeapBusyBlocks(16);
        var before = DefaultHeapBusyBlocks(16);
        long during;
        try
        {
            for (var index = 0; index < allocations.Length; index++)
            {
                allocations[index] = NativeMethods.HeapAlloc(heap, 0, 16);
                if (allocations[index] == IntPtr.Zero) throw new OutOfMemoryException();
            }
            during = DefaultHeapBusyBlocks(16);
        }
        finally
        {
            var freeFailed = false;
            foreach (var allocation in allocations)
                if (allocation != IntPtr.Zero && !NativeMethods.HeapFree(heap, 0, allocation))
                    freeFailed = true;
            if (freeFailed) throw new InvalidOperationException("Counter-check allocation was not freed.");
        }
        var after = DefaultHeapBusyBlocks(16);
        var passed = before >= 0 && during >= before + allocations.Length && after >= 0 && during - after >= allocations.Length;
        Control.Write(JsonSerializer.Serialize(new { before, during, after, allocations = allocations.Length, passed }));
        return passed ? 0 : 1;
    }

    // The walk holds the heap lock. If a GC suspended this thread meanwhile and the runtime
    // needed the same heap during that GC, the process would deadlock; one quiesced
    // snapshot stalled every managed thread that way. No GC may start during the walk, so
    // it runs inside a no-GC region with a pre-sized table, and is skipped (null) when the
    // region cannot be entered. Another thread allocating past the budget would still end
    // the region, which is why only quiesced snapshots walk.
    private static Dictionary<uint, long>? WalkBusyBlocks(IntPtr heap)
    {
        // No collection growth, comparer initialization or generic method calls
        // while the process heap is locked. Build the dictionary after unlock.
        const int capacity = 16384;
        var keys = new uint[capacity];
        var counts = new long[capacity];
        var overflow = false;
        // Resolve P/Invoke stubs on a private heap before locking the process
        // heap; a first-use loader allocation must not participate in that lock.
        var scratch = NativeMethods.HeapCreate(0, 0, 0);
        if (scratch == IntPtr.Zero) return null;
        try
        {
            if (!NativeMethods.HeapLock(scratch)) return null;
            try { var entry = default(NativeMethods.HeapEntry); _ = NativeMethods.HeapWalk(scratch, ref entry); }
            finally { _ = NativeMethods.HeapUnlock(scratch); }
        }
        finally { _ = NativeMethods.HeapDestroy(scratch); }
        if (!GC.TryStartNoGCRegion(16 * 1024 * 1024)) return null;
        try
        {
            if (!NativeMethods.HeapLock(heap)) return null;
            try
            {
                var entry = default(NativeMethods.HeapEntry);
                while (NativeMethods.HeapWalk(heap, ref entry))
                {
                    if ((entry.Flags & NativeMethods.BusyFlag) == 0) continue;
                    var slot = (int)(unchecked(entry.DataSize * 2654435761u) & (capacity - 1));
                    var start = slot;
                    while (counts[slot] != 0 && keys[slot] != entry.DataSize)
                    {
                        slot = (slot + 1) & (capacity - 1);
                        if (slot == start) { overflow = true; break; }
                    }
                    if (overflow) break;
                    keys[slot] = entry.DataSize;
                    counts[slot]++;
                }
            }
            finally
            {
                _ = NativeMethods.HeapUnlock(heap);
            }
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }

        if (overflow) return null; // An incomplete census is unavailable.
        var sizes = new Dictionary<uint, long>();
        for (var index = 0; index < capacity; index++)
            if (counts[index] != 0) sizes.Add(keys[index], counts[index]);
        return sizes;
    }

    internal sealed record HeapRow(bool IsDefault, long AllocatedBytes, long CommittedBytes, long ReservedBytes);

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr HeapAlloc(IntPtr heap, uint flags, nuint bytes);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapFree(IntPtr heap, uint flags, IntPtr allocation);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr HeapCreate(uint flags, nuint initial, nuint maximum);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapDestroy(IntPtr heap);

        [StructLayout(LayoutKind.Sequential)]
        internal struct HeapSummaryData
        {
            internal uint Size;
            internal nuint Allocated;
            internal nuint Committed;
            internal nuint Reserved;
            internal nuint MaxReserve;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetProcessHeaps(uint numberOfHeaps, IntPtr[]? processHeaps);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetProcessHeap();

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapSummary(IntPtr heap, uint flags, ref HeapSummaryData summary);

        internal const ushort BusyFlag = 0x0004; // PROCESS_HEAP_ENTRY_BUSY

        // PROCESS_HEAP_ENTRY; the trailing union (Block or Region) is not interpreted.
        [StructLayout(LayoutKind.Sequential)]
        internal struct HeapEntry
        {
            internal IntPtr Data;
            internal uint DataSize;
            internal byte Overhead;
            internal byte RegionIndex;
            internal ushort Flags;
            internal IntPtr Union0;
            internal IntPtr Union1;
            internal IntPtr Union2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapLock(IntPtr heap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapUnlock(IntPtr heap);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HeapWalk(IntPtr heap, ref HeapEntry entry);
    }
}
