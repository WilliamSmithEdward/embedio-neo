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

    internal static async Task<int> RunAsync(CommandLine options)
    {
        if (!OperatingSystem.IsWindows() || !QuicListener.IsSupported)
        {
            Control.Write("{\"skipped\":\"requires Windows with QUIC support\"}");
            return 0;
        }

        var streams = (int)options.Number("--streams", 2000);
        // Schannel cannot use an ephemeral key, so reload the certificate from PKCS#12.
        using var generated = Orchestrator.CreateCertificate();
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        var results = new List<object>();
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
        }));
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<object> MeasureAsync(X509Certificate2 certificate, bool write, int count)
    {
        Settle();
        var before = NativeHeaps.DefaultHeapBusyBlocks(16);
        var retainedArrays = 0;

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
        }).ConfigureAwait(false);
        try
        {
            var accept = listener.AcceptConnectionAsync().AsTask();
            var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultStreamErrorCode = 1,
                DefaultCloseErrorCode = 1,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    ApplicationProtocols = [Protocol],
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                },
            }).ConfigureAwait(false);
            var server = await accept.ConfigureAwait(false);
            var buffer = new byte[16];

            for (var i = 0; i < count; i++)
            {
                await using (var outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false))
                {
                    outbound.CompleteWrites(); // The client never sends data.
                    var inbound = await server.AcceptInboundStreamAsync().ConfigureAwait(false);
                    while (await inbound.ReadAsync(buffer).ConfigureAwait(false) > 0)
                    {
                        // Drain the empty request.
                    }

                    if (write) await inbound.WriteAsync(new byte[] { 42 }, completeWrites: true).ConfigureAwait(false);
                    else inbound.CompleteWrites();
                    while (await outbound.ReadAsync(buffer).ConfigureAwait(false) > 0)
                    {
                        // Read the response to its end.
                    }

                    await inbound.DisposeAsync().ConfigureAwait(false);
                    if (SendBufferCount(inbound) > 0) retainedArrays++;
                }
            }

            await client.DisposeAsync().ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        Settle();
        var after = NativeHeaps.DefaultHeapBusyBlocks(16);
        return new
        {
            mode = write ? "write" : "empty",
            streams = count,
            busy16Before = before,
            busy16After = after,
            busy16Delta = after - before,
            disposedStreamsWithUnfreedSendBuffer = retainedArrays,
        };
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
