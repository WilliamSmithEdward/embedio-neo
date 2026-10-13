using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private sealed class DrainHub : WebSocketModule
        {
            internal readonly TaskCompletionSource Connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int DisconnectCount;
            internal DrainHub(bool closeOnDrain) : base("/ws", false) => CloseOnDrain = closeOnDrain;
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            { Connected.TrySetResult(); return Task.CompletedTask; }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Task.CompletedTask;
            protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
            { Interlocked.Increment(ref DisconnectCount); Disconnected.TrySetResult(); return Task.CompletedTask; }
        }

        // Over HTTP/3, the drain's 1001 reaches the WebSocket through its extended CONNECT stream; by default the
        // drain waits for the stream until its deadline instead.
        [TestCase(true)]
        [TestCase(false)]
        public async Task Http3DrainClosesWebSocketsOnlyWhenTheModuleAsks(bool closeOnDrain)
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }
            await Http3WebSocketDrainWire(closeOnDrain);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3WebSocketDrainWire(bool closeOnDrain)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            var hub = new DrainHub(closeOnDrain);
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(hub)
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var running = server.RunAsync(stop.Token);
            if (running.IsFaulted) await running;
            var connection = await ConnectWebSocketPeerAsync(server, running, new QuicClientConnectionOptions
            {
                RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() },
            }, stop.Token);
            var peerStreams = new List<QuicStream>();
            try
            {
                await using var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token);
                await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                await ReadConnectSetting(connection, peerStreams, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                await SendFieldSection(request, new[]
                {
                    (":method", "CONNECT"), (":scheme", "https"), (":authority", uri.Authority), (":path", "/ws"), (":protocol", "websocket"),
                    ("sec-websocket-version", "13"),
                }, false, stop.Token);
                Assert.That((await ReadResponseFields(request, stop.Token))[":status"], Is.EqualTo("200"));
                using var tunnel = new ClientDataStream(request);
                using var socket = System.Net.WebSockets.WebSocket.CreateFromStream(tunnel,
                    new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan });
                await hub.Connected.Task.WaitAsync(stop.Token);

                var deadline = closeOnDrain ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1.5);
                var clock = Stopwatch.StartNew();
                var drain = server.DrainAsync(deadline, stop.Token);
                var receiving = socket.ReceiveAsync(new ArraySegment<byte>(new byte[256]), stop.Token);
                if (!closeOnDrain)
                {
                    // Nothing closes the socket before the deadline; what follows at the deadline depends on timing.
                    await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                    Assert.That(receiving.IsCompleted, Is.False, "The WebSocket stayed open before the deadline.");
                }

                WebSocketCloseStatus? status = null;
                try
                {
                    var result = await receiving.WaitAsync(TimeSpan.FromSeconds(10));
                    status = result.MessageType == WebSocketMessageType.Close ? result.CloseStatus : null;
                }
                catch (Exception error) when (error is System.Net.WebSockets.WebSocketException or IOException or QuicException)
                {
                    // Cut without a close frame.
                }

                if (closeOnDrain)
                {
                    Assert.That(status, Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ok", stop.Token);
                    request.CompleteWrites();
                    await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    // HTTP/3 drain also waits for the client to close the connection after its GOAWAY.
                    await connection.CloseAsync(0x100, stop.Token);
                    await drain.WaitAsync(TimeSpan.FromSeconds(15));
                    clock.Stop();
                    Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(12)), "The drain ended without waiting for its deadline.");
                }
                else
                {
                    await drain.WaitAsync(TimeSpan.FromSeconds(15));
                    clock.Stop();
                    Assert.That(clock.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.3)), "Drain waited for the WebSocket until its deadline.");
                }

                await hub.Disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(hub.DisconnectCount, Is.EqualTo(1));
            }
            finally
            {
                stop.Cancel();
                foreach (var peer in peerStreams) await peer.DisposeAsync();
                await connection.DisposeAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
