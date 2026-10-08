using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class NativeWebSocketShutdownTest
    {
        private sealed class PendingInitialization : WebSocketModule
        {
            internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal PendingInitialization() : base("/ws", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Entered.TrySetResult(true);
                await Release.Task;
            }
        }

        [Test]
        public async Task ShutdownDoesNotSerializeAnotherHttpResponseAfterUpgrade()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var module = new PendingInitialization();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.Microsoft)).WithModule(module);
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, timeout.Token);
                var stream = client.GetStream();
                var request = $"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), timeout.Token);
                var headers = new StringBuilder();
                var single = new byte[1];
                while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(headers.Length, Is.LessThan(16384));
                    await stream.ReadExactlyAsync(single, timeout.Token);
                    headers.Append((char)single[0]);
                }
                Assert.That(headers.ToString(), Does.StartWith("HTTP/1.1 101"));
                await module.Entered.Task.WaitAsync(timeout.Token);
                stop.Cancel();
                await running.WaitAsync(timeout.Token);
                using var trailing = new MemoryStream();
                try { await stream.CopyToAsync(trailing, timeout.Token); }
                catch (IOException error) when (OperatingSystem.IsWindows()
                    && error.InnerException is SocketException socketError
                    && socketError.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // HTTP.sys aborts upgraded transports on Stop. Any bytes read
                    // before that reset must still satisfy the wire assertion.
                }
                Assert.That(Encoding.ASCII.GetString(trailing.ToArray()), Is.Empty,
                    "A stopped upgraded transport must not append an HTTP response to the WebSocket wire.");
            }
            finally
            {
                module.Release.TrySetResult(true);
                stop.Cancel();
                await running.WaitAsync(timeout.Token);
            }
        }
    }
}
