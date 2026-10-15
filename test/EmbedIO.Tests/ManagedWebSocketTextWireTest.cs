using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedWebSocketTextWireTest
    {
        private sealed class Echo : WebSocketModule
        {
            internal int Calls;
            internal Echo() : base("/ws", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Interlocked.Increment(ref Calls);
                return context.WebSocket.SendAsync(buffer, result.MessageType == 0, context.CancellationToken);
            }
        }
        [TestCase("80", true, false, true)]
        [TestCase("C080", true, false, true)]
        [TestCase("E08080", true, false, true)]
        [TestCase("EDA080", true, false, true)]
        [TestCase("F4908080", true, false, true)]
        [TestCase("F5808080", true, false, true)]
        [TestCase("FF", true, false, true)]
        [TestCase("E282", true, false, true)]
        [TestCase("F09F92", true, false, true)]
        [TestCase("E2|28A1", true, false, true)]
        [TestCase("E2|82", true, false, true)]
        [TestCase("F09F|92", true, false, true)]
        [TestCase("C2|20", true, false, true)]
        [TestCase("", true, true, true)]
        [TestCase("007F", true, true, true)]
        [TestCase("C280DFBF", true, true, true)]
        [TestCase("E0A080ED9FBFEE8080EFBFBF", true, true, true)]
        [TestCase("F0908080F48FBFBF", true, true, true)]
        [TestCase("C2|80", true, true, true)]
        [TestCase("E2|82|AC", true, true, true)]
        [TestCase("F0|9F|92|A9", true, true, true)]
        [TestCase("E2||82||AC|", true, true, true)]
        [TestCase("FF80C080", false, true, true)]
        [TestCase("E2|28A1", false, true, true)]
        [TestCase("FF", true, false, false)]
        [TestCase("E0|80", true, false, false)]
        public async Task MessageUtf8ValidationPreservesBinaryAndFragmentBoundaries(string frames, bool text, bool valid, bool final)
        {
            ArgumentNullException.ThrowIfNull(frames);
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var echo = new Echo();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(echo).WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, stop.Token);
                var stream = tcp.GetStream();
                var handshake = $"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), stop.Token);
                var header = new StringBuilder(); var single = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(header.Length, Is.LessThan(8192));
                    await stream.ReadExactlyAsync(single, stop.Token); header.Append((char)single[0]);
                }
                Assert.That(header.ToString(), Does.StartWith("HTTP/1.1 101"));
                var parts = frames.Split('|');
                using var expected = new MemoryStream();
                for (var i = 0; i < parts.Length; ++i)
                {
                    var bytes = Convert.FromHexString(parts[i]); expected.Write(bytes);
                    await SendFrame(stream, (byte)((i == 0 ? text ? 1 : 2 : 0) | (final && i == parts.Length - 1 ? 128 : 0)), bytes, stop.Token);
                    if (i < parts.Length - 1)
                    {
                        // A control payload is arbitrary bytes, even during partial text.
                        await SendFrame(stream, 0x89, new byte[] { 0xff }, stop.Token);
                        var pong = await ReadFrame(stream, stop.Token);
                        Assert.That(pong.Opcode, Is.EqualTo(0x8a));
                        Assert.That(pong.Bytes, Is.EqualTo(new byte[] { 0xff }));
                    }
                }
                var reply = await ReadFrame(stream, stop.Token);
                if (valid)
                {
                    Assert.That(reply.Opcode, Is.EqualTo(text ? 0x81 : 0x82));
                    Assert.That(reply.Bytes, Is.EqualTo(expected.ToArray()));
                    Assert.That(Volatile.Read(ref echo.Calls), Is.EqualTo(1));
                    // A subsequent message must not inherit a text decoder's partial state.
                    await SendFrame(stream, 0x81, new byte[] { 65 }, stop.Token);
                    var next = await ReadFrame(stream, stop.Token);
                    Assert.That(next.Opcode, Is.EqualTo(0x81)); Assert.That(next.Bytes, Is.EqualTo(new byte[] { 65 }));
                    await SendFrame(stream, 0x01, new byte[] { 0xe2 }, stop.Token);
                    await SendFrame(stream, 0x80, new byte[] { 0x82, 0xac }, stop.Token);
                    var reused = await ReadFrame(stream, stop.Token);
                    Assert.That(reused.Opcode, Is.EqualTo(0x81)); Assert.That(reused.Bytes, Is.EqualTo(new byte[] { 0xe2, 0x82, 0xac }));
                    await SendFrame(stream, 0x82, new byte[] { 0xff }, stop.Token);
                    var binary = await ReadFrame(stream, stop.Token);
                    Assert.That(binary.Opcode, Is.EqualTo(0x82)); Assert.That(binary.Bytes, Is.EqualTo(new byte[] { 0xff }));
                    Assert.That(Volatile.Read(ref echo.Calls), Is.EqualTo(4));
                    await SendFrame(stream, 0x88, new byte[] { 3, 232 }, stop.Token);
                    Assert.That((await ReadFrame(stream, stop.Token)).Opcode, Is.EqualTo(0x88));
                }
                else
                {
                    Assert.That(reply.Opcode, Is.EqualTo(0x88), "Malformed text must close before reaching application callbacks.");
                    Assert.That(reply.Bytes.Length, Is.GreaterThanOrEqualTo(2));
                    Assert.That((reply.Bytes[0] << 8) | reply.Bytes[1], Is.EqualTo(1007));
                    Assert.That(Volatile.Read(ref echo.Calls), Is.Zero);
                }
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await client.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private static async Task SendFrame(Stream stream, byte flags, byte[] bytes, CancellationToken token)
        {
            Assert.That(bytes.Length, Is.LessThan(126));
            var wire = new byte[6 + bytes.Length]; wire[0] = flags; wire[1] = (byte)(128 | bytes.Length);
            var key = new byte[] { 3, 7, 13, 29 }; key.CopyTo(wire, 2);
            for (var i = 0; i < bytes.Length; ++i) wire[6 + i] = (byte)(bytes[i] ^ key[i % 4]);
            await stream.WriteAsync(wire, token);
        }
        private static async Task<(byte Opcode, byte[] Bytes)> ReadFrame(Stream stream, CancellationToken token)
        {
            var header = new byte[2]; await stream.ReadExactlyAsync(header, token);
            Assert.That(header[1], Is.LessThan(126));
            var payload = new byte[header[1]]; await stream.ReadExactlyAsync(payload, token);
            return (header[0], payload);
        }
    }
}
