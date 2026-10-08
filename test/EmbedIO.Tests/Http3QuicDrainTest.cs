using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
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
        [TestCase("idle")]
        [TestCase("idle-deadline")]
        [TestCase("blocked-qpack")]
        [TestCase("complete")]
        [TestCase("timeout")]
        [TestCase("abort")]
        [TestCase("late-lower-id")]
        public async Task GoAwayDrainsAcceptedRequestsAndRejectsLaterArrivals(string scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await Drain(scenario);
        }
        [TestCase("idle")]
        [TestCase("complete")]
        [TestCase("blocked-qpack")]
        public async Task CooperativePeerCloseDoesNotBecomeACriticalStreamFailure(string scenario)
        {
            ArgumentNullException.ThrowIfNull(scenario);
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            for (var iteration = 0; iteration < 5; ++iteration) await Drain(scenario);
        }
        private sealed class DrainingApplication
        {
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Count;
            internal async Task Handle<T>(T value)
            {
                object exchange = value ?? throw new AssertionException("Missing exchange.");
                Interlocked.Increment(ref Count); Entered.TrySetResult();
                try
                {
                    await Release.Task;
                    await ((Task)(exchange.GetType().GetMethod("RespondAsync", Flags)?.Invoke(exchange,
                        new object[] { new byte[] { 100, 111, 110, 101 }, CancellationToken.None }) ?? throw new AssertionException("Missing response.")));
                }
                finally { Completed.TrySetResult(); }
            }
        }
        private static async Task<long> ReadQuicInteger(Stream stream, CancellationToken token)
        {
            var bytes = new byte[8];
            await stream.ReadExactlyAsync(bytes.AsMemory(0, 1), token);
            var length = 1 << (bytes[0] >> 6);
            if (length > 1) await stream.ReadExactlyAsync(bytes.AsMemory(1, length - 1), token);
            long value = bytes[0] & 63;
            for (var i = 1; i < length; ++i) value = (value << 8) | bytes[i];
            return value;
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<QuicStream> ReadControlForDrainAsync(QuicConnection client, List<QuicStream> peers, CancellationToken token)
        {
            QuicStream? control = null;
            // Read both server stream types so later acceptance observes connection closure.
            for (var i = 0; i < 2; ++i)
            {
                var peer = await client.AcceptInboundStreamAsync(token); peers.Add(peer);
                if (await ReadQuicInteger(peer, token) == 0) control = peer;
            }
            var source = control ?? throw new AssertionException("Missing control stream.");
            Assert.That(await ReadQuicInteger(source, token), Is.EqualTo(4));
            var settings = new byte[checked((int)await ReadQuicInteger(source, token))];
            await source.ReadExactlyAsync(settings, token);
            return source;
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Drain(string scenario)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var drain = new CancellationTokenSource();
            var application = new DrainingApplication();
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
            var handler = (typeof(DrainingApplication).GetMethod(nameof(DrainingApplication.Handle), Flags) ?? throw new AssertionException("Missing handler."))
                .MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)), application);
            var sessionReady = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                if (scenario == "blocked-qpack")
                {
                    await using var ownedConnection = accepted.ConfigureAwait(false);
                    var session = Activator.CreateInstance(connection, Flags, null, new object[] { accepted, handler, deadline.Token }, null)
                        ?? throw new AssertionException("Missing owner.");
                    using var owner = (IDisposable)session;
                    (connection.GetField("_drainToken", Flags) ?? throw new AssertionException("Missing drain token.")).SetValue(session, drain.Token);
                    (connection.GetField("_drainTimeout", Flags) ?? throw new AssertionException("Missing drain timeout.")).SetValue(session, TimeSpan.FromSeconds(10));
                    sessionReady.TrySetResult(session);
                    await ((Task)(connection.GetMethod("RunCoreAsync", Flags)?.Invoke(session, null) ?? throw new AssertionException("Missing runner.")));
                }
                else
                    await ((Task)(connection.GetMethod("RunWithDrainAsync", Flags)?.Invoke(null,
                        new object[] { accepted, handler, deadline.Token, drain.Token, TimeSpan.FromSeconds(scenario is "timeout" or "idle-deadline" ? 3 : 10) })
                        ?? throw new AssertionException("Missing graceful runner.")));
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
            var peers = new List<QuicStream>();
            QuicStream? lower = null;
            try
            {
                if (scenario is "idle" or "idle-deadline")
                {
                    var idleControl = await ReadControlForDrainAsync(client, peers, deadline.Token);
                    drain.Cancel();
                    Assert.That(await ReadQuicInteger(idleControl, deadline.Token), Is.EqualTo(7));
                    Assert.That(await ReadQuicInteger(idleControl, deadline.Token), Is.EqualTo(1));
                    Assert.That(await ReadQuicInteger(idleControl, deadline.Token), Is.EqualTo(0));
                    if (scenario == "idle") await client.CloseAsync(0x100, deadline.Token);
                    await server.WaitAsync(TimeSpan.FromSeconds(5));
                    return;
                }
                if (scenario == "late-lower-id" || scenario == "blocked-qpack") lower = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                if (scenario == "blocked-qpack" && lower != null)
                    await lower.WriteAsync(Convert.FromHexString("01110200D1D7C150096C6F63616C686F737480"), false, deadline.Token);
                await using var request = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                await request.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                await application.Entered.Task.WaitAsync(deadline.Token);
                var source = await ReadControlForDrainAsync(client, peers, deadline.Token);
                if (scenario == "blocked-qpack")
                {
                    var session = await sessionReady.Task.WaitAsync(deadline.Token);
                    var gate = connection.GetField("_sync", Flags)?.GetValue(session) ?? throw new AssertionException("Missing gate.");
                    var pending = (System.Collections.IDictionary)(connection.GetField("_pending", Flags)?.GetValue(session) ?? throw new AssertionException("Missing pending fields."));
                    while (true)
                    {
                        lock (gate) { if (pending.Count == 1) break; }
                        await Task.Delay(1, deadline.Token);
                    }
                }
                drain.Cancel();
                Assert.That(await ReadQuicInteger(source, deadline.Token), Is.EqualTo(7));
                Assert.That(await ReadQuicInteger(source, deadline.Token), Is.EqualTo(1));
                Assert.That(await ReadQuicInteger(source, deadline.Token), Is.EqualTo(request.Id + 4));
                Assert.That(server.IsCompleted, Is.False, "An accepted callback still owns the request.");
                await using var late = lower ?? await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                lower = null;
                QuicException? rejection = null;
                try
                {
                    if (scenario != "blocked-qpack") await late.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, deadline.Token);
                    await late.ReadExactlyAsync(new byte[1], deadline.Token);
                }
                catch (QuicException error) { rejection = error; }
                Assert.That(rejection?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                Assert.That(rejection?.ApplicationErrorCode, Is.EqualTo(0x10b));
                Assert.That(application.Count, Is.EqualTo(1));
                if (scenario == "complete" || scenario == "late-lower-id" || scenario == "blocked-qpack")
                {
                    application.Release.TrySetResult();
                    using var response = new MemoryStream();
                    await request.CopyToAsync(response, deadline.Token);
                    var bytes = response.ToArray();
                    Assert.That(bytes.AsSpan(bytes.Length - 4).ToArray(), Is.EqualTo(new byte[] { 100, 111, 110, 101 }));
                    // A cooperative client has consumed GOAWAY and all responses.
                    await client.CloseAsync(0x100, deadline.Token);
                }
                else if (scenario == "abort") deadline.Cancel();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
                if (scenario == "timeout" || scenario == "abort") Assert.That(application.Completed.Task.IsCompleted, Is.False);
                if (scenario is "complete" or "late-lower-id" or "blocked-qpack") return;
                QuicException? closure = null;
                try
                {
                    await using var unexpected = await client.AcceptInboundStreamAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Fail("Unexpected server stream after drain.");
                }
                catch (QuicException error) { closure = error; }
                Assert.That(closure?.ApplicationErrorCode, Is.EqualTo(0x100));
            }
            finally
            {
                deadline.Cancel(); application.Release.TrySetResult();
                if (lower != null) await lower.DisposeAsync();
                foreach (var peer in peers) await peer.DisposeAsync();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
                if (application.Count != 0) await application.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
