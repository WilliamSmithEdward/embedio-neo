using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2WebSocketTest
    {
        private sealed class Echo : WebSocketModule
        {
            internal int RemotePort;
            internal Echo() : base("/ws", false) { }
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                RemotePort = context.RemoteEndPoint.Port;
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => context.WebSocket.SendAsync(buffer, result.MessageType == (int)WebSocketMessageType.Text, context.CancellationToken);
        }

        private sealed class Limited : WebSocketModule
        {
            internal int Calls;
            internal Limited() : base("/ws", false) => MaxMessageSize = 1024;
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                Interlocked.Increment(ref Calls);
                return context.WebSocket.SendAsync(buffer, false, context.CancellationToken);
            }
        }

        // The managed limit applies inside an RFC 8441 tunnel; rejecting a message
        // closes only that stream.
        [TestCase(false)]
        [TestCase(true)]
        public async Task MaxMessageSizeClosesTheTunnelWith1009AndKeepsSiblingStreamsUsable(bool fragmented)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var module = new Limited();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(module)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = server.RunAsync(stop.Token);
            using var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
            using var invoker = new HttpMessageInvoker(handler, false);
            using var client = new HttpClient(handler, false) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var socket = new ClientWebSocket();
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            try
            {
                await socket.ConnectAsync(new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "ws"), invoker, stop.Token);
                var accepted = new byte[1024];
                await socket.SendAsync(accepted, WebSocketMessageType.Binary, true, stop.Token);
                var echoed = 0;
                WebSocketReceiveResult echo;
                do
                {
                    echo = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[2048]), stop.Token);
                    echoed += echo.Count;
                } while (!echo.EndOfMessage);
                Assert.That(echoed, Is.EqualTo(1024));
                var oversized = new byte[1025];
                if (fragmented)
                {
                    await socket.SendAsync(new ArraySegment<byte>(oversized, 0, 600), WebSocketMessageType.Binary, false, stop.Token);
                    await socket.SendAsync(new ArraySegment<byte>(oversized, 600, 425), WebSocketMessageType.Binary, true, stop.Token);
                }
                else await socket.SendAsync(oversized, WebSocketMessageType.Binary, true, stop.Token);
                var close = await socket.ReceiveAsync(new ArraySegment<byte>(new byte[16]), stop.Token);
                Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
                Assert.That(close.CloseStatus, Is.EqualTo(WebSocketCloseStatus.MessageTooBig));
                Assert.That(await client.GetStringAsync(url + "after", stop.Token), Is.EqualTo("healthy"));
                Assert.That(module.Calls, Is.EqualTo(1));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(262144, true, false, false)]
        [TestCase(262144, true, true, false)]
        [TestCase(0, true, false, false)]
        [TestCase(127, true, false, false)]
        [TestCase(262144, false, false, false)]
        [TestCase(127, true, true, false)]
        [TestCase(262144, false, true, false)]
        [TestCase(127, true, false, true)]
        public async Task ExtendedConnectEchoAndCloseKeepSiblingHttpStreamUsable(int length, bool text, bool fragmented, bool abort)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var echo = new Echo();
            var httpPort = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(echo)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    httpPort = context.RemoteEndPoint.Port;
                    return context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = server.RunAsync(stop.Token);
            using var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
            using var invoker = new HttpMessageInvoker(handler, false);
            using var client = new HttpClient(handler, false) { DefaultRequestVersion = HttpVersion.Version20, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var socket = new ClientWebSocket();
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            try
            {
                await socket.ConnectAsync(new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "ws"), invoker, stop.Token);
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
                var bytes = text ? Encoding.UTF8.GetBytes(new string('x', length)) : Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
                var kind = text ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                if (fragmented)
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes, 0, length / 2), kind, false, stop.Token);
                    await socket.SendAsync(new ArraySegment<byte>(bytes, length / 2, length - length / 2), kind, true, stop.Token);
                }
                else await socket.SendAsync(bytes, kind, true, stop.Token);
                using var received = new MemoryStream();
                var buffer = new byte[4096];
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    Assert.That(result.MessageType, Is.EqualTo(text ? WebSocketMessageType.Text : WebSocketMessageType.Binary));
                    received.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                Assert.That(received.ToArray(), Is.EqualTo(bytes));
                Assert.That(await client.GetStringAsync(url + "during", stop.Token), Is.EqualTo("healthy"));
                Assert.That(httpPort, Is.EqualTo(echo.RemotePort));
                if (abort) socket.Abort();
                else await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token);
                Assert.That(await client.GetStringAsync(url + "after", stop.Token), Is.EqualTo("healthy"));
                Assert.That(httpPort, Is.EqualTo(echo.RemotePort));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
