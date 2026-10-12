using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue401_StartupSubscription
    {
        [TestCase(false, false, 0, Category = "StartupHttp1")]
        [TestCase(false, false, 1, Category = "StartupHttp1")]
        [TestCase(false, false, 2, Category = "StartupHttp1")]
        [TestCase(false, false, 3, Category = "StartupHttp1")]
        [TestCase(true, false, 0, Category = "StartupHttp1")]
        [TestCase(true, false, 1, Category = "StartupHttp1")]
        [TestCase(true, false, 2, Category = "StartupHttp1")]
        [TestCase(true, false, 3, Category = "StartupHttp1")]
        [TestCase(false, true, 0, Category = "StartupHttp2")]
        [TestCase(false, true, 1, Category = "StartupHttp2")]
        [TestCase(false, true, 2, Category = "StartupHttp2")]
        [TestCase(false, true, 3, Category = "StartupHttp2")]
        [TestCase(true, true, 0, Category = "StartupHttp2")]
        [TestCase(true, true, 1, Category = "StartupHttp2")]
        [TestCase(true, true, 2, Category = "StartupHttp2")]
        [TestCase(true, true, 3, Category = "StartupHttp2")]
        public async Task InitialApplicationHandshakeIsRetainedBeforeSubscription(bool secure, bool http2, int shape)
        {
            await using var host = new Fixture(secure, http2);
            using var client = host.Client();
            await host.Connect(client);
            var expected = Payloads("client");
            await SendBurst(client, expected, shape, host.Token);
            // Observe actual transport receipt, not merely completion of client sends.
            await host.Module.WaitForTransportMessages(1, expected.Length, host.Token);
            Assert.That(host.Module.Delivered, Is.Zero, "Initialization is still pending; application callbacks must not run yet.");
            host.Module.Release.TrySetResult();
            await ReceiveBurst(client, expected, shape, host.Token);
            Assert.That(host.Module.Transcripts.Values.Single().ToArray(), Is.EqualTo(expected));
            Assert.That(host.Module.Errors, Is.Empty);
            await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", host.Token);
        }

        [TestCase(false, false, false, Category = "StartupHttp1")]
        [TestCase(false, false, true, Category = "StartupHttp1")]
        [TestCase(true, false, false, Category = "StartupHttp1")]
        [TestCase(true, false, true, Category = "StartupHttp1")]
        [TestCase(false, true, false, Category = "StartupHttp2")]
        [TestCase(false, true, true, Category = "StartupHttp2")]
        [TestCase(true, true, false, Category = "StartupHttp2")]
        [TestCase(true, true, true, Category = "StartupHttp2")]
        public async Task IndependentClientsKeepTheirOwnInitialProtocolMessages(bool secure, bool http2, bool binary)
        {
            await using var host = new Fixture(secure, http2);
            using var first = host.Client();
            using var second = host.Client();
            await Task.WhenAll(host.Connect(first), host.Connect(second));
            var one = Payloads("one");
            var two = Payloads("two");
            var shape = binary ? 1 : 0;
            await Task.WhenAll(SendBurst(first, one, shape, host.Token), SendBurst(second, two, shape, host.Token));
            await host.Module.WaitForTransportMessages(2, one.Length, host.Token);
            Assert.That(host.Module.Delivered, Is.Zero);
            host.Module.Release.TrySetResult();
            await Task.WhenAll(ReceiveBurst(first, one, shape, host.Token), ReceiveBurst(second, two, shape, host.Token));
            var transcripts = host.Module.Transcripts.Values.Select(q => q.ToArray()).ToArray();
            Assert.That(transcripts.Length, Is.EqualTo(2));
            Assert.That(transcripts.Single(t => t[0] == one[0]), Is.EqualTo(one));
            Assert.That(transcripts.Single(t => t[0] == two[0]), Is.EqualTo(two));
            Assert.That(host.Module.Errors, Is.Empty);
            await Task.WhenAll(first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", host.Token), second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", host.Token));
        }

        private static string[] Payloads(string client)
            => Enumerable.Range(0, 8).Select(i => i == 0 ? "AUTH:" + client : "DATA:" + client + ":" + i).ToArray();

        private static async Task SendBurst(ClientWebSocket socket, string[] messages, int shape, CancellationToken token)
        {
            var type = shape % 2 == 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
            foreach (var message in messages)
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                if (shape >= 2)
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes, 0, 2), type, false, token);
                    await socket.SendAsync(new ArraySegment<byte>(bytes, 2, bytes.Length - 2), type, true, token);
                }
                else await socket.SendAsync(new ArraySegment<byte>(bytes), type, true, token);
            }
        }

        private static async Task ReceiveBurst(ClientWebSocket socket, string[] messages, int shape, CancellationToken token)
        {
            var type = shape % 2 == 0 ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
            var buffer = new byte[128];
            foreach (var expected in messages)
            {
                var count = 0;
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), token);
                    Assert.That(received.MessageType, Is.EqualTo(type));
                    count += received.Count;
                    Assert.That(count, Is.LessThan(buffer.Length));
                } while (!received.EndOfMessage);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, count), Is.EqualTo(expected));
            }
        }

        private sealed class InitializingModule : WebSocketModule
        {
            private readonly ConcurrentDictionary<string, IWebSocketContext> _pending = new();
            internal InitializingModule() : base("/socket", false) { }
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ConcurrentDictionary<string, ConcurrentQueue<string>> Transcripts { get; } = new();
            internal ConcurrentQueue<string> Errors { get; } = new();
            internal int Delivered => Transcripts.Values.Sum(q => q.Count);
            protected override async Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Transcripts[context.Id] = new ConcurrentQueue<string>();
                _pending[context.Id] = context;
                await Release.Task.WaitAsync(context.CancellationToken);
                context.Items["ready"] = true;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
            {
                if (!context.Items.ContainsKey("ready")) Errors.Enqueue("Message callback preceded connection initialization.");
                Transcripts[context.Id].Enqueue(Encoding.UTF8.GetString(buffer));
                return result.MessageType == (int)WebSocketMessageType.Text
                    ? SendAsync(context, Encoding.UTF8.GetString(buffer)) : SendAsync(context, buffer);
            }
            internal async Task WaitForTransportMessages(int clients, int messages, CancellationToken token)
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var contexts = _pending.Values.ToArray();
                    if (contexts.Length == clients && contexts.All(c => Queued(c) == messages)) return;
                    await Task.Delay(1, token);
                }
            }
            private static int Queued(IWebSocketContext context)
            {
                var queue = context.WebSocket.GetType().GetField("_messageEventQueue", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context.WebSocket)
                    ?? throw new AssertionException("Missing managed transport message queue.");
                return queue.GetType().GetProperty("Count")?.GetValue(queue) as int?
                    ?? throw new AssertionException("Missing queued-message count.");
            }
        }

        private sealed class Fixture : IAsyncDisposable
        {
            private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
            private readonly CancellationTokenSource _stop = new();
            private readonly X509Certificate2? _certificate;
            private readonly SocketsHttpHandler _handler;
            private readonly HttpMessageInvoker _invoker;
            private readonly bool _http2;
            private readonly WebServer _server;
            private readonly Task _running;
            internal Fixture(bool secure, bool http2)
            {
                var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
                SocketUrl = new UriBuilder(new Uri(url, "socket")) { Scheme = secure ? "wss" : "ws" }.Uri;
                _certificate = secure ? HttpsSmoke.CreateCertificate() : null;
                _http2 = http2;
                _handler = new SocketsHttpHandler { UseProxy = false };
                if (_certificate != null) _handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, _) => c != null && c.GetRawCertData().AsSpan().SequenceEqual(_certificate.RawData);
                _invoker = new HttpMessageInvoker(_handler, false);
                var options = new WebServerOptions().WithUrlPrefix(url.ToString()).WithMode(HttpListenerMode.EmbedIO);
                if (_certificate != null) options.WithCertificate(_certificate);
                Module = new InitializingModule();
                _server = new WebServer(options).WithModule(Module);
                _running = _server.RunAsync(_stop.Token);
            }
            internal InitializingModule Module { get; }
            internal Uri SocketUrl { get; }
            internal CancellationToken Token => _deadline.Token;
            internal ClientWebSocket Client()
            {
                var socket = new ClientWebSocket();
                socket.Options.HttpVersion = _http2 ? System.Net.HttpVersion.Version20 : System.Net.HttpVersion.Version11;
                socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                if (!_http2 && _certificate != null) socket.Options.RemoteCertificateValidationCallback = (_, c, _, _) => c != null && c.GetRawCertData().AsSpan().SequenceEqual(_certificate.RawData);
                return socket;
            }
            internal Task Connect(ClientWebSocket socket)
                => _http2 ? socket.ConnectAsync(SocketUrl, _invoker, Token) : socket.ConnectAsync(SocketUrl, Token);
            public async ValueTask DisposeAsync()
            {
                Module.Release.TrySetResult();
                try
                {
                    _stop.Cancel();
                    await _running.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    _server.Dispose();
                    _invoker.Dispose();
                    _handler.Dispose();
                    _certificate?.Dispose();
                    _stop.Dispose();
                    _deadline.Dispose();
                }
            }
        }
    }
}
