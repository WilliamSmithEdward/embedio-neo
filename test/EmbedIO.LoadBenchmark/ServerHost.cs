using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

// Server child process. The orchestrator drives it over stdin/stdout so that the
// measurement window matches the client's and resource snapshots follow an idle period.
internal static class ServerHost
{
    internal static async Task<int> RunAsync(CommandLine options)
    {
        var protocol = Enum.Parse<Protocol>(options.Required("--protocol"), ignoreCase: true);
        var tls = options.Has("--tls") || protocol == Protocol.Http3;
        using var certificate = tls
            ? X509CertificateLoader.LoadPkcs12FromFile(options.Required("--certificate"), options.Required("--certificate-password"), X509KeyStorageFlags.Exportable)
            : null;
        var settings = new ServerSettings(options.Required("--engine"), protocol, tls, options.Integer("--port", 0), certificate);
        Payloads.Prepare(options.Integer("--reference-bytes", 1 << 20));
        var exceptions = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        // Diagnostic opt-in: key by message too and log the first stack per key (bounded).
        var detail = Environment.GetEnvironmentVariable("EMBEDIO_BENCH_EXCEPTION_DETAIL") == "1";
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var key = e.Exception.GetType().FullName ?? "?";
            if (detail)
            {
                key += ": " + e.Exception.Message;
                if (!exceptions.ContainsKey(key) && exceptions.Count < 64) Console.Error.WriteLine("FIRST " + key + Environment.NewLine + e.Exception.StackTrace);
            }
            exceptions.AddOrUpdate(key, 1, static (_, count) => count + 1);
        };
        using var profile = options.Has("--profile") ? new RuntimeEventProfile() : null;
        await using IBenchmarkServer server = settings.Engine switch
        {
            "kestrel" => new KestrelServer(settings),
            "embedio" => new EmbedIOServer(settings),
            _ => throw new ArgumentException("Unknown engine " + settings.Engine),
        };
        var started = Stopwatch.StartNew();
        await server.StartAsync().ConfigureAwait(false);
        Control.Write("READY " + JsonSerializer.Serialize(new
        {
            engine = settings.Engine,
            protocol = settings.Protocol.ToString(),
            tls,
            startMilliseconds = started.Elapsed.TotalMilliseconds,
            processId = Environment.ProcessId,
            processorCount = Environment.ProcessorCount,
            serverGc = GCSettings.IsServerGC,
            gcLatencyMode = GCSettings.LatencyMode.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            assemblies = AssemblyIdentity.Describe(typeof(EmbedIO.WebServer).Assembly, typeof(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions).Assembly, typeof(System.Net.Quic.QuicListener).Assembly),
        }));

        Measurement? measurement = null;
        while (true)
        {
            var command = await Console.In.ReadLineAsync().ConfigureAwait(false);
            switch (command)
            {
                case "snapshot":
                    Control.Write(JsonSerializer.Serialize(ResourceSnapshot.Capture()));
                    break;
                case "start":
                    profile?.Begin();
                    measurement?.Dispose();
                    measurement = new Measurement(exceptions);
                    Control.Write("MEASURING");
                    break;
                case "stop":
                    var result = await (measurement ?? throw new InvalidOperationException("stop without start")).FinishAsync().ConfigureAwait(false);
                    Control.Write(JsonSerializer.Serialize(new { window = result, profile = profile?.End() }));
                    break;
                case "exit":
                case null:
                    measurement?.Dispose();
                    var stopping = Stopwatch.StartNew();
                    await server.StopAsync().ConfigureAwait(false);
                    Control.Write(JsonSerializer.Serialize(new { stopMilliseconds = stopping.Elapsed.TotalMilliseconds }));
                    return 0;
                default:
                    throw new InvalidOperationException("Unknown server command: " + command);
            }
        }
    }
}

internal static class AssemblyIdentity
{
    internal static object[] Describe(params Assembly[] assemblies) => assemblies.Select(assembly => (object)new
    {
        name = assembly.GetName().Name,
        version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        path = assembly.Location,
        sha256 = string.IsNullOrEmpty(assembly.Location) ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
    }).ToArray();
}

internal static class ResourceSnapshot
{
    // Collects first so the figures describe retained state, not garbage awaiting collection.
    internal static object Capture()
    {
        for (var pass = 0; pass < 2; pass++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        return new
        {
            managedHeapBytes = GC.GetTotalMemory(false),
            gcCommittedBytes = info.TotalCommittedBytes,
            workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64,
            handles = process.HandleCount,
            threads = process.Threads.Count,
            threadPoolThreads = ThreadPool.ThreadCount,
        };
    }
}

internal sealed class Measurement : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _cpu;
    private readonly TimeSpan _userCpu;
    private readonly long _allocated;
    private readonly int[] _collections;
    private readonly TimeSpan _pause;
    private readonly long _contention;
    private readonly long _workItems;
    private readonly ConcurrentDictionary<string, long> _exceptions;
    private readonly Dictionary<string, long> _exceptionsBefore;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _sampler;
    private long _peakWorkingSet;
    private long _peakPrivate;
    private int _peakThreads;
    private int _peakPoolThreads;

