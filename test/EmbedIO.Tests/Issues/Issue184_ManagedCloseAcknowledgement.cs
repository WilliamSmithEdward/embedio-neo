using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue184_ManagedCloseAcknowledgement
    {
        [Repeat(10)]
        [TestCase("connected", false)]
        [TestCase("message", false)]
        [TestCase("external", false)]
        [TestCase("connected", true)]
        [TestCase("message", true)]
        [TestCase("external", true)]
        public async Task ValidAcknowledgementClosesTransportBeforeTheClientRuntimeDeadline(string origin, bool dataDuringClose)
        {
            var url = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var module = new ClosingModule(origin);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module)
                .OnGet("/health", c => c.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, limit.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /socket HTTP/1.1\r\nHost: 127.0.0.1:{new Uri(url).Port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"), limit.Token);
                using var header = new MemoryStream();
                var one = new byte[1];
                while (header.Length < 8192)
                {
                    await stream.ReadExactlyAsync(one, limit.Token);
                    header.WriteByte(one[0]);
                    if (Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                Assert.That(Encoding.ASCII.GetString(header.ToArray()), Does.StartWith("HTTP/1.1 101"));
                var context = await module.Connected.Task.WaitAsync(limit.Token);
                Task? externalClose = null;
                if (origin == "message") await SendMasked(stream, 1, Encoding.UTF8.GetBytes("close"), limit.Token);
                if (origin == "external") externalClose = Task.Run(() => context.WebSocket.CloseAsync(CloseStatusCode.PolicyViolation, "test", limit.Token));
                var prefix = new byte[2];
                await stream.ReadExactlyAsync(prefix, limit.Token);
                Assert.That(prefix[0], Is.EqualTo(0x88));
                Assert.That(prefix[1] & 0x80, Is.Zero);
                var payload = new byte[prefix[1] & 0x7f];
                await stream.ReadExactlyAsync(payload, limit.Token);
                Assert.That(payload, Is.EqualTo(new byte[] { 3, 240, (byte)'t', (byte)'e', (byte)'s', (byte)'t' }));
                if (dataDuringClose)
                {
                    await SendMasked(stream, 1, Encoding.UTF8.GetBytes("after-close"), limit.Token);
                    await SendMasked(stream, 2, new byte[] { 1, 2, 3 }, limit.Token);
                }
                await SendMasked(stream, 8, new byte[] { 3, 232 }, limit.Token);
                // The BCL waits one second for TCP EOF after acknowledgement.
                // A valid acknowledgement must not consume that entire budget.
                Assert.That(await stream.ReadAsync(one, limit.Token).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500)), Is.Zero);
                await module.Disconnected.Task.WaitAsync(limit.Token);
                if (externalClose != null) await externalClose.WaitAsync(limit.Token);
                Assert.That(module.Disconnects, Is.EqualTo(1));
                Assert.That(module.Messages, Is.EqualTo(origin == "message" ? 1 : 0));
                using var healthy = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await healthy.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase("connected")]
        [TestCase("message")]
        public async Task RepeatedBclCloseAcknowledgementsCompleteWithoutClientDisposalErrors(string origin)
        {
            var url = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var module = new ClosingModule(origin);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO)).WithModule(module)
                .OnGet("/health", c => c.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            // Keep all 64 sequential close/disconnect rounds within one overall deadline.
            // Managed disconnection now follows the terminal close signal without polling.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = server.RunAsync(stop.Token);
            try
            {
                for (var iteration = 0; iteration < 64; iteration++)
                {
                    module.Reset();
                    using var client = new ClientWebSocket();
                    client.Options.Proxy = null;
                    await client.ConnectAsync(new Uri(url.Replace("http", "ws", StringComparison.Ordinal) + "socket"), limit.Token);
                    await module.Connected.Task.WaitAsync(limit.Token);
                    if (origin == "message")
                        await client.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("close")), WebSocketMessageType.Text, true, limit.Token);
                    var close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), limit.Token);
                    Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                    Assert.That(close.CloseStatus, Is.EqualTo(WebSocketCloseStatus.PolicyViolation));
                    Assert.That(close.CloseStatusDescription, Is.EqualTo("test"));
                    await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", limit.Token);
                    Assert.That(client.State, Is.EqualTo(WebSocketState.Closed));
                    await module.Disconnected.Task.WaitAsync(limit.Token);
                    Assert.That(module.Disconnects, Is.EqualTo(1));
                }
                using var healthy = new HttpClient(new HttpClientHandler { UseProxy = false });
                Assert.That(await healthy.GetStringAsync(url + "health", limit.Token), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task SendMasked(Stream stream, byte opcode, byte[] data, CancellationToken token)
        {
            var frame = new byte[data.Length + 6];
            frame[0] = (byte)(0x80 | opcode); frame[1] = (byte)(0x80 | data.Length);
            for (var i = 0; i < 4; i++) frame[i + 2] = (byte)(i + 1);
            for (var i = 0; i < data.Length; i++) frame[i + 6] = (byte)(data[i] ^ frame[2 + i % 4]);
            await stream.WriteAsync(frame, token);
        }

        private sealed class ClosingModule : WebSocketModule
        {
            private readonly string _origin;
            public ClosingModule(string origin) : base("/socket", false) => _origin = origin;
            public TaskCompletionSource<IWebSocketContext> Connected { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Disconnected { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Reset()
            {
                Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Disconnects = 0;
                Messages = 0;
            }
            public int Disconnects;
            public int Messages;
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Connected.TrySetResult(context);
                if (_origin == "connected") await context.WebSocket.CloseAsync(CloseStatusCode.PolicyViolation, "test", context.CancellationToken);
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Interlocked.Increment(ref Messages);
                return context.WebSocket.CloseAsync(CloseStatusCode.PolicyViolation, "test", context.CancellationToken);
            }
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref Disconnects); Disconnected.TrySetResult(true); return Task.CompletedTask;
            }
        }
    }
}
