using System;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class BrowserRequestProtectionWireTest
    {
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RealProtocolsPreserveSessionTokensAndRejectCrossClientReuse(int protocol)
        {
            if (protocol == 3 && (!QuicConnection.IsSupported))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.Fail("QUIC is required by this job.");
                Assert.Ignore("The host does not provide QUIC."); return;
            }
            var url = Resources.GetServerAddress(); if (protocol > 1) url = url.Replace("http://", "https://", StringComparison.Ordinal);
            using var certificate = protocol > 1 ? HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable) : null;
            using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { url }));
            using var server = (protocol == 1 ? new WebServer(url) : new WebServer(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO, certificate ?? throw new AssertionException("No certificate."), url))
                .WithSessionManager(new LocalSessionManager())
                .WithModule(new ActionModule("/token", HttpVerbs.Get, c => c.SendStringAsync(protection.GetToken(c), "text/plain", WebServer.Utf8NoBomEncoding)))
                .WithModule(new BrowserRequestProtectionModule("/", protection))
                .WithModule(new ActionModule("/", HttpVerbs.Any, c => c.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(); var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) => { if (change.NewState == WebServerState.Listening) listening.TrySetResult(); };
            var running = server.RunAsync(stop.Token);
            using var transport = Transport(); using var otherTransport = Transport(); using var client = Client(transport); using var other = Client(otherTransport);
            try
            {
                await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var token = await client.GetStringAsync("token");
                using (var missing = Request(null)) { using var response = await client.SendAsync(missing); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); }
                using (var good = Request(token)) { using var response = await client.SendAsync(good); Assert.That(response.Version.Major, Is.EqualTo(protocol)); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK)); }
                using (var crossed = Request(token)) { using var response = await other.SendAsync(crossed); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); }
                using (var bad = Request(token)) { bad.Headers.Add("Origin", "http://evil.example"); using var response = await client.SendAsync(bad); Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden)); }
                using (var healthy = Request(token)) { using var response = await client.SendAsync(healthy); Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("accepted")); }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            SocketsHttpHandler Transport() => new() { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => certificate != null && peer?.GetCertHashString() == certificate.GetCertHashString() } };
            HttpClient Client(SocketsHttpHandler handler) => new(handler) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10), DefaultRequestVersion = protocol == 3 ? HttpVersion.Version30 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
            HttpRequestMessage Request(string? token)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/") { Version = client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent("payload") };
                if (token != null) request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token); return request;
            }
        }

        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(0, 2)]
        [TestCase(1, 2)]
        [TestCase(2, 2)]
        public async Task WebSocketHandshakeRequiresTrustedOriginBeforeUpgrade(int origin, int protocol)
        {
            var url = Resources.GetServerAddress(); using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { url }));
            using var socketModule = new Echo(); using var server = new WebServer(url).WithModule(new BrowserRequestProtectionModule("/", protection)).WithModule(socketModule);
            using var stop = new CancellationTokenSource(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); server.StateChanged += (_, change) => { if (change.NewState == WebServerState.Listening) listening.TrySetResult(); };
            var running = server.RunAsync(stop.Token); using var client = new ClientWebSocket();
            using var handler = new SocketsHttpHandler { UseProxy = false }; using var invoker = new HttpMessageInvoker(handler, false);
            client.Options.HttpVersion = protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11;
            client.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            if (origin != 0) client.Options.SetRequestHeader("Origin", origin == 1 ? url.TrimEnd('/') : "http://evil.example");
            try
            {
                await listening.Task.WaitAsync(timeout.Token); var uri = new Uri(url.Replace("http://", "ws://", StringComparison.Ordinal) + "echo");
                if (origin != 1)
                {
                    await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () => await client.ConnectAsync(uri, invoker, timeout.Token)); Assert.That(socketModule.Connected, Is.Zero);
                }
                else
                {
                    await client.ConnectAsync(uri, invoker, timeout.Token); var bytes = Encoding.ASCII.GetBytes("protected"); await client.SendAsync(bytes, WebSocketMessageType.Binary, true, timeout.Token);
                    var received = new byte[32]; var result = await client.ReceiveAsync(received, timeout.Token); Assert.That(received.AsSpan(0, result.Count).ToArray(), Is.EqualTo(bytes));
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token); Assert.That(socketModule.Connected, Is.EqualTo(1));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        private sealed class Echo() : WebSocketModule("/echo", false)
        {
            internal int Connected;
            protected override Task OnClientConnectedAsync(IWebSocketContext context) { Interlocked.Increment(ref Connected); return Task.CompletedTask; }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) => SendAsync(context, buffer);
        }
    }
}
