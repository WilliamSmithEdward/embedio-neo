using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class HttpTunnelContextTest
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Http1HandoffPreservesPipelinedBytesAndReadsAfterSendCompletion(bool tls, bool capsules)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            if (!tls) url = url.Replace("https:", "http:", StringComparison.Ordinal);
            var endpoint = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/healthy")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }
                    context.OnClose(_ => closed.TrySetResult(Interlocked.Increment(ref closes)));
                    try
                    {
                        var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing managed tunnel capability.");
                        var tunnel = await capability.AcceptTunnelAsync(capsules ? "example-tunnel/1" : null, capsules, stop.Token);
                        if (capsules)
                        {
                            var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule framing.");
                            var first = await channel.ReadHeaderAsync(stop.Token);
                            Assert.That(first?.Type, Is.EqualTo(0));
                            Assert.That(first?.Length, Is.EqualTo(3));
                            var bytes = new byte[3];
                            var count = 0;
                            while (count != bytes.Length) count += await channel.ReadPayloadAsync(bytes, count, bytes.Length - count, stop.Token);
                            Assert.That(bytes, Is.EqualTo(new byte[] { 7, 8, 9 }));
                            await channel.WriteHeaderAsync(17, 3, stop.Token);
                            await channel.WritePayloadAsync(bytes, 0, bytes.Length, stop.Token);
                            await tunnel.CompleteOutputAsync(stop.Token);
                            var last = await channel.ReadHeaderAsync(stop.Token);
                            Assert.That(last?.Length, Is.EqualTo(1));
                            Assert.That(await channel.ReadPayloadAsync(bytes, 0, 1, stop.Token), Is.EqualTo(1));
                            Assert.That(bytes[0], Is.EqualTo(42));
                        }
                        else
                        {
                            var bytes = new byte[3];
                            await tunnel.Stream.ReadExactlyAsync(bytes, stop.Token);
                            Assert.That(bytes, Is.EqualTo(new byte[] { 7, 8, 9 }));
                            await tunnel.Stream.WriteAsync(bytes, stop.Token);
                            await tunnel.CompleteOutputAsync(stop.Token);
                            var last = new byte[4];
                            await tunnel.Stream.ReadExactlyAsync(last, stop.Token);
                            Assert.That(Encoding.ASCII.GetString(last), Is.EqualTo("done"));
                        }
                        verified.TrySetResult();
                    }
                    catch (Exception error) { verified.TrySetException(error); throw; }
                });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, stop.Token);
                Stream wire = tcp.GetStream();
                if (tls)
                {
                    var fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                    var ssl = new SslStream(wire, false, (_, peer, _, _) => peer?.GetCertHashString(HashAlgorithmName.SHA256) == fingerprint);
                    wire = ssl;
                    // TLS 1.3 allows each peer to finish its send side independently.
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = SslProtocols.Tls13
                    }, stop.Token);
                }
                using (wire)
                {
                    var authority = endpoint.Host + ":" + endpoint.Port;
                    var head = capsules
                        ? $"GET / HTTP/1.1\r\nHost: {authority}\r\nConnection: Upgrade\r\nUpgrade: Example-Tunnel/1\r\nCapsule-Protocol: ?1\r\n\r\n"
                        : $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n";
                    var early = capsules ? new byte[] { 0, 3, 7, 8, 9 } : new byte[] { 7, 8, 9 };
                    await wire.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(early).ToArray(), stop.Token);
                    var response = await ReadHeadAsync(wire, stop.Token);
                    Assert.That(response.StartsWith(capsules ? "HTTP/1.1 101 " : "HTTP/1.1 200 ", StringComparison.Ordinal), Is.True);
                    Assert.That(response.Contains("Content-Length:", StringComparison.OrdinalIgnoreCase), Is.False);
                    Assert.That(response.Contains("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase), Is.False);
                    Assert.That(response.Contains("Content-Type:", StringComparison.OrdinalIgnoreCase), Is.False);
                    if (capsules)
                    {
                        Assert.That(response.Contains("Upgrade: example-tunnel/1", StringComparison.OrdinalIgnoreCase), Is.True);
                        Assert.That(response.Contains("Capsule-Protocol: ?1", StringComparison.OrdinalIgnoreCase), Is.True);
                    }
                    var echoed = new byte[capsules ? 5 : 3];
                    await wire.ReadExactlyAsync(echoed, stop.Token);
                    Assert.That(echoed, Is.EqualTo(capsules ? new byte[] { 17, 3, 7, 8, 9 } : new byte[] { 7, 8, 9 }));
                    Assert.That(await wire.ReadAsync(new byte[1], stop.Token), Is.Zero, "The server must finish its send side before the peer's final input.");
                    await wire.WriteAsync(capsules ? new byte[] { 0, 1, 42 } : Encoding.ASCII.GetBytes("done"), stop.Token);
                    await verified.Task.WaitAsync(stop.Token);
                    Assert.That(await closed.Task.WaitAsync(stop.Token), Is.EqualTo(1));
                }
                using var client = tls ? HttpsSmoke.CreateClient(certificate)
                    : new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await client.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                Assert.That(closes, Is.EqualTo(1));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Test]
        public async Task Http1ApplicationFailureAfterHandoffDoesNotStopTheListener()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var endpoint = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/healthy")
                {
                    await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }
                context.OnClose(_ => closed.TrySetResult());
                var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing tunnel capability.");
                await capability.AcceptTunnelAsync("example-tunnel", false, stop.Token);
                throw new InvalidOperationException("Injected failure after the protocol handoff.");
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, stop.Token);
                using var wire = tcp.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {endpoint.Host}:{endpoint.Port}\r\nConnection: Upgrade\r\nUpgrade: example-tunnel\r\n\r\n"), stop.Token);
                var response = await ReadHeadAsync(wire, stop.Token);
                Assert.That(response.StartsWith("HTTP/1.1 101 ", StringComparison.Ordinal), Is.True);
                try { Assert.That(await wire.ReadAsync(new byte[1], stop.Token), Is.Zero); }
                catch (IOException) { /* A failed accepted carrier may reset its connection. */ }
                await closed.Task.WaitAsync(stop.Token);
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await client.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private static async Task<string> ReadHeadAsync(Stream stream, CancellationToken token)
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
