using System.Net;
using System.Net.Quic;
using System.Diagnostics;
using System.Net.Security;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Same empty-client/write-or-empty-server workload as the runtime probe. This
// invokes the original binding's internal surface without changing visibility or
// selecting a different production listener. It is not an HTTP/3 soak campaign.
internal static class NativeQuicRetentionProbe
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly byte[] Alpn = Encoding.ASCII.GetBytes("embedio-probe");

    internal static async Task<int> RunAsync(CommandLine options)
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
            throw new PlatformNotSupportedException("The native heap probe requires Windows and QUIC support.");
        var requested = options.Number("--streams", 2000);
        if (requested < 1 || requested > 1_000_000 || requested != Math.Truncate(requested))
            throw new ArgumentOutOfRangeException(nameof(options), "Stream count must be an integer between 1 and 1000000.");
        var count = (int)requested;
        var warmup = options.Integer("--warmup-streams", 500);
        if (warmup < 1 || warmup > 1_000_000) throw new ArgumentOutOfRangeException(nameof(options), "Invalid warm-up stream count.");
        Action<string>? progress = options.Has("--progress")
            ? stage => Console.Error.WriteLine(JsonSerializer.Serialize(new { stage, at = DateTimeOffset.UtcNow })) : null;
        using var generated = Orchestrator.CreateCertificate();
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        _ = await MeasureAsync(certificate, true, warmup, progress);
        var results = new List<object>();
        foreach (var write in new[] { true, false, true, false }) results.Add(await MeasureAsync(certificate, write, count, progress));
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(item => string.Equals(item.ModuleName, "msquic.dll", StringComparison.OrdinalIgnoreCase));
        var nativeLibrary = new
        {
            path = module.FileName,
            version = module.FileVersionInfo.FileVersion,
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName)))
        };
        Control.Write(JsonSerializer.Serialize(new { provider = "embedio-native", runtime = RuntimeInformation.FrameworkDescription, nativeLibrary, streams = count, results }));
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<object> MeasureAsync(X509Certificate2 certificate, bool write, int count, Action<string>? progress)
    {
        progress?.Invoke("before-initial-settle");
        QuicSendBufferProbe.Settle();
        progress?.Invoke("before-initial-heap-census");
        var before = NativeHeaps.DefaultHeapBusyBlocks(16);
        progress?.Invoke("after-initial-heap-census");
        if (before < 0) throw new InvalidOperationException("Native heap census is unavailable before the probe.");
        var closed = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        {
            var apiType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi")
                ?? throw new NotSupportedException("The selected core has no original native binding.");
            using var api = (SafeHandle)Invoke(apiType, null, "Open");
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var configuration = (SafeHandle)Call(registration, "CreateStreamConfiguration", Alpn, (ushort)100, (ushort)0);
            Call(configuration, "LoadServerCertificate", certificate);
            using var listener = (SafeHandle)Call(registration, "CreateListener");
            Call(listener, "EnableAcceptance");
            Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), Alpn);
            var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
            var connecting = QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultCloseErrorCode = 1,
                DefaultStreamErrorCode = 1,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = [new SslApplicationProtocol(Alpn)],
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString() == certificate.GetCertHashString(),
                },
            }, deadline.Token).AsTask();
            var accepting = (Task)Call(listener, "AcceptAsync", deadline.Token);
            await accepting.WaitAsync(deadline.Token);
            using var connection = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                ?? throw new InvalidOperationException("Missing native connection."));
            Call(connection, "EnableStreamAcceptance");
            Call(connection, "Configure", configuration);
            await using var client = await connecting.WaitAsync(deadline.Token);
            var ready = (Task)(connection.GetType().GetProperty("Connected", Hidden)?.GetValue(connection)
                ?? throw new InvalidOperationException("Missing handshake completion."));
            await ready.WaitAsync(deadline.Token);
            progress?.Invoke("connected");
            var buffer = new byte[16];
            try
            {
                for (var index = 0; index < count; index++)
                {
                    await using var outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                    outbound.CompleteWrites();
                    var receiving = (Task)Call(connection, "AcceptStreamAsync", deadline.Token);
                    await receiving.WaitAsync(deadline.Token);
                    using var inbound = (SafeHandle)(receiving.GetType().GetProperty("Result")?.GetValue(receiving)
                        ?? throw new InvalidOperationException("Missing native stream."));
                    try
                    {
                        if (await (ValueTask<int>)Call(inbound, "ReadAsync", buffer.AsMemory(), deadline.Token) != 0)
                            throw new InvalidDataException("Probe client unexpectedly sent request data.");
                        if (write) await (ValueTask)Call(inbound, "WriteAsync", new ReadOnlyMemory<byte>(new byte[] { 42 }), true, deadline.Token);
                        else Call(inbound, "CompleteWrites");
                        var length = 0;
                        int read;
                        while ((read = await outbound.ReadAsync(buffer, deadline.Token)) != 0)
                        {
                            for (var offset = 0; offset < read; offset++)
                                if (buffer[offset] != 42) throw new InvalidDataException("Probe response byte differs.");
                            length += read;
                        }
                        if (length != (write ? 1 : 0)) throw new InvalidDataException("Probe response length differs.");
                    }
                    finally { await (ValueTask)Call(inbound, "DisposeAsync"); }
                    if (!inbound.IsClosed) throw new InvalidOperationException("Native stream ownership did not close.");
                    closed++;
                    if (index % 100 == 0) progress?.Invoke("closed-stream-" + closed);
                }
            }
            finally
            {
                progress?.Invoke("before-connection-shutdown");
                await ((Task)Call(connection, "ShutdownAsync", 1L)).WaitAsync(deadline.Token);
                progress?.Invoke("after-connection-shutdown");
            }
        }
        progress?.Invoke("after-owners-disposed");
        QuicSendBufferProbe.Settle();
        progress?.Invoke("before-final-heap-census");
        var after = NativeHeaps.DefaultHeapBusyBlocks(16);
        progress?.Invoke("after-final-heap-census");
        if (after < 0) throw new InvalidOperationException("Native heap census is unavailable after the probe.");
        return new
        {
            mode = write ? "write" : "empty",
            streams = count,
            busy16Before = before,
            busy16After = after,
            busy16Delta = after - before,
            closedNativeStreams = closed,
            runtimeSendBufferInspection = "not-applicable"
        };
    }

    private static object Call(object target, string name, params object[] args) => Invoke(target.GetType(), target, name, args);
    private static object Invoke(Type type, object? target, string name, params object[] args)
    {
        try
        {
            var method = type.GetMethod(name, Hidden) ?? throw new MissingMethodException(type.FullName, name);
            return method.Invoke(target, args) ?? new object();
        }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
