using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Lifetime and resource behavior of accepted HTTP/1 tunnels over real sockets.
    // Each case observes the application side (pending operations, close callbacks)
    // and the peer side, then requires a healthy sibling request on the same listener.
    [TestFixture]
    public class HttpTunnelLifetimeTest
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        [TestCase(false)]
        [TestCase(true)]
        public async Task PeerResetReleasesAPendingReadAndClosesTheContextOnce(bool tls)
        {
            await using var host = await TunnelHost.StartAsync(tls, async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                // No deadline token: only the peer reset may release this read.
                var outcome = await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16));
                probe.Observed.TrySetResult(outcome);
            });
            var peer = await host.ConnectAsync(null);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            peer.Reset();
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed application outcome: " + observed);
            Assert.That(observed, Is.AnyOf("eof", nameof(IOException), nameof(ObjectDisposedException)), observed);
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task PeerResetFailsABackpressuredWriteWithBoundedOutput()
        {
            long written = 0;
            await using var host = await TunnelHost.StartAsync(false, async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                var chunk = new byte[65536];
                try
                {
                    // The cap only bounds a failed test; backpressure must stop this loop.
                    while (Interlocked.Read(ref written) < 512L * 1024 * 1024)
                    {
                        await tunnel.Stream.WriteAsync(chunk, 0, chunk.Length);
                        Interlocked.Add(ref written, chunk.Length);
                    }
                    probe.Observed.TrySetResult("unbounded");
                }
                catch (Exception error) when (IsTransportOutcome(error)) { probe.Observed.TrySetResult(error.GetType().Name); }
            });
            var peer = await host.ConnectAsync(null);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            await WaitForBackpressureAsync(() => Interlocked.Read(ref written), host.Token);
            peer.Reset();
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed application outcome: " + observed);
            Assert.That(observed, Is.AnyOf(nameof(IOException), nameof(ObjectDisposedException), nameof(OperationCanceledException)), observed);
            Assert.That(Interlocked.Read(ref written), Is.LessThan(512L * 1024 * 1024));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HandlerFailureReleasesAConcurrentPendingRead(bool tls)
        {
            Task<string>? pending = null;
            await using var host = await TunnelHost.StartAsync(tls, (tunnel, probe, token) =>
            {
                pending = Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16));
                probe.Accepted.TrySetResult();
                throw new InvalidOperationException("Injected handler failure with a read in flight.");
            }, expectHandlerFailure: true);
            var peer = await host.ConnectAsync(null);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            var read = pending ?? throw new AssertionException("Missing pending read.");
            var outcome = await read.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed pending read outcome: " + outcome);
            Assert.That(outcome, Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(ObjectDisposedException), nameof(IOException), "eof"), outcome);
            Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero, "No bytes may follow the handshake after an application failure.");
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentCompletionAndDisposalShareOneClosure(bool tls)
        {
            await using var host = await TunnelHost.StartAsync(tls, async (tunnel, probe, token) =>
            {
                await tunnel.Stream.WriteAsync(new byte[] { 1, 2, 3 }, 0, 3, token);
                using var start = new ManualResetEventSlim();
                var completions = new Task[4];
                var closes = new Task[8];
                var workers = new List<Task>();
                for (var i = 0; i < 4; i++)
                {
                    var index = i;
                    workers.Add(Task.Run(() => { start.Wait(token); completions[index] = tunnel.CompleteOutputAsync(token); }, token));
                }
                for (var i = 0; i < 8; i++)
                {
                    var index = i;
                    workers.Add(Task.Run(() =>
                    {
                        start.Wait(token);
                        switch (index % 3)
                        {
                            case 0: closes[index] = tunnel.CloseAsync(); break;
                            case 1: closes[index] = tunnel.DisposeAsync().AsTask(); break;
                            default: tunnel.Dispose(); closes[index] = tunnel.CloseAsync(); break;
                        }
                    }, token));
                }
                start.Set();
                await Task.WhenAll(workers);
                foreach (var completion in completions) Assert.That(completion, Is.SameAs(completions[0]));
                foreach (var close in closes) Assert.That(close, Is.SameAs(closes[0]));
                await closes[0].WaitAsync(token);
                Assert.That(tunnel.Stream.CanRead, Is.False);
                Assert.That(tunnel.Stream.CanWrite, Is.False);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await tunnel.Stream.WriteAsync(new byte[1], 0, 1, token));
                probe.Observed.TrySetResult("closed");
            });
            var peer = await host.ConnectAsync(null);
            var echoed = new byte[3];
            await peer.Wire.ReadExactlyAsync(echoed, host.Token);
            Assert.That(echoed, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero);
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.EqualTo("closed"));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task CanceledPendingReadLeavesOutputCompletionAndCloseUsable()
        {
            await using var host = await TunnelHost.StartAsync(false, async (tunnel, probe, token) =>
            {
                using var cancel = new CancellationTokenSource();
                var read = Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, cancel.Token));
                probe.Accepted.TrySetResult();
                await Task.Delay(100, token);
                Assert.That(read.IsCompleted, Is.False, "The read must still be waiting for peer input.");
                cancel.Cancel();
                var outcome = await read.WaitAsync(Settle, token);
                Assert.That(outcome, Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException)), outcome);
                await tunnel.Stream.WriteAsync(new byte[] { 5 }, 0, 1, token);
                await tunnel.CompleteOutputAsync(token);
                probe.Observed.TrySetResult("completed");
            });
            var peer = await host.ConnectAsync(null);
            await host.Probe.Accepted.Task.WaitAsync(host.Token);
            var bytes = new byte[1];
            await peer.Wire.ReadExactlyAsync(bytes, host.Token);
            Assert.That(bytes[0], Is.EqualTo(5));
            Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero);
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.EqualTo("completed"));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task ServerStopReleasesABackpressuredWriterAndDrainsTheListener()
        {
            long written = 0;
            var host = await TunnelHost.StartAsync(false, async (tunnel, probe, token) =>
            {
                probe.Accepted.TrySetResult();
                var chunk = new byte[65536];
                try
                {
                    while (Interlocked.Read(ref written) < 512L * 1024 * 1024)
                    {
                        await tunnel.Stream.WriteAsync(chunk, 0, chunk.Length, token);
                        Interlocked.Add(ref written, chunk.Length);
                    }
                    probe.Observed.TrySetResult("unbounded");
                }
                catch (Exception error) when (IsTransportOutcome(error)) { probe.Observed.TrySetResult(error.GetType().Name); }
            }, useContextToken: true);
            try
            {
                var peer = await host.ConnectAsync(null);
                await host.Probe.Accepted.Task.WaitAsync(host.Token);
                await WaitForBackpressureAsync(() => Interlocked.Read(ref written), host.Token);
                host.StopServer();
                await host.Running.WaitAsync(Settle);
                var observed = await host.Probe.Observed.Task.WaitAsync(Settle);
                TestContext.Out.WriteLine("Observed application outcome: " + observed);
                Assert.That(observed, Is.AnyOf(nameof(OperationCanceledException), nameof(TaskCanceledException), nameof(IOException), nameof(ObjectDisposedException)), observed);
                Assert.That(await host.Probe.Closes.Task.WaitAsync(Settle), Is.EqualTo(1));
                peer.Dispose();
            }
            finally { await host.DisposeAsync(); }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task BulkPeerInputIsObservedCompletelyAroundSendCompletion(bool tls, bool capsules)
        {
            var expected = Payload(1 << 20, 7);
            // TLS 1.2 close_notify ends both directions, so there the peer sends first.
            var halfClose = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await TunnelHost.StartAsync(tls, async (tunnel, probe, token) =>
            {
                var independent = await halfClose.Task.WaitAsync(token);
                if (independent) await tunnel.CompleteOutputAsync(token);
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
                if (!independent) await tunnel.CompleteOutputAsync(token);
                probe.Observed.TrySetResult(total + ":" + Convert.ToHexString(hash.GetHashAndReset()));
            }, protocol: capsules ? "example-tunnel" : null, capsules: capsules);
            var peer = await host.ConnectAsync(capsules ? "example-tunnel" : null);
            var independentDirections = peer.Tls is null or SslProtocols.Tls13;
            TestContext.Out.WriteLine($"Negotiated {peer.Tls?.ToString() ?? "plain TCP"}; peer input follows server send completion: {independentDirections}");
            halfClose.TrySetResult(independentDirections);
            if (independentDirections) Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero, "Server send completion must precede peer input.");
            var random = new Random(31);
            var offset = 0;
            while (offset < expected.Length)
            {
                var size = Math.Min(expected.Length - offset, random.Next(0, 40000));
                if (capsules)
                {
                    // Interleave unknown and empty capsules that the application skips.
                    await peer.Wire.WriteAsync(Capsule(0x2a, Payload(random.Next(0, 300), 3)), host.Token);
                    await peer.Wire.WriteAsync(Capsule(0, Array.Empty<byte>()), host.Token);
                    await peer.Wire.WriteAsync(Capsule(0, expected.AsSpan(offset, size).ToArray()), host.Token);
                }
                else await peer.Wire.WriteAsync(expected.AsMemory(offset, size), host.Token);
                offset += size;
            }
            await peer.CompleteSendAsync();
            if (!independentDirections) Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero);
            var observed = await host.Probe.Observed.Task.WaitAsync(Settle, host.Token);
            TestContext.Out.WriteLine("Observed application outcome: " + observed);
            Assert.That(observed, Is.EqualTo(expected.Length + ":" + Convert.ToHexString(SHA256.HashData(expected))));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task IncompleteOutgoingCapsuleClosesOnlyItsConnection()
        {
            await using var host = await TunnelHost.StartAsync(false, async (tunnel, probe, token) =>
            {
                var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                await channel.WriteHeaderAsync(0, 10, token);
                await channel.WritePayloadAsync(new byte[] { 1, 2, 3 }, 0, 3, token);
                probe.Observed.TrySetResult("returned");
                // Returning leaves seven declared payload bytes unwritten.
            }, protocol: "example-tunnel", capsules: true);
            var peer = await host.ConnectAsync("example-tunnel");
            // HTTP/1 has no stream-level error code: the peer sees the connection end
            // before the declared capsule length, which its own framing must reject.
            Assert.That(await peer.DrainToEndAsync(host.Token), Is.LessThanOrEqualTo(5));
            Assert.That(await host.Probe.Observed.Task.WaitAsync(Settle, host.Token), Is.EqualTo("returned"));
            await host.AssertClosedOnceAndHealthyAsync();
        }

        [Test]
        public async Task RepeatedTunnelsDoNotRetainTunnelOrStreamObjects()
        {
            const int Rounds = 64;
            var tunnels = new List<WeakReference>();
            await using var host = await TunnelHost.StartAsync(false, async (tunnel, probe, token) =>
            {
                lock (tunnels) { tunnels.Add(new WeakReference(tunnel)); tunnels.Add(new WeakReference(tunnel.Stream)); }
                var bytes = new byte[4];
                await tunnel.Stream.ReadExactlyAsync(bytes, token);
                await tunnel.Stream.WriteAsync(bytes, token);
            });
            for (var i = 0; i < Rounds; i++)
            {
                using var peer = await host.ConnectAsync(null);
                var bytes = BitConverter.GetBytes(i);
                await peer.Wire.WriteAsync(bytes, host.Token);
                var echoed = new byte[4];
                await peer.Wire.ReadExactlyAsync(echoed, host.Token);
                Assert.That(echoed, Is.EqualTo(bytes));
                Assert.That(await peer.DrainToEndAsync(host.Token), Is.Zero);
            }
            await host.WaitForClosesAsync(Rounds);
            await host.AssertHealthyAsync();
            var alive = int.MaxValue;
            for (var attempt = 0; attempt < 20 && alive != 0; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                lock (tunnels) alive = tunnels.FindAll(reference => reference.IsAlive).Count;
                if (alive != 0) await Task.Delay(50, host.Token);
            }
            Assert.That(tunnels.Count, Is.EqualTo(Rounds * 2));
            Assert.That(alive, Is.Zero, "Closed tunnels and their streams must become collectable.");
        }

        private static async Task WaitForBackpressureAsync(Func<long> written, CancellationToken token)
        {
            // Backpressure means the writer stopped advancing for a while after
            // filling the socket buffers, not that it wrote a fixed amount.
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

        internal static async Task<string> Outcome(Task<int> operation)
        {
            try { return await operation.ConfigureAwait(false) == 0 ? "eof" : "data"; }
            catch (Exception error) when (IsTransportOutcome(error)) { return error.GetType().Name; }
        }

        // Outcomes the transport may report; anything else fails the test.
        internal static bool IsTransportOutcome(Exception error)
            => error is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidDataException;

        internal static byte[] Payload(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        internal static byte[] Capsule(long type, byte[] payload)
        {
            using var output = new MemoryStream();
            WriteVarint(output, type);
            WriteVarint(output, payload.Length);
            output.Write(payload, 0, payload.Length);
            return output.ToArray();
        }

        // RFC 9000 variable-length integer, written independently of EmbedIO's codec.
        internal static void WriteVarint(Stream output, long value)
        {
            if (value < 64) output.WriteByte((byte)value);
            else if (value < 16384) { output.WriteByte((byte)(0x40 | (value >> 8))); output.WriteByte((byte)value); }
            else if (value < 1073741824)
            {
                output.WriteByte((byte)(0x80 | (value >> 24))); output.WriteByte((byte)(value >> 16));
                output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value);
            }
            else
            {
                output.WriteByte((byte)(0xc0 | (value >> 56)));
                for (var shift = 48; shift >= 0; shift -= 8) output.WriteByte((byte)(value >> shift));
            }
        }

        internal sealed class Probe
        {
            internal readonly TaskCompletionSource Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<string> Observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<int> Closes = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<Exception> Failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int CloseCount;
        }

        private sealed class TunnelPeer : IDisposable
        {
            private readonly TcpClient _tcp;
            internal TunnelPeer(TcpClient tcp, Stream wire) { _tcp = tcp; Wire = wire; Tls = (wire as SslStream)?.SslProtocol; }
            internal SslProtocols? Tls { get; }
            internal Stream Wire { get; }
            internal void Reset()
            {
                _tcp.Client.LingerState = new LingerOption(true, 0);
                _tcp.Client.Close();
            }
            internal async Task CompleteSendAsync()
            {
                if (Wire is SslStream ssl) await ssl.ShutdownAsync();
                _tcp.Client.Shutdown(SocketShutdown.Send);
            }
            internal async Task<long> DrainToEndAsync(CancellationToken token)
            {
                var buffer = new byte[4096];
                long total = 0;
                try
                {
                    int count;
                    while ((count = await Wire.ReadAsync(buffer, token)) != 0) total += count;
                }
                catch (IOException) { /* A failed carrier may reset after its final bytes. */ }
                return total;
            }
            public void Dispose() { Wire.Dispose(); _tcp.Dispose(); }
        }

        private sealed class TunnelHost : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
            private readonly CancellationTokenSource _server = new();
            private CancellationTokenSource? _linked;
            private readonly X509Certificate2 _certificate;
            private readonly List<TunnelPeer> _peers = new();
            private WebServer? _webServer;
            private TunnelHost(bool tls)
            {
                Tls = tls;
                _certificate = HttpsSmoke.CreateCertificate();
                Url = HttpsSmoke.GetUrl();
                if (!tls) Url = Url.Replace("https:", "http:", StringComparison.Ordinal);
                Endpoint = new Uri(Url);
            }
            internal bool Tls { get; }
            internal string Url { get; }
            internal Uri Endpoint { get; }
            internal Probe Probe { get; } = new();
            internal CancellationToken Token => _stop.Token;
            internal Task Running { get; private set; } = Task.CompletedTask;
            internal static Task<TunnelHost> StartAsync(bool tls, Func<HttpTunnel, Probe, CancellationToken, Task> body,
                string? protocol = null, bool capsules = false, bool expectHandlerFailure = false, bool useContextToken = false)
            {
                var host = new TunnelHost(tls);
                var server = new WebServer(o => o.WithUrlPrefix(host.Url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(host._certificate))
                    .WithAction("/", HttpVerbs.Any, async context =>
                    {
                        if (context.Request.Url.AbsolutePath == "/healthy")
                        {
                            await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                            return;
                        }
                        context.OnClose(_ => host.Probe.Closes.TrySetResult(Interlocked.Increment(ref host.Probe.CloseCount)));
                        var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing managed tunnel capability.");
                        var token = useContextToken ? context.CancellationToken : host.Token;
                        var tunnel = await capability.AcceptTunnelAsync(protocol, capsules, token);
                        try { await body(tunnel, host.Probe, token); }
                        catch (Exception error) when (!expectHandlerFailure)
                        {
                            host.Probe.Failure.TrySetResult(error);
                            throw;
                        }
                    });
                host._webServer = server;
                host._linked = CancellationTokenSource.CreateLinkedTokenSource(host._stop.Token, host._server.Token);
                host.Running = server.RunAsync(host._linked.Token);
                return Task.FromResult(host);
            }
            internal void StopServer() => _server.Cancel();
            internal async Task<TunnelPeer> ConnectAsync(string? protocol)
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(Endpoint.Host, Endpoint.Port, Token);
                Stream wire = tcp.GetStream();
                if (Tls)
                {
                    var fingerprint = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
                    var ssl = new SslStream(wire, false, (_, peer, _, _) => peer?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
                    wire = ssl;
                    // Platform default: Apple server-side SslStream negotiates TLS 1.2.
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    { TargetHost = "localhost", EnabledSslProtocols = SslProtocols.None }, Token);
                    Assert.That(ssl.SslProtocol, Is.EqualTo(OperatingSystem.IsMacOS() ? SslProtocols.Tls12 : SslProtocols.Tls13));
                }
                var peer = new TunnelPeer(tcp, wire);
                lock (_peers) _peers.Add(peer);
                var authority = Endpoint.Host + ":" + Endpoint.Port;
                var head = protocol == null
                    ? $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n"
                    : $"GET / HTTP/1.1\r\nHost: {authority}\r\nConnection: Upgrade\r\nUpgrade: {protocol}\r\nCapsule-Protocol: ?1\r\n\r\n";
                await wire.WriteAsync(Encoding.ASCII.GetBytes(head), Token);
                var response = await ReadHeadAsync(wire, Token);
                Assert.That(response.StartsWith(protocol == null ? "HTTP/1.1 200 " : "HTTP/1.1 101 ", StringComparison.Ordinal), Is.True, response);
                return peer;
            }
            internal async Task WaitForClosesAsync(int count)
            {
                var deadline = DateTime.UtcNow + Settle;
                while (Volatile.Read(ref Probe.CloseCount) < count && DateTime.UtcNow < deadline) await Task.Delay(20, Token);
                Assert.That(Volatile.Read(ref Probe.CloseCount), Is.EqualTo(count));
            }
            internal async Task AssertClosedOnceAndHealthyAsync()
            {
                Assert.That(await Probe.Closes.Task.WaitAsync(Settle, Token), Is.EqualTo(1));
                if (Probe.Failure.Task.IsCompleted) throw new AssertionException("Handler failed.", await Probe.Failure.Task);
                await AssertHealthyAsync();
                // Late duplicate callbacks would surface here.
                await Task.Delay(100, Token);
                Assert.That(Volatile.Read(ref Probe.CloseCount), Is.EqualTo(1));
            }
            internal async Task AssertHealthyAsync()
            {
                using var client = Tls ? HttpsSmoke.CreateClient(_certificate) : new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await client.GetStringAsync(Url + "healthy", Token), Is.EqualTo("healthy"));
                Assert.That(_webServer?.State, Is.EqualTo(WebServerState.Listening));
            }
            public async ValueTask DisposeAsync()
            {
                lock (_peers) foreach (var peer in _peers) peer.Dispose();
                _stop.Cancel();
                try { await Running.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally
                {
                    _webServer?.Dispose();
                    _certificate.Dispose();
                    _stop.Dispose();
                    _server.Dispose();
                    _linked?.Dispose();
                }
            }
        }

        internal static async Task<string> ReadHeadAsync(Stream stream, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            var single = new byte[1];
            var matched = 0;
            var end = new byte[] { 13, 10, 13, 10 };
            while (matched != end.Length)
            {
                if (await stream.ReadAsync(single, token) == 0) throw new EndOfStreamException("Incomplete tunnel handshake.");
                bytes.WriteByte(single[0]);
                matched = single[0] == end[matched] ? matched + 1 : single[0] == 13 ? 1 : 0;
                if (bytes.Length > 32768) throw new IOException("Unbounded tunnel handshake.");
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }
}