    internal Measurement(ConcurrentDictionary<string, long> exceptions)
    {
        _exceptions = exceptions;
        _exceptionsBefore = new Dictionary<string, long>(exceptions, StringComparer.Ordinal);
        _cpu = _process.TotalProcessorTime;
        _userCpu = _process.UserProcessorTime;
        _allocated = GC.GetTotalAllocatedBytes(true);
        _collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        _pause = GC.GetTotalPauseDuration();
        _contention = Monitor.LockContentionCount;
        _workItems = ThreadPool.CompletedWorkItemCount;
        _sampler = SampleAsync(_stop.Token);
    }

    internal async Task<object> FinishAsync()
    {
        var elapsed = _clock.Elapsed;
        _process.Refresh();
        var cpu = _process.TotalProcessorTime - _cpu;
        var user = _process.UserProcessorTime - _userCpu;
        var allocated = GC.GetTotalAllocatedBytes(true) - _allocated;
        await _stop.CancelAsync().ConfigureAwait(false);
        await _sampler.ConfigureAwait(false);

        var thrown = _exceptions
            .Select(pair => (pair.Key, Count: pair.Value - _exceptionsBefore.GetValueOrDefault(pair.Key)))
            .Where(pair => pair.Count > 0)
            .ToDictionary(pair => pair.Key, pair => pair.Count, StringComparer.Ordinal);

        return new
        {
            elapsedSeconds = elapsed.TotalSeconds,
            cpuSeconds = cpu.TotalSeconds,
            userCpuSeconds = user.TotalSeconds,
            allocatedBytes = allocated,
            collections = new[] { GC.CollectionCount(0) - _collections[0], GC.CollectionCount(1) - _collections[1], GC.CollectionCount(2) - _collections[2] },
            gcPauseMilliseconds = (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds,
            lockContentions = Monitor.LockContentionCount - _contention,
            threadPoolWorkItems = ThreadPool.CompletedWorkItemCount - _workItems,
            peakWorkingSetBytes = _peakWorkingSet,
            peakPrivateBytes = _peakPrivate,
            peakThreads = _peakThreads,
            peakThreadPoolThreads = _peakPoolThreads,
            firstChanceExceptions = thrown,
        };
    }

    public void Dispose()
    {
        _stop.Dispose();
        _process.Dispose();
    }

    private async Task SampleAsync(CancellationToken cancellation)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            do
            {
                _process.Refresh();
                _peakWorkingSet = Math.Max(_peakWorkingSet, _process.WorkingSet64);
                _peakPrivate = Math.Max(_peakPrivate, _process.PrivateMemorySize64);
                _peakThreads = Math.Max(_peakThreads, _process.Threads.Count);
                _peakPoolThreads = Math.Max(_peakPoolThreads, ThreadPool.ThreadCount);
            }
            while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The measurement window ended.
        }
    }
}

// Opt-in runtime event aggregation for profiling passes. It perturbs timing, so
// measured comparison runs leave it off and profiling runs are reported separately.
internal sealed class RuntimeEventProfile : EventListener
{
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const EventKeywords ContentionKeyword = (EventKeywords)0x4000;
    private const EventKeywords ExceptionKeyword = (EventKeywords)0x8000;

    private readonly ConcurrentDictionary<string, long> _allocations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _exceptions = new(StringComparer.Ordinal);
    private long _contentions;
    private double _contentionNanoseconds;
    private volatile bool _active;

    internal void Begin()
    {
        _allocations.Clear();
        _exceptions.Clear();
        _contentions = 0;
        _contentionNanoseconds = 0;
        _active = true;
    }

    internal object End()
    {
        _active = false;
        return new
        {
            note = "AllocationTick events sample roughly every 100 KB per heap; amounts are sampled totals, not exact.",
            // Keep every sampled type: a per-request object of a few dozen bytes sits far
            // below the largest thirty types yet is exactly what a before/after diff needs.
            sampledAllocationBytesByType = _allocations.OrderByDescending(pair => pair.Value).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            exceptionsByType = _exceptions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            contentions = Interlocked.Read(ref _contentions),
            contentionMilliseconds = _contentionNanoseconds / 1_000_000,
        };
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(eventSource, EventLevel.Verbose, GcKeyword | ContentionKeyword | ExceptionKeyword);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_active || eventData.Payload is null || eventData.PayloadNames is null) return;
        switch (eventData.EventName)
        {
            case "GCAllocationTick_V4":
            case "GCAllocationTick_V3":
                var type = Field(eventData, "TypeName") as string ?? "?";
                var amount = Convert.ToInt64(Field(eventData, "AllocationAmount64") ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                _allocations.AddOrUpdate(type, amount, (_, total) => total + amount);
                break;
            case "ContentionStop_V1":
                Interlocked.Increment(ref _contentions);
                var duration = Convert.ToDouble(Field(eventData, "DurationNs") ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
                lock (_exceptions) _contentionNanoseconds += duration;
                break;
            case "ExceptionThrown_V1":
                var name = Field(eventData, "ExceptionType") as string ?? "?";
                _exceptions.AddOrUpdate(name, 1, static (_, count) => count + 1);
                break;
        }
    }

    private static object? Field(EventWrittenEventArgs eventData, string name)
    {
        var index = eventData.PayloadNames is { } names ? names.IndexOf(name) : -1;
        return index >= 0 && eventData.Payload is { } payload ? payload[index] : null;
    }
}
