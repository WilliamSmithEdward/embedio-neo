using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

internal static class QuicRebindProbe
{
    internal static async Task<int?> Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--quic-rebind") return null;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            return await RunSupported(args);
        throw new PlatformNotSupportedException("QUIC probe requires a supported desktop platform.");
    }
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static async Task<int> RunSupported(string[] args)
    {
        var iterations = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2048;
        if (iterations is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(args), "Use 1-4096 listener cycles.");
        if (Environment.Version.ToString() != "10.0.12") throw new InvalidOperationException("The probe requires .NET 10.0.12.");
        var output = Path.GetFullPath(args.Length > 2 ? args[2] : "TestResults/quic-rebind-probe");
        Directory.CreateDirectory(output);
        using var trace = new NativeTrace(Environment.GetEnvironmentVariable("EMBEDIO_QUIC_TRACE_LIBRARY"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = new List<Marker>(iterations * 4 + 8);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        var cycle = 0;
        Exception? failure = null;
        void Mark(int phase)
        {
            events.Add(new Marker(phase, cycle, endpoint.Port, Stopwatch.GetTimestamp()));
            trace.Mark(phase, cycle, endpoint.Port);
        }
        try
        {
            // Check that native tracing sees ordinary UDP bind/close calls;
            // the canary sends no packets and is separate from the QUIC port.
            using (var canary = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                canary.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            if (!QuicListener.IsSupported) throw new PlatformNotSupportedException("QUIC is required for this probe.");
            for (; cycle < iterations; ++cycle)
            {
                Mark(1);
                await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
                {
                    ListenEndPoint = endpoint,
                    ApplicationProtocols = [SslApplicationProtocol.Http3],
                    // No clients or certificates are needed to reproduce
                    // disposal and rebinding of an unconnected listener.
                    ConnectionOptionsCallback = (_, _, _) => throw new InvalidOperationException("Unexpected peer in loopback-only rebind probe."),
                }, deadline.Token);
                endpoint = listener.LocalEndPoint;
                Mark(2);
                Mark(3);
                await listener.DisposeAsync();
                Mark(4);
            }
        }
        catch (Exception error) when (error is SocketException or QuicException or OperationCanceledException or PlatformNotSupportedException)
        {
            failure = error;
            Mark(5);
        }
        // Observe late native cleanup after recording the result. This is
        // never a retry or a delay between listener operations.
        await Task.Delay(100);
        Mark(6);
        var traceComplete = trace.Flush();
        var report = new
        {
            passed = failure == null && traceComplete,
            runtime = Environment.Version.ToString(),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            processId = Environment.ProcessId,
            processPath = Environment.ProcessPath,
            iterations,
            completed = cycle,
            endpoint = endpoint.ToString(),
            trace.Enabled,
            traceComplete,
            error = failure?.ToString(),
            frequency = Stopwatch.Frequency,
            phases = "1=bind begin, 2=bound, 3=dispose begin, 4=disposed, 5=failure, 6=post-result observation complete",
            events,
        };
        await File.WriteAllTextAsync(Path.Combine(output, "managed.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        Console.WriteLine($"QUIC rebind: completed={cycle}/{iterations}; endpoint={endpoint}; nativeTrace={trace.Enabled}; passed={report.passed}");
        if (failure != null) Console.Error.WriteLine(failure);
        return report.passed ? 0 : 1;
    }
    private readonly record struct Marker(int Phase, int Cycle, int Port, long Timestamp);
    private sealed class NativeTrace : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeMarker(int phase, int cycle, int port);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeFlush();
        private readonly IntPtr _library;
        private readonly NativeMarker? _marker;
        private readonly NativeFlush? _flush;
        internal NativeTrace(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _library = NativeLibrary.Load(Path.GetFullPath(path));
            try
            {
                _marker = Marshal.GetDelegateForFunctionPointer<NativeMarker>(NativeLibrary.GetExport(_library, "embedio_quic_trace_marker"));
                _flush = Marshal.GetDelegateForFunctionPointer<NativeFlush>(NativeLibrary.GetExport(_library, "embedio_quic_trace_flush"));
            }
            catch { NativeLibrary.Free(_library); throw; }
        }
        internal bool Enabled => _marker != null;
        internal void Mark(int phase, int cycle, int port) => _marker?.Invoke(phase, cycle, port);
        internal bool Flush() => _flush == null || _flush() == 0;
        public void Dispose() { if (_library != IntPtr.Zero) NativeLibrary.Free(_library); }
    }
}
