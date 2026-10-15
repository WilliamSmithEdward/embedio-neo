using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;
using static EmbedIO.Tests.HttpTunnelLifetimeTest;

namespace EmbedIO.Tests
{
    // Distinguishes a peer's graceful end of input from a peer abort on an accepted
    // HTTP/2 tunnel. The raw peer writes frames and HPACK literals by hand, so the
    // exact frame that ends the stream is known rather than inferred from a client
    // library's disposal behavior.
    [TestFixture]
    public class HttpTunnelPeerAbortTest
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Http2PeerInputEndIsDistinguishedFromPeerReset(bool capsules, bool reset)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var endpoint = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithAction("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/healthy")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }
                    context.OnClose(_ => Interlocked.Increment(ref closes));
                    var capability = context as IHttpTunnelContext ?? throw new AssertionException("Missing managed tunnel capability.");
                    var tunnel = await capability.AcceptTunnelAsync("example-tunnel", capsules, stop.Token);
                    if (capsules)
                    {
                        var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                        var first = await channel.ReadHeaderAsync(stop.Token);
                        Assert.That(first?.Length, Is.EqualTo(2));
                        await channel.SkipPayloadAsync(stop.Token);
                        accepted.TrySetResult();
                        // The input ends exactly on a capsule boundary in both variants.
                        try { observed.TrySetResult(await channel.ReadHeaderAsync(stop.Token) is null ? "end" : "capsule"); }
                        catch (Exception error) when (IsTransportOutcome(error)) { observed.TrySetResult(error.GetType().Name); }
                    }
                    else
                    {
                        var bytes = new byte[2];
                        await tunnel.Stream.ReadExactlyAsync(bytes, stop.Token);
                        accepted.TrySetResult();
                        var outcome = await Outcome(tunnel.Stream.ReadAsync(new byte[16], 0, 16, stop.Token));
                        observed.TrySetResult(outcome == "eof" ? "end" : outcome);
                    }
                });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(endpoint.Host, endpoint.Port, stop.Token);
                using var wire = tcp.GetStream();
                await StartAsync(wire, stop.Token);
                await SendFrame(wire, 1, 4, 1, Fields(("CONNECT", ":method"), ("http", ":scheme"), (endpoint.Authority, ":authority"),
                    ("/tunnel", ":path"), ("example-tunnel", ":protocol"), (capsules ? "?1" : null, "capsule-protocol")), stop.Token);
                var head = await Until(wire, 1, 1, stop.Token);
                Assert.That(head.Flags & 1, Is.Zero, "A successful tunnel response must not end the stream.");
                await SendFrame(wire, 0, 0, 1, capsules ? new byte[] { 0x21, 2, 7, 7 } : new byte[] { 7, 7 }, stop.Token);
                await accepted.Task.WaitAsync(Settle, stop.Token);
                if (reset) await SendFrame(wire, 3, 0, 1, new byte[] { 0, 0, 0, 8 }, stop.Token);
                else await SendFrame(wire, 0, 1, 1, Array.Empty<byte>(), stop.Token);
                var outcome = await observed.Task.WaitAsync(Settle, stop.Token);
                TestContext.Out.WriteLine((reset ? "RST_STREAM(CANCEL)" : "END_STREAM") + " observed by the application as: " + outcome);
                if (reset) Assert.That(outcome, Is.Not.EqualTo("end"), "A peer reset must not be reported as a complete end of input.");
                else Assert.That(outcome, Is.EqualTo("end"));
                await SendFrame(wire, 1, 5, 3, Fields(("GET", ":method"), ("http", ":scheme"), (endpoint.Authority, ":authority"), ("/healthy", ":path")), stop.Token);
                var data = await Until(wire, 0, 3, stop.Token);
                Assert.That(Encoding.ASCII.GetString(data.Payload), Is.EqualTo("healthy"));
                var deadline = DateTime.UtcNow + Settle;
                while (Volatile.Read(ref closes) == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, stop.Token);
                await Task.Delay(100, stop.Token);
                Assert.That(Volatile.Read(ref closes), Is.EqualTo(1));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static async Task StartAsync(NetworkStream wire, CancellationToken token)
        {
            await wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), token);
            await SendFrame(wire, 4, 0, 0, Array.Empty<byte>(), token);
            var enabled = false;
            var acknowledged = false;
            while (!enabled || !acknowledged)
            {
                var frame = await ReceiveFrame(wire, token);
                if (frame.Type != 4) continue;
                if ((frame.Flags & 1) != 0) { acknowledged = true; continue; }
                for (var i = 0; i + 6 <= frame.Payload.Length; i += 6)
                    if (BinaryPrimitives.ReadUInt16BigEndian(frame.Payload.AsSpan(i)) == 8 && BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.AsSpan(i + 2)) == 1) enabled = true;
                await SendFrame(wire, 4, 1, 0, Array.Empty<byte>(), token);
                Assert.That(enabled, Is.True, "The server must advertise SETTINGS_ENABLE_CONNECT_PROTOCOL.");
            }
        }

        // HPACK literal fields without indexing or Huffman coding.
        private static byte[] Fields(params (string? Value, string Name)[] fields)
        {
            using var output = new MemoryStream();
            foreach (var (value, name) in fields)
            {
                if (value == null) continue;
                output.WriteByte(0);
                var nameBytes = Encoding.ASCII.GetBytes(name);
                output.WriteByte((byte)nameBytes.Length);
                output.Write(nameBytes, 0, nameBytes.Length);
                var valueBytes = Encoding.ASCII.GetBytes(value);
                output.WriteByte((byte)valueBytes.Length);
                output.Write(valueBytes, 0, valueBytes.Length);
            }
            return output.ToArray();
        }

        private static async Task SendFrame(Stream stream, byte type, byte flags, int id, byte[] payload, CancellationToken token)
        {
            var frame = new byte[9 + payload.Length];
            frame[0] = (byte)(payload.Length >> 16); frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)payload.Length;
            frame[3] = type; frame[4] = flags;
            BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), id);
            payload.CopyTo(frame, 9);
            await stream.WriteAsync(frame, token);
        }

        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)> ReceiveFrame(Stream stream, CancellationToken token)
        {
            var header = new byte[9];
            await stream.ReadExactlyAsync(header, token);
            var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(payload, token);
            return (header[3], header[4], BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & int.MaxValue, payload);
        }

        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)> Until(Stream stream, byte type, int id, CancellationToken token)
        {
            var seen = new List<string>();
            while (true)
            {
                var frame = await ReceiveFrame(stream, token);
                Assert.That(frame.Type, Is.Not.EqualTo(7), "The shared connection must not receive GOAWAY.");
                if (frame.Type == type && frame.Id == id) return frame;
                if (frame.Id == id && frame.Type == 3) throw new AssertionException($"Stream {id} was reset while waiting for frame type {type}.");
                seen.Add(frame.Type + "/" + frame.Id);
                if (seen.Count > 256) throw new AssertionException("Expected frame not received: " + string.Join(",", seen));
            }
        }
    }
}
