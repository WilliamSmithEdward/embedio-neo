using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private sealed class NegotiationSocketModule : WebSocketModule
        {
            internal NegotiationSocketModule() : base("/ws", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
        }

        [TestCase("12")]
        [TestCase("")]
        [TestCase(null)]
        public async Task InvalidWebSocketVersionReturns400AndPreservesSiblingStream(string? version)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithModule(new NegotiationSocketModule())
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True);
                using var socket = new TcpClient();
                var uri = new Uri(url);
                await socket.ConnectAsync(uri.Host, uri.Port, stop.Token);
                var wire = socket.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), stop.Token);
                await SendWire(wire, 4, 0, 0, Array.Empty<byte>(), stop.Token);
                _ = await Until(wire, 4, 0, stop.Token);
                await SendWire(wire, 4, 1, 0, Array.Empty<byte>(), stop.Token);
                using var block = new MemoryStream();
                Literal(block, ":method", "CONNECT");
                Literal(block, ":scheme", "http");
                Literal(block, ":path", "/ws");
                Literal(block, ":authority", uri.Authority);
                Literal(block, ":protocol", "websocket");
                if (version != null) Literal(block, "sec-websocket-version", version);
                await SendWire(wire, 1, 5, 1, block.ToArray(), stop.Token);
                var decoder = Activator.CreateInstance(Type("HpackDecoder"), Flags, null, new object[] { 32768 }, null)
                    ?? throw new AssertionException("Missing decoder.");
                var decode = (Type("HpackDecoder").GetMethod("Decode", Flags) ?? throw new AssertionException("Missing Decode.")).CreateDelegate<Func<byte[], Array>>(decoder);
                var fields = decode((await Until(wire, 1, 1, stop.Token)).Payload).Cast<object>()
                    .GroupBy(field => Property<string>(field, "Name"))
                    .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(field => Property<string>(field, "Value"))));
                Assert.That(fields[":status"], Is.EqualTo("400"));
                Assert.That(fields["sec-websocket-version"], Is.EqualTo("13"));
                using var next = new MemoryStream();
                Literal(next, ":method", "GET"); Literal(next, ":scheme", "http");
                Literal(next, ":path", "/healthy"); Literal(next, ":authority", uri.Authority);
                await SendWire(wire, 1, 5, 3, next.ToArray(), stop.Token);
                var response = decode((await Until(wire, 1, 3, stop.Token)).Payload).Cast<object>()
                    .GroupBy(field => Property<string>(field, "Name"))
                    .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(field => Property<string>(field, "Value"))));
                Assert.That(response[":status"], Is.EqualTo("200"));
                Assert.That(Encoding.UTF8.GetString((await Until(wire, 0, 3, stop.Token)).Payload), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static void Literal(Stream block, string name, string value)
        {
            var key = Encoding.ASCII.GetBytes(name); var bytes = Encoding.ASCII.GetBytes(value);
            Assert.That(key.Length, Is.LessThan(127)); Assert.That(bytes.Length, Is.LessThan(127));
            block.WriteByte(0); block.WriteByte((byte)key.Length); block.Write(key, 0, key.Length);
            block.WriteByte((byte)bytes.Length); block.Write(bytes, 0, bytes.Length);
        }
    }
}
