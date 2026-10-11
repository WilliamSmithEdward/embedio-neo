using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

// Isolates the per-stream native allocation found by the #278 endurance attribution,
// without HTTP. One process hosts a loopback QUIC listener and client; the client opens
// streams and the server answers each either with one byte ("write") or with an empty,
// completing write ("empty"). After every stream and both connections are disposed, it
// counts live 16-byte blocks in the default process heap and, for "write" streams, reads
// the private send-buffer state that System.Net.Quic should have freed on disposal.
// Windows only (the heap walk); test-only, outside the solution, never packed.
internal static class QuicSendBufferProbe
{
    private static readonly SslApplicationProtocol Protocol = new("embedio-probe");
    private sealed record Measurement(string Mode, int Streams, long Busy16Before, long Busy16After,
        long Busy16Delta, int? DisposedStreamsWithUnfreedSendBuffer, bool SendBufferInspectionSupported);

    internal static async Task<int> RunAsync(CommandLine options)
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
        {
            Control.Write("{\"skipped\":\"requires Windows with QUIC support\"}");
            return 0;
        }

        var requestedStreams = options.Number("--streams", 2000);
        if (requestedStreams < 1 || requestedStreams > 1_000_000 || requestedStreams != Math.Truncate(requestedStreams))
            throw new ArgumentOutOfRangeException(nameof(options), "Stream count must be an integer between 1 and 1000000.");
        var streams = (int)requestedStreams;
        // Schannel cannot use an ephemeral key, so reload the certificate from PKCS#12.
        using var generated = Orchestrator.CreateCertificate();
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        var results = new List<Measurement>();
        _ = await MeasureAsync(certificate, write: true, 500).ConfigureAwait(false); // Warm-up.
        foreach (var write in new[] { true, false, true, false })
            results.Add(await MeasureAsync(certificate, write, streams).ConfigureAwait(false));

        Control.Write(JsonSerializer.Serialize(new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            msquic = typeof(QuicListener).Assembly.GetType("System.Net.Quic.MsQuicApi")?
                .GetProperty("MsQuicLibraryVersion", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null),
            streams,
            results,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        return results.TrueForAll(result => result.SendBufferInspectionSupported) ? 0 : 1;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<Measurement> MeasureAsync(X509Certificate2 certificate, bool write, int count)
    {
        Settle();
        var before = NativeHeaps.DefaultHeapBusyBlocks(16);
        if (before < 0) throw new InvalidOperationException("Native heap census is unavailable before the probe.");
        var retainedArrays = 0;
        var inspectionSupported = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [Protocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                MaxInboundBidirectionalStreams = 100,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ApplicationProtocols = [Protocol],
                    ServerCertificate = certificate,
                },
            }),
        }, deadline.Token).ConfigureAwait(false);
        QuicConnection? client = null;
        QuicConnection? server = null;
        try
        {
            var accept = listener.AcceptConnectionAsync(deadline.Token).AsTask();
            client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [Protocol],
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString(),
                },
            }, deadline.Token).ConfigureAwait(false);
            server = await accept.ConfigureAwait(false);
            var buffer = new byte[16];

            for (var i = 0; i < count; i++)
            {
                await using (var outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token).ConfigureAwait(false))
                {
                    outbound.CompleteWrites(); // The client never sends data.
                    await using var inbound = await server.AcceptInboundStreamAsync(deadline.Token).ConfigureAwait(false);
                    while (await inbound.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) > 0)
                    {
                        throw new InvalidDataException("Probe client unexpectedly sent request data.");
                    }

                    if (write) await inbound.WriteAsync(new byte[] { 42 }, completeWrites: true, deadline.Token).ConfigureAwait(false);
                    else inbound.CompleteWrites();
                    var received = 0;
                    int read;
                    while ((read = await outbound.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                    {
                        for (var index = 0; index < read; index++)
                            if (buffer[index] != 42) throw new InvalidDataException("Probe response byte differs.");
                        received += read;
                    }

                    if (received != (write ? 1 : 0)) throw new InvalidDataException("Probe response length differs.");
                    await inbound.DisposeAsync().ConfigureAwait(false);
                    var buffers = SendBufferCount(inbound);
                    if (buffers < 0) inspectionSupported = false;
                    else if (buffers > 0) retainedArrays++;
                }
            }

        }
        finally
        {
            try
            {
                try { if (client != null) await client.DisposeAsync().ConfigureAwait(false); }
                finally { if (server != null) await server.DisposeAsync().ConfigureAwait(false); }
            }
            finally { await listener.DisposeAsync().ConfigureAwait(false); }
        }

        Settle();
        var after = NativeHeaps.DefaultHeapBusyBlocks(16);
        if (after < 0) throw new InvalidOperationException("Native heap census is unavailable after the probe.");
        return new Measurement(write ? "write" : "empty", count, before, after, after - before,
            inspectionSupported ? retainedArrays : null, inspectionSupported);
    }

    // QuicStream._sendBuffers (a MsQuicBuffers struct)._count, read after disposal.
    // MsQuicBuffers.FreeNativeMemory clears _count together with the _buffers pointer,
    // so a nonzero count means the QUIC_BUFFER array was never freed.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static int SendBufferCount(QuicStream stream)
    {
        var field = typeof(QuicStream).GetField("_sendBuffers", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = field?.GetValue(stream);
        return value?.GetType().GetField("_count", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) as int? ?? -1;
    }

    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Thread.Sleep(500);
    }
}
