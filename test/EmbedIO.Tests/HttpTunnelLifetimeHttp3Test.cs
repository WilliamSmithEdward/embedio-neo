using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using static EmbedIO.Tests.HttpTunnelLifetimeTest;

namespace EmbedIO.Tests
{
    // Lifetime of accepted HTTP/3 extended CONNECT tunnels against a raw QUIC peer.
    // The fixture's QUIC requirement applies; hosts without QUIC report Ignored.
    public partial class Http3ListenerTest
    {
        private const long RequestCancelled = 0x10c;
        private const long MessageError = 0x10e;

        [TestCase(false)]
        [TestCase(true)]
        public async Task TunnelBulkPeerInputAfterSendCompletionIsObservedCompletely(bool capsules)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await BulkInputCore(capsules);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task BulkInputCore(bool capsules)
        {
            var expected = Payload(1 << 20, 13);
            await WithTunnelAsync(capsules, async (tunnel, token) =>
            {
                await tunnel.CompleteOutputAsync(token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                var buffer = new byte[8192];
                if (capsules)
                {
                    var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                    while (await channel.ReadHeaderAsync(token) is { } header)
                    {
                        if (header.Type != 0) { await channel.SkipPayloadAsync(token); continue; }
                        int count;
                        while ((count = await channel.ReadPayloadAsync(buffer, 0, buffer.Length, token)) != 0)
                        { hash.AppendData(buffer, 0, count); total += count; }
                    }
                }
                else
                {
                    int count;
                    while ((count = await tunnel.Stream.ReadAsync(buffer, 0, buffer.Length, token)) != 0)
                    { hash.AppendData(buffer, 0, count); total += count; }
                }
                return total + ":" + Convert.ToHexString(hash.GetHashAndReset());
            }, async (request, data, token) =>
            {
                Assert.That(await data.ReadAsync(new byte[1], token), Is.Zero, "Server send completion must precede peer input.");
                var random = new Random(41);
                var offset = 0;
                while (offset < expected.Length)
                {
                    var size = Math.Min(expected.Length - offset, random.Next(0, 40000));
                    if (capsules)
                    {
                        await data.WriteAsync(Capsule(0x2a, Payload(random.Next(0, 300), 9)), token);
                        await data.WriteAsync(Capsule(0, Array.Empty<byte>()), token);
                        await data.WriteAsync(Capsule(0, expected.AsSpan(offset, size).ToArray()), token);
                    }
                    else await data.WriteAsync(expected.AsMemory(offset, size), token);
                    offset += size;
                }
                request.CompleteWrites();
            }, observed => Assert.That(observed, Is.EqualTo(expected.Length + ":" + Convert.ToHexString(SHA256.HashData(expected)))));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task TunnelPeerInputEndIsDistinguishedFromPeerAbort(bool capsules, bool abort)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await InputEndCore(capsules, abort);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task InputEndCore(bool capsules, bool abort)
        {
            await WithTunnelAsync(capsules, async (tunnel, token) =>
            {
                if (capsules)
                {
                    var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                    Assert.That((await channel.ReadHeaderAsync(token))?.Length, Is.EqualTo(2));
                    await channel.SkipPayloadAsync(token);
                    try { return await channel.ReadHeaderAsync(token) is null ? "end" : "capsule"; }
                    catch (Exception error) when (IsTransportOutcome(error)) { return error.GetType().Name; }
                }
                var bytes = new byte[2];
                await tunnel.Stream.ReadExactlyAsync(bytes, token);
                var outcome = await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, token));
                return outcome == "eof" ? "end" : outcome;
            }, async (request, data, token) =>
            {
                await data.WriteAsync(capsules ? new byte[] { 0x21, 2, 7, 7 } : new byte[] { 7, 7 }, token);
                // Let the application consume the first unit before the input ends.
                await Task.Delay(100, token);
                if (abort) request.Abort(QuicAbortDirection.Write, RequestCancelled);
                else request.CompleteWrites();
            }, observed =>
            {
                TestContext.Out.WriteLine((abort ? "RESET_STREAM" : "FIN") + " observed by the application as: " + observed);
                if (abort) Assert.That(observed, Is.Not.EqualTo("end"), "A peer abort must not be reported as a complete end of input.");
                else Assert.That(observed, Is.EqualTo("end"));
            });
        }

        [Test]
        public async Task TunnelIncompleteOutgoingCapsuleAbortsOnlyItsStream()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await IncompleteCapsuleCore();
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task IncompleteCapsuleCore()
        {
            var headersReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithTunnelAsync(true, async (tunnel, token) =>
            {
                var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                await channel.WriteHeaderAsync(0, 10, token);
                await channel.WritePayloadAsync(new byte[] { 1, 2, 3 }, 0, 3, token);
                // A QUIC reset can discard bytes not yet read by the peer,
                // including response HEADERS. This case tests capsule cleanup
                // after the peer has observed the successful tunnel response.
                await headersReceived.Task.WaitAsync(token);
                return "returned";
            }, async (request, data, token) =>
            {
                var buffer = new byte[64];
                var received = 0;
                var error = await Assert.ThrowsAsync<QuicException>(async () =>
                {
                    int count;
                    while ((count = await data.ReadAsync(buffer, token)) != 0) received += count;
                }) ?? throw new AssertionException("Missing stream abort.");
                TestContext.Out.WriteLine($"Peer outcome: {error.QuicError} 0x{error.ApplicationErrorCode:x}");
                Assert.That(error.ApplicationErrorCode, Is.EqualTo(MessageError));
                Assert.That(received, Is.LessThanOrEqualTo(5));
            }, observed => Assert.That(observed, Is.EqualTo("returned")), onResponseHeaders: () => headersReceived.TrySetResult());
        }

