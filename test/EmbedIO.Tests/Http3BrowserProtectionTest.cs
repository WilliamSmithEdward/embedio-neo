using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task BrowserRequestProtectionChecksExtendedWebSocketOrigin(int origin)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await BrowserWebSocketWire(origin);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task BrowserWebSocketWire(int origin)
        {
            using var certificate = Certificate(); var prefix = Prefix(); var uri = new Uri(prefix);
            using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { prefix }));
            var echo = new Http3Echo();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new BrowserRequestProtectionModule("/", protection)).WithModule(echo)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var running = server.RunAsync(stop.Token);
            if (running.IsFaulted) await running;
            await using var connection = await ConnectWebSocketPeerAsync(server, running, new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                DefaultStreamErrorCode = 0x10c,
                DefaultCloseErrorCode = 0x100,
                MaxInboundUnidirectionalStreams = 8,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions { TargetHost = "localhost", ApplicationProtocols = new List<SslApplicationProtocol> { new("h3") }, RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() }
            }, stop.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token); var peers = new List<QuicStream>();
            try
            {
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token); await ReadConnectSetting(connection, peers, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                var fields = new List<(string Name, string Value)>
                {
                    (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/ws"), (":protocol", "websocket"),
                    ("sec-websocket-version", "13"), ("sec-websocket-protocol", "echo"), ("cookie", "test=value")
                };
                if (origin != 0) fields.Add(("origin", origin == 1 ? prefix.TrimEnd('/') : "https://evil.example"));
                await SendFieldSection(request, fields.ToArray(), false, stop.Token);
                Assert.That((await ReadResponseFields(request, stop.Token))[":status"], Is.EqualTo(origin == 1 ? "200" : "403"));
                if (origin == 1)
                {
                    using var tunnel = new ClientDataStream(request);
                    using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(tunnel, new WebSocketCreationOptions { IsServer = false, SubProtocol = "echo", KeepAliveInterval = Timeout.InfiniteTimeSpan });
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token); request.CompleteWrites(); await echo.Disconnected.Task.WaitAsync(stop.Token);
                }
                else { request.CompleteWrites(); Assert.That(echo.DisconnectCount, Is.Zero); }
                await HealthySibling(connection, uri.Authority, stop.Token);
            }
            finally { stop.Cancel(); foreach (var peer in peers) await peer.DisposeAsync(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
