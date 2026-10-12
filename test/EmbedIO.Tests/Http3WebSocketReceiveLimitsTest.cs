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
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase("count")]
        [TestCase("bytes")]
        [TestCase("four-null")]
        public async Task Http3ReceiveLimitsInsideExtendedConnectPreserveCallbacksAndSiblingStreams(string policy)
        {
            // Existing RequireQuic setup fails a required-host prerequisite before
            // ignoring unsupported assets; this guard also scopes platform APIs.
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await Http3ReceiveLimitsWire(policy);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3ReceiveLimitsWire(string policy)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            var module = new WebSocketReceiveTunnelProbe(policy);
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(module).WithModule(new ActionModule("/", HttpVerbs.Get,
                    context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            if (running.IsFaulted) await running;
            Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            await using var connection = await ConnectWebSocketPeerAsync(server, running, new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString(),
                },
            }, stop.Token);
            await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                Assert.That(connection.NegotiatedApplicationProtocol, Is.EqualTo(new SslApplicationProtocol("h3")));
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                await ReadConnectSetting(connection, peerStreams, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                await SendFieldSection(request, new[]
                {
                    (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority),
                    (":path", "/ws"), (":protocol", "websocket"), ("sec-websocket-version", "13"),
                }, false, stop.Token);
                var headers = await ReadResponseFields(request, stop.Token);
                Assert.That(headers[":status"], Is.EqualTo("200"));
                Assert.That(headers["sec-websocket-accept"], Is.Null);
                Assert.That(headers["content-length"], Is.Null);
                using var tunnel = new ClientDataStream(request);
                using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(tunnel,
                    new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
                await module.ExerciseAsync(socket, async () =>
                {
                    if (!QuicConnection.IsSupported) throw new AssertionException("QUIC became unavailable during the owned peer proof.");
                    await HealthySibling(connection, uri.Authority, stop.Token);
                }, async () =>
                {
                    if (!QuicConnection.IsSupported) throw new AssertionException("QUIC became unavailable during the owned tunnel completion.");
                    request.CompleteWrites();
                    Assert.That(await request.ReadAsync(new byte[1], stop.Token), Is.Zero,
                        "A WebSocket policy/normal close must finish only its QUIC stream without resetting the connection.");
                }, stop.Token);
            }
            finally
            {
                module.ReleaseAll();
                stop.Cancel();
                foreach (var peer in peerStreams) await peer.DisposeAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
