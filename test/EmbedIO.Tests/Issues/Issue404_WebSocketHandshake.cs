using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue404_WebSocketHandshake
    {
        [TestCase(false, "/register")]
        [TestCase(true, "/register")]
        [TestCase(false, "/register/")]
        [TestCase(true, "/register/")]
        [TestCase(false, "/register?source=dotnet")]
        [TestCase(true, "/register?source=dotnet")]
        public async Task ExactEndpointsUpgradeAndExposeCommittedHttp11Status(bool secure, string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            await using var host = new Fixture(secure);
            var metadata = await host.Exchange(path);
            Assert.That(metadata.Status, Is.EqualTo(101), "The response model must match the successful HTTP/1 wire upgrade.");
            Assert.That(metadata.Reason, Is.EqualTo("Switching Protocols"));
            Assert.That(metadata.Session, Is.True);
            Assert.That(await host.Health(), Is.EqualTo("healthy"));
        }

        [TestCase(false, "/register/extra")]
        [TestCase(true, "/register/extra")]
        [TestCase(false, "/register/extra/")]
        [TestCase(true, "/register/extra/")]
        public async Task UnregisteredDescendantsAreNotClaimedAsEmptySuccessfulResponses(bool secure, string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            await using var host = new Fixture(secure);
            Assert.That(await host.RawStatus(path), Is.EqualTo(404));
            using var rejected = host.Socket(false);
            await Assert.ThatAsync(async () => await rejected.ConnectAsync(host.WebSocketUrl(path), host.Token), Throws.TypeOf<System.Net.WebSockets.WebSocketException>());
            Assert.That(host.Primary.Connections, Is.Zero);
            await host.Exchange("/register");
            Assert.That(await host.Health(), Is.EqualTo("healthy"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NestedWebSocketModuleReceivesItsOwnHandshake(bool secure)
        {
            await using var host = new Fixture(secure, "nested");
            await host.Exchange("/register/child");
            Assert.That(host.Primary.Connections, Is.Zero);
            Assert.That(host.Child?.Connections, Is.EqualTo(1));
            await host.Exchange("/register");
            Assert.That(host.Primary.Connections, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task DescendantsCanReachAnExplicitOrdinaryHttpHandler(bool secure)
        {
            await using var host = new Fixture(secure, "http-child");
            Assert.That(await host.Http.GetStringAsync(host.Url + "register/child", host.Token), Is.EqualTo("child"));
            Assert.That(await host.RawStatus("/register/child"), Is.EqualTo(200), "An explicitly configured ordinary HTTP route remains HTTP.");
            Assert.That(host.Primary.Connections, Is.Zero);
            await host.Exchange("/register");
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ExplicitModuleOrderControlsEarlierOrdinaryHttpResponses(bool secure, bool shadow)
        {
            await using var host = new Fixture(secure, shadow ? "shadow" : "fallback");
            if (shadow)
            {
                Assert.That(await host.RawStatus("/register"), Is.EqualTo(200));
                using var client = host.Socket(false);
                await Assert.ThatAsync(async () => await client.ConnectAsync(host.WebSocketUrl("/register"), host.Token), Throws.TypeOf<System.Net.WebSockets.WebSocketException>());
                Assert.That(host.Primary.Connections, Is.Zero);
            }
            else
            {
                await host.Exchange("/register");
                Assert.That(host.Primary.Connections, Is.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OrdinaryGetAtTheSocketEndpointIsRejectedAsBadHandshake(bool secure)
        {
            await using var host = new Fixture(secure);
            using var response = await host.Http.GetAsync(host.Url + "register", host.Token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(host.Primary.Connections, Is.Zero);
            await host.Exchange("/register");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SimilarPrefixIsOutsideTheEndpointBoundary(bool secure)
        {
            await using var host = new Fixture(secure);
            Assert.That(await host.RawStatus("/registering"), Is.EqualTo(404));
            Assert.That(host.Primary.Connections, Is.Zero);
            await host.Exchange("/register");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http2ExtendedConnectRetainsSuccessful200Status(bool secure)
        {
            await using var host = new Fixture(secure);
            var metadata = await host.Exchange("/register", true);
            Assert.That(metadata.Status, Is.EqualTo(200), "RFC 8441 CONNECT uses 200, not HTTP/1's 101.");
            Assert.That(await host.Health(), Is.EqualTo("healthy"));
        }

        private sealed class Snapshot
        {
            internal Snapshot(IHttpContext context)
            {
                Status = context.Response.StatusCode;
                Reason = context.Response.StatusDescription;
                Session = context.Session.Exists;
            }
            internal int Status { get; }
            internal string? Reason { get; }
            internal bool Session { get; }
        }

        private sealed class Observe : WebModuleBase
        {
            internal Observe() : base("/") { }
            internal ConcurrentDictionary<string, IHttpContext> Contexts { get; } = new();
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                Contexts[context.Id] = context;
                context.Session["seen"] = true;
                return Task.CompletedTask;
            }
        }

        private sealed class Echo : WebSocketModule
        {
            private readonly Observe _observe;
            internal Echo(string path, Observe observe) : base(path, false) => _observe = observe;
            internal int Connections;
            internal ConcurrentQueue<Snapshot> Accepted { get; } = new();
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                Interlocked.Increment(ref Connections);
                Accepted.Enqueue(new Snapshot(_observe.Contexts[context.HttpContextId]));
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => result.MessageType == (int)WebSocketMessageType.Text
                    ? SendAsync(context, Encoding.UTF8.GetString(buffer)) : SendAsync(context, buffer);
        }

        private sealed class Fixture : IAsyncDisposable
        {
            private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
            private readonly CancellationTokenSource _stop = new();
            private readonly X509Certificate2? _certificate;
            private readonly SocketsHttpHandler _handler;
            private readonly HttpMessageInvoker _invoker;
            private readonly WebServer _server;
            private readonly Task _running;
            internal Fixture(bool secure, string shape = "normal")
            {
                Url = new UriBuilder(Resources.GetServerAddress()) { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
                _certificate = secure ? HttpsSmoke.CreateCertificate() : null;
                _handler = new SocketsHttpHandler { UseProxy = false };
                if (_certificate != null) _handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, _) => Trusted(c);
                _invoker = new HttpMessageInvoker(_handler, false);
                Http = new HttpClient(_handler, false);
                var options = new WebServerOptions().WithUrlPrefix(Url.ToString()).WithMode(HttpListenerMode.EmbedIO);
                if (_certificate != null) options.WithCertificate(_certificate);
                var observe = new Observe();
                Primary = new Echo("/register", observe);
                _server = new WebServer(options).WithLocalSessionManager().WithModule(observe);
                static ActionModule Ordinary() => new("/", HttpVerbs.Any, c => c.SendStringAsync("ordinary", "text/plain", Encoding.UTF8));
                if (shape == "shadow") _server.WithModule(Ordinary());
                _server.WithModule(Primary);
                if (shape == "nested")
                {
                    Child = new Echo("/register/child", observe);
                    _server.WithModule(Child);
                }
                if (shape == "http-child") _server.WithModule(new ActionModule("/register/child", HttpVerbs.Any, c => c.SendStringAsync("child", "text/plain", Encoding.UTF8)));
                if (shape == "fallback") _server.WithModule(Ordinary());
                _server.OnGet("/health", c => c.SendStringAsync("healthy", "text/plain", Encoding.UTF8));
                _running = _server.RunAsync(_stop.Token);
            }
            internal Uri Url { get; }
            internal Echo Primary { get; }
            internal Echo? Child { get; }
            internal HttpClient Http { get; }
            internal CancellationToken Token => _deadline.Token;
            internal Uri WebSocketUrl(string path) => new UriBuilder(new Uri(Url, path)) { Scheme = _certificate == null ? "ws" : "wss" }.Uri;
            private bool Trusted(X509Certificate? certificate) => certificate != null && _certificate != null
                && certificate.GetRawCertData().AsSpan().SequenceEqual(_certificate.RawData);
            internal ClientWebSocket Socket(bool http2)
            {
                var socket = new ClientWebSocket();
                socket.Options.HttpVersion = http2 ? HttpVersion.Version20 : HttpVersion.Version11;
                socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                if (!http2 && _certificate != null) socket.Options.RemoteCertificateValidationCallback = (_, c, _, _) => Trusted(c);
                return socket;
            }
            internal async Task<Snapshot> Exchange(string path, bool http2 = false)
            {
                using var socket = Socket(http2);
                if (http2) await socket.ConnectAsync(WebSocketUrl(path), _invoker, Token);
                else await socket.ConnectAsync(WebSocketUrl(path), Token);
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hello-404")), WebSocketMessageType.Text, true, Token);
                var buffer = new byte[64];
                var reply = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), Token);
                Assert.That(reply.MessageType, Is.EqualTo(WebSocketMessageType.Text));
                Assert.That(reply.EndOfMessage, Is.True);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, reply.Count), Is.EqualTo("hello-404"));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Token);
                var endpoint = path.StartsWith("/register/child", StringComparison.Ordinal) ? Child : Primary;
                while (endpoint?.Accepted.IsEmpty == true)
                {
                    Token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (endpoint == null || !endpoint.Accepted.TryDequeue(out var result)) throw new AssertionException("Missing acceptance metadata.");
                return result;
            }
            internal async Task<int> RawStatus(string path)
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(Url.Host, Url.Port, Token);
                using Stream stream = _certificate == null ? peer.GetStream() : new SslStream(peer.GetStream(), false, (_, c, _, _) => Trusted(c));
                if (stream is SslStream tls) await tls.AuthenticateAsClientAsync("localhost").WaitAsync(Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {Url.Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"), Token);
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(head.Length, Is.LessThan(8192));
                    if (await stream.ReadAsync(one, Token) == 0) throw new IOException("Unexpected response EOF.");
                    head.Append((char)one[0]);
                }
                return int.Parse(head.ToString().Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            internal Task<string> Health() => Http.GetStringAsync(Url + "health", Token);
            public async ValueTask DisposeAsync()
            {
                try
                {
                    _stop.Cancel();
                    await _running.WaitAsync(TimeSpan.FromSeconds(10));
                }
                finally
                {
                    _server.Dispose();
                    Http.Dispose();
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