        [Test]
        public async Task TunnelPeerAbortReleasesAFlowControlBlockedWrite()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await BlockedWriteCore();
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task BlockedWriteCore()
        {
            long written = 0;
            await WithTunnelAsync(false, async (tunnel, token) =>
            {
                var chunk = new byte[16384];
                try
                {
                    while (Interlocked.Read(ref written) < 512L * 1024 * 1024)
                    {
                        await tunnel.Stream.WriteAsync(chunk, 0, chunk.Length);
                        await tunnel.Stream.FlushAsync();
                        Interlocked.Add(ref written, chunk.Length);
                    }
                    return "unbounded";
                }
                catch (Exception error) when (IsTransportOutcome(error) || error is QuicException) { return error.GetType().Name; }
            }, async (request, data, token) =>
            {
                var last = -1L;
                var stableSince = DateTime.UtcNow;
                while (true)
                {
                    await Task.Delay(50, token);
                    var current = Interlocked.Read(ref written);
                    if (current != last) { last = current; stableSince = DateTime.UtcNow; continue; }
                    if (current > 0 && DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(400)) break;
                }
                TestContext.Out.WriteLine("Bytes accepted before flow control stalled the writer: " + last);
                Assert.That(last, Is.LessThan(64L * 1024 * 1024), "Flow control must bound unread output.");
                request.Abort(QuicAbortDirection.Both, RequestCancelled);
            }, observed =>
            {
                TestContext.Out.WriteLine("Observed application outcome: " + observed);
                Assert.That(observed, Is.Not.EqualTo("unbounded"));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task TunnelServerStopReleasesAPendingReadAndDrains(bool capsules)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await ServerStopCore(capsules);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ServerStopCore(bool capsules)
        {
            var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithTunnelAsync(capsules, async (tunnel, token) =>
            {
                reading.TrySetResult();
                try
                {
                    if (capsules) return await (tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.")).ReadHeaderAsync(token) is null ? "end" : "capsule";
                    return await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, token));
                }
                catch (Exception error) when (IsTransportOutcome(error)) { return error.GetType().Name; }
            }, async (request, data, token) =>
            {
                await reading.Task.WaitAsync(token);
                await Task.Delay(100, token);
            }, observed =>
            {
                TestContext.Out.WriteLine("Observed application outcome: " + observed);
                Assert.That(observed, Is.Not.EqualTo("end").And.Not.EqualTo("eof"), "Server shutdown is not a peer end of input.");
            }, stopServerAfterPeer: true);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task WithTunnelAsync(bool capsules, Func<HttpTunnel, CancellationToken, Task<string>> application,
            Func<QuicStream, ClientDataStream, CancellationToken, Task> peer, Action<string> verify, bool stopServerAfterPeer = false, Action? onResponseHeaders = null)
        {
            using var certificate = Certificate();
            var prefix = Prefix(); var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/healthy")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }
                    context.OnClose(_ => Interlocked.Increment(ref closes));
                    try
                    {
                        var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing managed tunnel capability.");
                        var tunnel = await capability.AcceptTunnelAsync("example-tunnel", capsules, stop.Token);
                        observed.TrySetResult(await application(tunnel, stopServerAfterPeer ? context.CancellationToken : stop.Token));
                    }
                    catch (Exception error) { observed.TrySetException(error); throw; }
                });
            using var serverStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var running = server.RunAsync(serverStop.Token);
            await using var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = RequestCancelled,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, RemoteCertificateValidationCallback = (_, peerCertificate, _, _) => peerCertificate?.GetCertHashString() == certificate.GetCertHashString() }
            }, stop.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                await ReadConnectSetting(connection, peerStreams, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                var fields = new List<(string, string)>
                { (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/tunnel"), (":protocol", "example-tunnel") };
                if (capsules) fields.Add(("capsule-protocol", "?1"));
                await SendFieldSection(request, fields.ToArray(), false, stop.Token);
                var headers = await ReadResponseFields(request, stop.Token);
                Assert.That(headers[":status"], Is.EqualTo("200"));
                Assert.That(headers["content-length"], Is.Null);
                onResponseHeaders?.Invoke();
                using var data = new ClientDataStream(request);
                await peer(request, data, stop.Token);
                if (stopServerAfterPeer)
                {
                    // Graceful drain: stopping the server must release the pending
                    // tunnel operation, run its close callback once and finish RunAsync.
                    serverStop.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
                    verify(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token));
                    Assert.That(Volatile.Read(ref closes), Is.EqualTo(1));
                    return;
                }
                verify(await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), stop.Token));
                await HealthySibling(connection, uri.Authority, stop.Token);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (Volatile.Read(ref closes) == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, stop.Token);
                await Task.Delay(100, stop.Token);
                Assert.That(Volatile.Read(ref closes), Is.EqualTo(1));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                foreach (var stream in peerStreams) await stream.DisposeAsync();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
