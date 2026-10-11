using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;
using static EmbedIO.Tests.HttpTunnelLifetimeTest;

namespace EmbedIO.Tests
{
    // Lifetime of accepted HTTP/2 extended CONNECT tunnels over a real cleartext
    // connection, driven by the .NET HttpClient as an independent peer. Every case
    // shares one client connection so sibling requests prove stream-scoped failure.
    [TestFixture]
    public class HttpTunnelLifetimeHttp2Test
    {
        private const string Protocol = "example-tunnel";
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        [TestCase(false)]
        [TestCase(true)]
        public async Task BulkPeerInputAfterSendCompletionIsObservedCompletely(bool capsules)
        {
            var expected = Payload(1 << 20, 11);
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                await tunnel.CompleteOutputAsync(token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                var buffer = new byte[8192];
                if (capsules)
                {
                    var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                    // The peer cannot half-close an HttpClient tunnel, so the
                    // application stops after the agreed number of payload bytes.
                    while (total < expected.Length && await channel.ReadHeaderAsync(token) is { } header)
                    {
                        if (header.Type != 0) { await channel.SkipPayloadAsync(token); continue; }
                        int count;
                        while ((count = await channel.ReadPayloadAsync(buffer, 0, buffer.Length, token)) != 0)
                        { hash.AppendData(buffer, 0, count); total += count; }
                    }
                }
                else
                {
                    while (total < expected.Length)
                    {
                        var count = await tunnel.Stream.ReadAsync(buffer, 0, buffer.Length, token);
                        if (count == 0) break;
                        hash.AppendData(buffer, 0, count); total += count;
                    }
                }
                probe.Observed.TrySetResult(total + ":" + Convert.ToHexString(hash.GetHashAndReset()));
            }, capsules);
            using var response = await host.ConnectAsync(capsules);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            Assert.That(await stream.ReadAsync(new byte[1], host.Token), Is.Zero, "Server send completion must precede peer input.");
            var random = new Random(37);
            var offset = 0;
            while (offset < expected.Length)
            {
                var size = Math.Min(expected.Length - offset, random.Next(0, 40000));
                if (capsules)
                {
                    await stream.WriteAsync(Capsule(0x2a, Payload(random.Next(0, 300), 5)), host.Token);
                    await stream.WriteAsync(Capsule(0, Array.Empty<byte>()), host.Token);
                    await stream.WriteAsync(Capsule(0, expected.AsSpan(offset, size).ToArray()), host.Token);
                }
                else await stream.WriteAsync(expected.AsMemory(offset, size), host.Token);
                await stream.FlushAsync(host.Token);
                offset += size;
            }
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            Assert.That(observed, Is.EqualTo(expected.Length + ":" + Convert.ToHexString(SHA256.HashData(expected))));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task PeerStreamDisposalReleasesAPendingReadAndKeepsTheConnectionUsable()
        {
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                var outcome = await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16));
                probe.Observed.TrySetResult(outcome);
            }, false);
            var response = await host.ConnectAsync(false);
            var stream = await response.Content.ReadAsStreamAsync(host.Token);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            // HttpClient decides which frame ends a disposed tunnel; HttpTunnelPeerAbortTest
            // pins the explicit RST_STREAM versus END_STREAM distinction with a raw peer.
            stream.Dispose();
            response.Dispose();
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed application outcome: " + observed);
            Assert.That(observed, Is.AnyOf("eof", nameof(IOException), nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(ObjectDisposedException)), observed);
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task PeerStreamDisposalReleasesAFlowControlBlockedWrite()
        {
            long written = 0;
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                var chunk = new byte[16384];
                try
                {
                    while (Interlocked.Read(ref written) < 512L * 1024 * 1024)
                    {
                        await tunnel.Stream.WriteAsync(chunk, 0, chunk.Length);
                        await tunnel.Stream.FlushAsync();
                        Interlocked.Add(ref written, chunk.Length);
                    }
                    probe.Observed.TrySetResult("unbounded");
                }
                catch (Exception error) when (IsTransportOutcome(error)) { probe.Observed.TrySetResult(error.GetType().Name); }
            }, false);
            var response = await host.ConnectAsync(false);
            var stream = await response.Content.ReadAsStreamAsync(host.Token);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            await WaitForStallAsync(() => Interlocked.Read(ref written), host.Token);
            var stalled = Interlocked.Read(ref written);
            TestContext.Out.WriteLine("Bytes accepted before flow control stalled the writer: " + stalled);
            Assert.That(stalled, Is.LessThan(64L * 1024 * 1024), "Flow control must bound unread output.");
            stream.Dispose();
            response.Dispose();
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed application outcome: " + observed);
            Assert.That(observed, Is.AnyOf(nameof(IOException), nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(ObjectDisposedException)), observed);
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task IncompleteOutgoingCapsuleResetsOnlyItsStream()
        {
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                await channel.WriteHeaderAsync(0, 10, token);
                await channel.WritePayloadAsync(new byte[] { 1, 2, 3 }, 0, 3, token);
                probe.Observed.TrySetResult("returned");
                // Returning leaves seven declared payload bytes unwritten.
            }, true);
            using var response = await host.ConnectAsync(true);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            var received = new MemoryStream();
            var failure = await ReadUntilEndOrFailureAsync(stream, received, host.Token);
            TestContext.Out.WriteLine("Peer outcome: " + failure);
            Assert.That(failure, Is.Not.EqualTo("eof"), "An incomplete capsule must not end with a successful END_STREAM.");
            Assert.That(received.Length, Is.LessThanOrEqualTo(5));
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.EqualTo("returned"));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task HandlerFailureReleasesAPendingReadAndResetsOnlyItsStream()
        {
            Task<string>? pending = null;
            await using var host = new Http2Host((tunnel, probe, token) =>
            {
                pending = Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16));
                probe.Accepted.TrySetResult();
                throw new InvalidOperationException("Injected handler failure with a read in flight.");
            }, false, expectHandlerFailure: true);
            using var response = await host.ConnectAsync(false);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            var outcome = await (pending ?? throw new AssertionException("Missing pending read.")).WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed pending read outcome: " + outcome);
            Assert.That(outcome, Is.AnyOf("eof", nameof(IOException), nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(ObjectDisposedException)), outcome);
            var peer = await ReadUntilEndOrFailureAsync(stream, new MemoryStream(), host.Token);
            TestContext.Out.WriteLine("Peer outcome: " + peer);
            Assert.That(peer, Is.Not.EqualTo("eof"), "An application failure must not finish the tunnel successfully.");
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task ServerStopReleasesAPendingReadAndDrainsTheListener()
        {
            var host = new Http2Host(async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                probe.Observed.TrySetResult(await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, token)));
            }, false, useContextToken: true);
            try
            {
                using var response = await host.ConnectAsync(false);
                using var stream = await response.Content.ReadAsStreamAsync(host.Token);
                await host.Probe.Accepted.Task.WaitAsync(host.Token);
                host.StopServer();
                await host.Running.WaitAsync(Settle);
                var observed = await host.Probe.Observed.Task.WaitAsync(Settle);
                TestContext.Out.WriteLine("Observed application outcome: " + observed);
                Assert.That(observed, Is.AnyOf("eof", nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(IOException), nameof(ObjectDisposedException)), observed);
                Assert.That(await host.Probe.Closes.Task.WaitAsync(Settle), Is.EqualTo(1));
            }
            finally { await host.DisposeAsync(); }
        }

        [Test]
        public async Task CanceledPendingReadLeavesOutputCompletionUsable()
        {
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                using var cancel = new CancellationTokenSource();
                var read = Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, cancel.Token));
                await Task.Delay(100, token);
                Assert.That(read.IsCompleted, Is.False, "The read must still be waiting for peer input.");
                cancel.Cancel();
                var outcome = await read.WaitAsync(Settle, token);
                TestContext.Out.WriteLine("Observed pending read outcome: " + outcome);
                Assert.That(outcome, Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException)), outcome);
                await tunnel.Stream.WriteAsync(new byte[] { 5 }, 0, 1, token);
                await tunnel.CompleteOutputAsync(token);
                probe.Observed.TrySetResult("completed");
            }, false);
            using var response = await host.ConnectAsync(false);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            var received = new MemoryStream();
            Assert.That(await ReadUntilEndOrFailureAsync(stream, received, host.Token), Is.EqualTo("eof"));
            Assert.That(received.ToArray(), Is.EqualTo(new byte[] { 5 }));
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.EqualTo("completed"));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task FlowControlBlockedTunnelDoesNotStallSiblingStreams()
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                using var writerStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                long written = 0;
                var writer = BlockedWriter(tunnel, () => Interlocked.Read(ref written), count => Interlocked.Add(ref written, count), writerStop.Token);
                await WaitForStallAsync(() => Interlocked.Read(ref written), token);
                parked.TrySetResult();
                await release.Task.WaitAsync(token);
                writerStop.Cancel();
                probe.Observed.TrySetResult(await Settled(writer));
            }, false);
            using var response = await host.ConnectAsync(false);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            await parked.Task.WaitAsync(Settle, host.Token);
            // Each sibling response exceeds the default stream window several times.
            for (var i = 0; i < 4; i++)
            {
                var body = await host.GetLargeAsync();
                Assert.That(body, Is.EqualTo(Http2Host.LargeBody));
            }
            release.TrySetResult();
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException)));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        // The guide requires pending writes to settle before output completion, so the
        // supported sequence cancels the parked writer and then closes.
        [Test]
        public async Task CancelingAFlowControlBlockedWriterThenClosingSettles()
        {
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                using var writerStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                long written = 0;
                var writer = BlockedWriter(tunnel, () => Interlocked.Read(ref written), count => Interlocked.Add(ref written, count), writerStop.Token);
                await WaitForStallAsync(() => Interlocked.Read(ref written), token);
                writerStop.Cancel();
                var writeOutcome = await Settled(writer);
                var closeOutcome = await Settled(Report(tunnel.CloseAsync()));
                probe.Observed.TrySetResult(writeOutcome + "/" + closeOutcome);
            }, false);
            using var response = await host.ConnectAsync(false);
            using var stream = await response.Content.ReadAsStreamAsync(host.Token);
            var observed = await host.Probe.Observed.Task.WaitAsync(TimeSpan.FromSeconds(30), host.Token);
            TestContext.Out.WriteLine("Write/close outcome: " + observed);
            var parts = observed.Split('/');
            Assert.That(parts[0], Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException)), observed);
            Assert.That(parts[1], Is.Not.EqualTo("pending"), "Close must settle once no write is pending.");
            Assert.That(await ReadUntilEndOrFailureAsync(stream, Stream.Null, host.Token), Is.Not.Null);
            await host.AssertClosedOnceAndHealthyAsync();
        }

        private static async Task<string> BlockedWriter(HttpTunnel tunnel, Func<long> written, Action<long> advance, CancellationToken token)
        {
            await Task.Yield();
            var chunk = new byte[16384];
            try
            {
                while (written() < 512L * 1024 * 1024)
                {
                    await tunnel.Stream.WriteAsync(chunk, 0, chunk.Length, token);
                    await tunnel.Stream.FlushAsync(token);
                    advance(chunk.Length);
                }
                return "unbounded";
            }
            catch (Exception error) when (IsTransportOutcome(error)) { return error.GetType().Name; }
        }

        private static async Task<string> Report(Task operation)
        {
            try { await operation.ConfigureAwait(false); return "completed"; }
            catch (Exception error) when (IsTransportOutcome(error)) { return error.GetType().Name; }
        }

        // Records "pending" instead of throwing when an operation does not settle.
        private static async Task<string> Settled(Task<string> operation)
            => await Task.WhenAny(operation, Task.Delay(Settle)) == operation ? await operation : "pending";

        [Test]
        public async Task RepeatedTunnelsOnOneConnectionDoNotRetainTunnelObjects()
        {
            const int Rounds = 64;
            var references = new List<WeakReference>();
            await using var host = new Http2Host(async (tunnel, probe, token) =>
            {
                lock (references) { references.Add(new WeakReference(tunnel)); references.Add(new WeakReference(tunnel.Stream)); }
                var bytes = new byte[4];
                await tunnel.Stream.ReadExactlyAsync(bytes, token);
                await tunnel.Stream.WriteAsync(bytes, token);
                await tunnel.CompleteOutputAsync(token);
            }, false);
            for (var i = 0; i < Rounds; i++)
            {
                using var response = await host.ConnectAsync(false);
                using var stream = await response.Content.ReadAsStreamAsync(host.Token);
                var bytes = BitConverter.GetBytes(i);
                await stream.WriteAsync(bytes, host.Token);
                await stream.FlushAsync(host.Token);
                var received = new MemoryStream();
                Assert.That(await ReadUntilEndOrFailureAsync(stream, received, host.Token), Is.EqualTo("eof"));
                Assert.That(received.ToArray(), Is.EqualTo(bytes));
            }
            var deadline = DateTime.UtcNow + Settle;
            while (Volatile.Read(ref host.Probe.CloseCount) < Rounds && DateTime.UtcNow < deadline) await Task.Delay(20, host.Token);
            Assert.That(Volatile.Read(ref host.Probe.CloseCount), Is.EqualTo(Rounds));
            var alive = int.MaxValue;
            for (var attempt = 0; attempt < 20 && alive != 0; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                lock (references) alive = references.FindAll(reference => reference.IsAlive).Count;
                if (alive != 0) await Task.Delay(50, host.Token);
            }
            Assert.That(references.Count, Is.EqualTo(Rounds * 2));
            Assert.That(alive, Is.Zero, "Closed tunnels on a live connection must become collectable.");
            Assert.That(await host.GetHealthyAsync(), Is.EqualTo("healthy"));
        }

        private static async Task<string> ReadUntilEndOrFailureAsync(Stream stream, Stream received, CancellationToken token)
        {
            var buffer = new byte[4096];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) != 0) received.Write(buffer, 0, count);
                return "eof";
            }
            catch (HttpProtocolException error) { return "HttpProtocolException:" + error.ErrorCode; }
            catch (IOException error) { return error.GetType().Name + ":" + (error.InnerException as HttpProtocolException)?.ErrorCode; }
        }

        private static async Task WaitForStallAsync(Func<long> written, CancellationToken token)
        {
            var last = -1L;
            var stableSince = DateTime.UtcNow;
            while (true)
            {
                await Task.Delay(50, token);
                var current = written();
                if (current != last) { last = current; stableSince = DateTime.UtcNow; continue; }
                if (current > 0 && DateTime.UtcNow - stableSince > TimeSpan.FromMilliseconds(400)) return;
            }
        }

        private sealed class Http2Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
            private readonly CancellationTokenSource _server = new();
            private readonly CancellationTokenSource _linked;
            private readonly WebServer _webServer;
            private readonly SocketsHttpHandler _handler = new() { UseProxy = false, MaxConnectionsPerServer = 1 };
            private readonly HttpClient _client;
            internal Http2Host(Func<HttpTunnel, Probe, CancellationToken, Task> body, bool capsules,
                bool expectHandlerFailure = false, bool useContextToken = false)
            {
                Url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
                _client = new HttpClient(_handler, false) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
                _webServer = new WebServer(o => o.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO))
                    .WithAction("/", HttpVerbs.Any, async context =>
                    {
                        if (context.Request.Url.AbsolutePath == "/large")
                        {
                            Interlocked.Exchange(ref HealthyPort, context.RemoteEndPoint.Port);
                            await context.Response.OutputStream.WriteAsync(LargeBody, context.CancellationToken);
                            return;
                        }
                        if (context.Request.Url.AbsolutePath == "/healthy")
                        {
                            Interlocked.Exchange(ref HealthyPort, context.RemoteEndPoint.Port);
                            await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                            return;
                        }
                        Interlocked.Exchange(ref TunnelPort, context.RemoteEndPoint.Port);
                        context.OnClose(_ => Probe.Closes.TrySetResult(Interlocked.Increment(ref Probe.CloseCount)));
                        var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing managed tunnel capability.");
                        var token = useContextToken ? context.CancellationToken : Token;
                        var tunnel = await capability.AcceptTunnelAsync(Protocol, capsules, token);
                        try { await body(tunnel, Probe, token); }
                        catch (Exception error) when (!expectHandlerFailure)
                        {
                            Probe.Failure.TrySetResult(error);
                            throw;
                        }
                    });
                _linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _server.Token);
                Running = _webServer.RunAsync(_linked.Token);
            }
            internal string Url { get; }
            internal Probe Probe { get; } = new();
            internal int HealthyPort;
            internal int TunnelPort;
            internal Task Running { get; }
            internal CancellationToken Token => _stop.Token;
            internal void StopServer() => _server.Cancel();
            internal async Task<HttpResponseMessage> ConnectAsync(bool capsules)
            {
                using var request = new HttpRequestMessage(HttpMethod.Connect, Url + "tunnel")
                { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                request.Headers.Protocol = Protocol;
                if (capsules) request.Headers.TryAddWithoutValidation("capsule-protocol", "?1");
                var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(response.Content.Headers.ContentLength, Is.Null);
                return response;
            }
            internal static readonly byte[] LargeBody = Payload(262144, 17);
            internal async Task<byte[]> GetLargeAsync()
            {
                var body = await _client.GetByteArrayAsync(Url + "large", Token);
                Assert.That(Volatile.Read(ref HealthyPort), Is.EqualTo(Volatile.Read(ref TunnelPort)), "The sibling must share the tunnel connection.");
                return body;
            }
            internal async Task<string> GetHealthyAsync()
            {
                var body = await _client.GetStringAsync(Url + "healthy", Token);
                Assert.That(Volatile.Read(ref HealthyPort), Is.EqualTo(Volatile.Read(ref TunnelPort)));
                return body;
            }
            internal async Task AssertClosedOnceAndHealthyAsync()
            {
                Assert.That(await Probe.Closes.Task.WaitAsync(Settle, Token), Is.EqualTo(1));
                if (Probe.Failure.Task.IsCompleted) throw new AssertionException("Handler failed.", await Probe.Failure.Task);
                Assert.That(await _client.GetStringAsync(Url + "healthy", Token), Is.EqualTo("healthy"));
                // The sibling request used the same TCP connection as the tunnel.
                Assert.That(Volatile.Read(ref HealthyPort), Is.EqualTo(Volatile.Read(ref TunnelPort)));
                Assert.That(_webServer.State, Is.EqualTo(WebServerState.Listening));
                await Task.Delay(100, Token);
                Assert.That(Volatile.Read(ref Probe.CloseCount), Is.EqualTo(1));
            }
            public async ValueTask DisposeAsync()
            {
                _client.Dispose();
                _handler.Dispose();
                _stop.Cancel();
                try { await Running.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally
                {
                    _webServer.Dispose();
                    _linked.Dispose();
                    _stop.Dispose();
                    _server.Dispose();
                }
            }
        }
    }
}
