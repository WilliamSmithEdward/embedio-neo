using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue583_WebSocketCookies
    {
        [TestCase("", false, 0, null)]
        [TestCase("culture=en", false, 1, null)]
        [TestCase("culture=en; token=demo", false, 2, null)]
        [TestCase("culture=en; token=\"left,right\"", false, 2, null)]
        [TestCase("culture=; token=", false, 2, null)]
        [TestCase("culture=en; token=abc==", false, 2, null)]
        [TestCase("culture=en; token=demo", true, 3, null)]
        [TestCase("culture=en; token=demo", false, 2, "echo.v1")]
        public async Task ManagedUpgradeKeepsEachCookieOnItsOwnWireHeader(string cookies, bool createSession, int expectedCount, string? protocol)
        {
            if (cookies is null) throw new System.NullReferenceException();
            var url = Resources.GetServerAddress();
            var module = new EchoSocket(protocol);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            if (createSession) server.WithSessionManager(new LocalSessionManager()).WithModule(new SessionTouch());
            server.WithModule(module).OnGet("/health", context => context.SendStringAsync("healthy", "text/plain", Encoding.UTF8));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var client = new TcpClient();
                var address = new Uri(url);
                await client.ConnectAsync(address.Host, address.Port, timeout.Token);
                var stream = client.GetStream();
                var request = $"GET /echo HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n";
                if (cookies.Length > 0) request += $"Cookie: {cookies}\r\n";
                if (protocol != null) request += $"Sec-WebSocket-Protocol: {protocol}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request + "\r\n"), timeout.Token);
                // Read one byte at a time so a text reader cannot consume WebSocket frame bytes.
                var header = new List<byte>();
                var one = new byte[1];
                while (!Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(one, timeout.Token);
                    header.Add(one[0]);
                    Assert.That(header.Count, Is.LessThan(8192));
                }
                var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                Assert.That(lines[0], Is.EqualTo("HTTP/1.1 101 Switching Protocols"));
                Assert.That(lines, Does.Contain("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo="));
                if (protocol != null) Assert.That(lines, Does.Contain($"Sec-WebSocket-Protocol: {protocol}"));
                var setCookies = lines.Where(line => line.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => line.Substring(line.IndexOf((':').ToString(), System.StringComparison.Ordinal) + 1).Trim()).ToArray();
                var observed = await module.ObservedCookies.Task.WaitAsync(timeout.Token);
                Assert.That(observed.Length, Is.EqualTo(expectedCount));
                if (createSession)
                {
                    var session = setCookies.Single(h => h.StartsWith("__session=", StringComparison.Ordinal));
                    Assert.That(session, Does.Contain("; HttpOnly"));
                    Assert.That(session, Does.Contain("; Path=/"));
                    Assert.That(setCookies.Select(h => Net.CookieList.Parse(h).Single().ToString()), Is.EquivalentTo(observed));
                }
                else
                    Assert.That(setCookies, Is.EquivalentTo(observed), "Repeated Set-Cookie values must never be comma-folded or split on commas.");
                // A masked normal-close frame, retaining an ordinary, accepted connection lifecycle.
                await stream.WriteAsync(new byte[] { 0x88, 0x82, 0, 0, 0, 0, 0x03, 0xe8 }, timeout.Token);
                var close = new byte[4];
                await stream.ReadExactlyAsync(close, timeout.Token);
                Assert.That(close[0], Is.EqualTo(0x88));
                Assert.That(close[2..], Is.EqualTo(new byte[] { 0x03, 0xe8 }));
                using var http = new HttpClient();
                Assert.That(await http.GetStringAsync(url + "health", timeout.Token), Is.EqualTo("healthy"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task CookieBearingUpgradePreservesCookiesAndMessageExchange(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            var module = new EchoSocket();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode)).WithModule(module);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                socket.Options.Cookies = new CookieContainer();
                socket.Options.Cookies.Add(new Uri(url), new Cookie("culture", "en"));
                socket.Options.Cookies.Add(new Uri(url), new Cookie("token", "demo"));
                await socket.ConnectAsync(new Uri(EmbedIO.Internal.StringOperations.ReplaceOrdinal(url, "http://", "ws://") + "echo"), timeout.Token);
                Assert.That(await module.ObservedCookies.Task.WaitAsync(timeout.Token), Is.EquivalentTo(new[] { "culture=en", "token=demo" }));
                var responseCookies = (socket.HttpResponseHeaders ?? throw new AssertionException("Expected upgrade response headers.")).Where(pair => pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(pair => pair.Value).ToArray();
                Assert.That(responseCookies, mode == HttpListenerMode.EmbedIO
                    ? Is.EquivalentTo(new[] { "culture=en", "token=demo" }) : Is.Empty);
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hello")), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[64];
                var reply = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, reply.Count), Is.EqualTo("hello"));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class SessionTouch : WebModuleBase
        {
            public SessionTouch() : base("/") { }
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.Session["test"] = "value";
                return Task.CompletedTask;
            }
        }

        private sealed class EchoSocket : WebSocketModule
        {
            public EchoSocket(string? protocol = null) : base("/echo", false)
            {
                if (protocol != null) AddProtocol(protocol);
            }
            public TaskCompletionSource<string[]> ObservedCookies { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                ObservedCookies.TrySetResult(context.Cookies.Select(cookie => cookie.ToString()).ToArray());
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => SendAsync(context, buffer);
        }
    }
}
