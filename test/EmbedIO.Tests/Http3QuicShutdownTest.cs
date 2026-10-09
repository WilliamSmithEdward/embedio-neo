using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [TestCase(false, false, false)]
        [TestCase(false, true, false)]
        [TestCase(true, false, false)]
        [TestCase(false, false, true)]
        public async Task ShutdownDoesNotWaitForAnUncooperativeApplication(bool synchronous, bool fault, bool pendingOutput)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await Shutdown(synchronous, fault, pendingOutput);
        }
        [Test]
        public async Task ResetRequestsCannotAccumulateUnlimitedApplicationCallbacks()
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await Shutdown(false, false, false, true);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task PeerClosureCompletesWhenAnApplicationCancellationCallbackThrows(bool criticalFault)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await Shutdown(false, false, false, cancellationFault: true, criticalFault: criticalFault);
        }
        private sealed class StalledApplication
        {
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int CancellationFaultCount;
            internal bool CancellationFault;
            internal bool Synchronous;
            internal bool Fault;
            internal bool PendingOutput;
            internal bool OutputReleasedSafely;
            internal int EnteredCount;
            internal bool LateReadRejected;
            internal bool LateWriteRejected;
            internal async Task Handle<T>(T value)
            {
                object exchange = value ?? throw new AssertionException("Missing exchange.");
                var token = (CancellationToken)(exchange.GetType().GetProperty("CancellationToken", Flags)?.GetValue(exchange)
                    ?? throw new AssertionException("Missing exchange cancellation."));
                using var cancellation = CancellationFault
                    ? token.Register(() =>
                    {
                        Interlocked.Increment(ref CancellationFaultCount);
                        throw new InvalidOperationException("Deliberate application cancellation callback fault.");
                    })
                    : default;
                var output = Array.Empty<Task>();
                if (PendingOutput)
                {
                    output = new[] { Respond(exchange, new byte[8 * 1024 * 1024]), Respond(exchange, Array.Empty<byte>()) };
                    var gate = exchange.GetType().GetField("_outputLifetime", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing output lifetime.");
                    var users = exchange.GetType().GetField("_outputUsers", Flags) ?? throw new AssertionException("Missing output count.");
                    using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    while (true)
                    {
                        lock (gate) { if ((int)(users.GetValue(exchange) ?? 0) >= 2) break; }
                        await Task.Delay(1, ready.Token);
                    }
                }
                Interlocked.Increment(ref EnteredCount);
                Entered.TrySetResult();
                try
                {
                    if (Synchronous) Release.Task.GetAwaiter().GetResult();
                    else await Release.Task;
                    var body = (Stream)(exchange.GetType().GetProperty("InputStream", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing body."));
                    try { _ = await body.ReadAsync(new byte[1]); }
                    catch (ObjectDisposedException) { LateReadRejected = true; }
                    try
                    {
                        await ((Task)(exchange.GetType().GetMethod("RespondAsync", Flags)?.Invoke(exchange,
                            new object[] { Array.Empty<byte>(), CancellationToken.None }) ?? throw new AssertionException("Missing response.")));
                    }
                    catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    { LateWriteRejected = true; }
                    if (PendingOutput)
                    {
                        try { await Task.WhenAll(output).WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or ObjectDisposedException) { }
                        OutputReleasedSafely = Array.TrueForAll(output, task => task.IsCompleted &&
                            (task.Exception == null || !task.Exception.ToString().Contains("SemaphoreSlim", StringComparison.Ordinal)));
                    }
                    if (Fault) throw new InvalidOperationException("Deliberate late application fault.");
                }
                finally { Completed.TrySetResult(); }
            }
            private static Task Respond(object exchange, byte[] bytes) =>
                (Task)(exchange.GetType().GetMethod("RespondAsync", Flags)?.Invoke(exchange,
                    new object[] { bytes, CancellationToken.None }) ?? throw new AssertionException("Missing response."));
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Shutdown(bool synchronous, bool fault, bool pendingOutput, bool exhaust = false, bool cancellationFault = false, bool criticalFault = false)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var application = new StalledApplication { Synchronous = synchronous, Fault = fault, PendingOutput = pendingOutput, CancellationFault = cancellationFault };
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            Assert.That(cert.Export(X509ContentType.Pkcs12).Length, Is.GreaterThan(0), "The OpenSSL QUIC backend must be able to export its synthetic TLS key.");
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 32,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = cert }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchange = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var connection = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var handler = (typeof(StalledApplication).GetMethod(nameof(StalledApplication.Handle), Flags) ?? throw new AssertionException("Missing handler."))
                .MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)), application);
            var sessionReady = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                await using var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                var session = Activator.CreateInstance(connection, Flags, null, new object[] { accepted, handler, deadline.Token }, null)
                    ?? throw new AssertionException("Missing connection owner.");
                using var owner = (IDisposable)session;
                sessionReady.SetResult(session);
                try { await ((Task)(connection.GetMethod("RunCoreAsync", Flags)?.Invoke(session, null) ?? throw new AssertionException("Missing runner."))); }
                catch (Exception error) when (criticalFault && error.GetType().Name == "Http3ProtocolException")
                {
                    Assert.That(error.GetType().GetProperty("ErrorCode", Flags)?.GetValue(error), Is.EqualTo(0x104));
                }
            });
            await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString()
                }
            }, deadline.Token);
            try
            {
                if (exhaust)
                {
                    var session = await sessionReady.Task.WaitAsync(deadline.Token);
                    var gate = connection.GetField("_sync", Flags)?.GetValue(session) ?? throw new AssertionException("Missing gate.");
                    var workers = (System.Collections.IDictionary)(connection.GetField("_workers", Flags)?.GetValue(session) ?? throw new AssertionException("Missing workers."));
                    for (var i = 0; i < 256; ++i)
                    {
                        await using var stalled = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                        await stalled.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                        while (Volatile.Read(ref application.EnteredCount) != i + 1) await Task.Delay(1, deadline.Token);
                        stalled.Abort(QuicAbortDirection.Both, 0x10c);
                        while (true)
                        {
                            lock (gate) { if (!workers.Contains(stalled.Id)) break; }
                            await Task.Delay(1, deadline.Token);
                        }
                    }
                    await using var rejected = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                    await rejected.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                    QuicException? limit = null;
                    try { await rejected.ReadExactlyAsync(new byte[1], deadline.Token); }
                    catch (QuicException error) { limit = error; }
                    Assert.That(limit?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                    Assert.That(limit?.ApplicationErrorCode, Is.EqualTo(0x107));
                    Assert.That(application.EnteredCount, Is.EqualTo(256));
                }
                else
                {
                    await using var request = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                    await request.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                    await application.Entered.Task.WaitAsync(deadline.Token);
                    if (cancellationFault)
                    {
                        if (criticalFault)
                        {
                            await using var control = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
                            // Valid empty SETTINGS followed by forbidden critical-stream FIN.
                            await control.WriteAsync(new byte[] { 0, 4, 0 }, true, deadline.Token);
                            await server.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                        else
                        {
                            await client.CloseAsync(0x100, deadline.Token);
                            await server.WaitAsync(TimeSpan.FromSeconds(5));
                        }
                    }
                }
                deadline.Cancel();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancellationFault) Assert.That(application.CancellationFaultCount, Is.EqualTo(1));
                Assert.That(application.Completed.Task.IsCompleted, Is.False, "The test callback must still be blocked when transport shutdown finishes.");
                application.Release.TrySetResult();
                await application.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var finishedSession = await sessionReady.Task;
                var count = connection.GetField("_applicationCount", Flags) ?? throw new AssertionException("Missing callback count.");
                using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while ((int)(count.GetValue(finishedSession) ?? -1) != 0) await Task.Delay(1, completionDeadline.Token);
                Assert.That(application.LateReadRejected, Is.True);
                Assert.That(application.LateWriteRejected, Is.True);
                if (pendingOutput) Assert.That(application.OutputReleasedSafely, Is.True, "In-flight and queued writes must terminate without disposing an in-use semaphore.");
            }
            finally
            {
                deadline.Cancel();
                application.Release.TrySetResult();
                try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
    }
}
