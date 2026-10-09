using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
    public class ManagedWebSocketHandshakeTest
    {
        private const string Key = "dGhlIHNhbXBsZSBub25jZQ==";
        private sealed class Probe : WebSocketModule
        {
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => Task.CompletedTask;
            internal int Connections;
            private readonly TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task WaitForConnectionsAsync(int count, CancellationToken token) =>
                (count == 1 ? _first.Task : _second.Task).WaitAsync(token);
            internal Probe(bool protocol) : base("/ws", false) { if (protocol) AddProtocol("echo"); }
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                var count = Interlocked.Increment(ref Connections);
                if (count == 1) _first.TrySetResult(true);
                else if (count == 2) _second.TrySetResult(true);
                return Task.CompletedTask;
            }
        }
        private static IEnumerable<TestCaseData> InvalidRequests()
        {
            foreach (var kind in new[] { "missing-key", "empty-key", "invalid-key", "short-key", "long-key", "padding-key", "space-key", "comma-key", "duplicate-key", "replaced-key", "post", "http10", "missing-upgrade", "wrong-upgrade", "missing-connection", "wrong-connection", "substring-connection", "missing-version", "empty-version", "old-version", "list-version", "duplicate-version" })
                yield return new TestCaseData(kind).SetName("InvalidHandshake_" + kind);
        }
        [TestCaseSource(nameof(InvalidRequests))]
        public async Task InvalidHandshakeDoesNotConnectApplication(string kind)
        {
            ArgumentNullException.ThrowIfNull(kind);
            await Exercise(kind, false);
        }
        [TestCase("ordinary")]
        [TestCase("zero-key")]
        [TestCase("ows-key")]
        [TestCase("connection-list")]
        [TestCase("upgrade-list")]
        [TestCase("duplicate-connection")]
        [TestCase("duplicate-upgrade")]
        [TestCase("duplicate-protocol-fields")]
        public async Task ValidHandshakePreservesRequiredAcceptAndListSemantics(string kind)
        {
            ArgumentNullException.ThrowIfNull(kind);
            await Exercise(kind, true);
        }
        private static async Task Exercise(string kind, bool valid)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var protocol = kind == "duplicate-protocol-fields";
            var probe = new Probe(protocol);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(probe).WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using (var tcp = new TcpClient())
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, stop.Token);
                    var stream = tcp.GetStream();
                    var method = kind == "post" ? "POST" : "GET";
                    var version = kind == "http10" ? "1.0" : "1.1";
                    var key = kind switch
                    {
                        "missing-key" => "",
                        "empty-key" => "Sec-WebSocket-Key: \r\n",
                        "invalid-key" => "Sec-WebSocket-Key: ?GhlIHNhbXBsZSBub25jZQ==\r\n",
                        "short-key" => "Sec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAA\r\n",
                        "long-key" => "Sec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAAAAA=\r\n",
                        "padding-key" => "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ\r\n",
                        "space-key" => "Sec-WebSocket-Key: dGhl IHNhbXBsZSBub25jZQ==\r\n",
                        "comma-key" => $"Sec-WebSocket-Key: {Key}, {Key}\r\n",
                        "duplicate-key" => $"Sec-WebSocket-Key: {Key}\r\nsEc-WeBsOcKeT-kEy: {Key}\r\n",
                        "replaced-key" => $"Sec-WebSocket-Key: bad\r\nSec-WebSocket-Key: {Key}\r\n",
                        "zero-key" => "Sec-WebSocket-Key: AAAAAAAAAAAAAAAAAAAAAA==\r\n",
                        "ows-key" => $"Sec-WebSocket-Key: \t{Key} \t\r\n",
                        _ => $"Sec-WebSocket-Key: {Key}\r\n"
                    };
                    var upgrade = kind switch
                    {
                        "missing-upgrade" => "",
                        "wrong-upgrade" => "Upgrade: h2c\r\n",
                        "upgrade-list" => "Upgrade: h2c, WebSocket\r\n",
                        "duplicate-upgrade" => "Upgrade: WebSocket\r\nUpgrade: h2c\r\n",
                        _ => "Upgrade: websocket\r\n"
                    };
                    var connection = kind switch
                    {
                        "missing-connection" => "",
                        "wrong-connection" => "Connection: keep-alive\r\n",
                        "substring-connection" => "Connection: notUpgrade\r\n",
                        "connection-list" => "Connection: keep-alive, uPgRaDe\r\n",
                        "duplicate-connection" => "Connection: Upgrade\r\nConnection: keep-alive\r\n",
                        _ => "Connection: Upgrade\r\n"
                    };
                    var wsVersion = kind switch
                    {
                        "missing-version" => "",
                        "empty-version" => "Sec-WebSocket-Version: \r\n",
                        "old-version" => "Sec-WebSocket-Version: 12\r\n",
                        "list-version" => "Sec-WebSocket-Version: 13, 13\r\n",
                        "duplicate-version" => "Sec-WebSocket-Version: 13\r\nSec-WebSocket-Version: 13\r\n",
                        _ => "Sec-WebSocket-Version: 13\r\n"
                    };
                    var protocols = protocol ? "Sec-WebSocket-Protocol: echo\r\nSec-WebSocket-Protocol: other\r\n" : "";
                    var request = $"{method} /ws HTTP/{version}\r\nHost: {new Uri(url).Authority}\r\nContent-Length: 0\r\n{connection}{upgrade}{key}{wsVersion}{protocols}\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(request), stop.Token);
                    var response = await ReadHeaders(stream, stop.Token);
                    Assert.That(response, Does.StartWith(valid ? "HTTP/1.1 101" : $"HTTP/{version} 400"));
                    if (valid)
                    {
                        var expected = kind == "zero-key" ? "ICX+Yqv66kxgM0FcWaLWlFLwTAI=" : "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=";
                        Assert.That(response, Does.Contain("Sec-WebSocket-Accept: " + expected));
                        if (protocol) Assert.That(response, Does.Contain("Sec-WebSocket-Protocol: echo"));
                        await probe.WaitForConnectionsAsync(1, stop.Token);
                        await stream.WriteAsync(new byte[] { 0x88, 0x82, 0, 0, 0, 0, 3, 232 }, stop.Token);
                        var close = new byte[4]; await stream.ReadExactlyAsync(close, stop.Token);
                        Assert.That(close, Is.EqualTo(new byte[] { 0x88, 2, 3, 232 }));
                    }
                    else
                    {
                        Assert.That(Volatile.Read(ref probe.Connections), Is.Zero);
                        Assert.That(response, Does.Not.Contain("Sec-WebSocket-Accept:"));
                        if (kind is "missing-version" or "empty-version" or "old-version" or "list-version")
                            Assert.That(response, Does.Contain("Sec-WebSocket-Version: 13"));
                    }
                }
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await http.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                using var healthy = new ClientWebSocket();
                if (protocol) healthy.Options.AddSubProtocol("echo");
                await healthy.ConnectAsync(new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "ws"), stop.Token);
                // Wire handshake completion does not await the application's
                // independently scheduled connection callback.
                await probe.WaitForConnectionsAsync(valid ? 2 : 1, stop.Token);
                await healthy.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token);
                Assert.That(Volatile.Read(ref probe.Connections), Is.EqualTo(valid ? 2 : 1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        private static async Task<string> ReadHeaders(Stream stream, CancellationToken token)
        {
            var result = new StringBuilder(); var single = new byte[1];
            while (!result.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.That(result.Length, Is.LessThan(16384));
                await stream.ReadExactlyAsync(single, token); result.Append((char)single[0]);
            }
            return result.ToString();
        }
    }
}
