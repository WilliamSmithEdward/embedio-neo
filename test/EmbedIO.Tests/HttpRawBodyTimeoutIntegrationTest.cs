using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public async Task RawBodyUnreadCleanupDeadlinePreservesOwnedResponseAndHealthyTransport(int protocol, bool committed)
        {
            if (protocol == 3)
            {
                RequireQuic();
                if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            }
            var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var formatted = 0;
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                protocol == 3 ? certificate : null, async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    { await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding); return; }
                    if (committed)
                    {
                        context.Response.ContentLength64 = 10;
                        await context.Response.OutputStream.WriteAsync(new byte[] { (byte)'x' }, context.CancellationToken);
                        await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                        await resume.Task.WaitAsync(context.CancellationToken);
                    }
                    // The handler deliberately does not read the entity: cleanup
                    // must enforce both the raw policy and body-read deadline.
                    context.SetHandled();
                }, options => options.WithMaximumRequestBodyBytes(2).WithRequestBodyReadTimeout(TimeSpan.FromMilliseconds(200)),
                server => server.OnHttpException = async (context, error) =>
                {
                    Interlocked.Increment(ref formatted);
                    await HttpExceptionHandler.PlainTextResponse(context, error);
                });
            try
            {
                if (protocol == 1)
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
                    var wire = tcp.GetStream();
                    await wire.WriteAsync(Encoding.ASCII.GetBytes(
                        $"POST /upload HTTP/1.1\r\nHost: localhost:{host.Port}\r\nContent-Length: 1\r\n\r\n"), host.Token);
                    if (!committed)
                    {
                        var response = await new RawHttp1Reader(wire).ReadResponseAsync(host.Token);
                        Assert.That(response.Status, Is.EqualTo(408));
                        Assert.That(Encoding.UTF8.GetString(response.Body), Does.Contain("within the configured time"));
                        Assert.That(response.Headers["connection"], Is.EqualTo("close"));
                    }
                    else
                    {
                        using var head = new MemoryStream();
                        var one = new byte[1];
                        while (true)
                        {
                            Assert.That(await wire.ReadAsync(one, host.Token), Is.EqualTo(1));
                            head.WriteByte(one[0]);
                            if (head.Length >= 4 && Encoding.ASCII.GetString(head.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                        }
                        Assert.That(Encoding.ASCII.GetString(head.ToArray()), Does.StartWith("HTTP/1.1 200"));
                        Assert.That(await wire.ReadAsync(one, host.Token), Is.EqualTo(1));
                        Assert.That(one[0], Is.EqualTo((byte)'x'));
                        resume.TrySetResult(true);
                        // A truncated committed response must end, never receive
                        // a replacement408 or a successful ten-byte body.
                        try { Assert.That(await wire.ReadAsync(one, host.Token), Is.Zero); }
                        catch (IOException) { }
                    }
                    using var successor = new TcpClient();
                    await successor.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
                    await successor.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                        $"GET /healthy HTTP/1.1\r\nHost: localhost:{host.Port}\r\nConnection: close\r\n\r\n"), host.Token);
                    var healthy = await new RawHttp1Reader(successor.GetStream()).ReadResponseAsync(host.Token);
                    Assert.That(healthy.Status, Is.EqualTo(200));
                    Assert.That(Encoding.ASCII.GetString(healthy.Body), Is.EqualTo("healthy"));
                }
                else if (protocol == 2)
                {
                    await using var peer = await H2Peer.ConnectAsync(host);
                    await peer.SendAsync(Http2Headers, EndHeaders, 1, H2Peer.Literal(
                        (":method", "POST"), (":scheme", "http"), (":authority", "localhost:" + host.Port),
                        (":path", "/upload"), ("content-length", "1")));
                    var head = await peer.UntilAsync(frame => frame.Id == 1 && frame.Type == Http2Headers);
                    Assert.That(RawBodyHpackStatus(head.Payload), Is.EqualTo(committed ? 200 : 408));
                    if (committed)
                    {
                        var data = await peer.UntilAsync(frame => frame.Id == 1 && frame.Type == Http2Data);
                        Assert.That(data.Payload, Is.EqualTo(new byte[] { (byte)'x' }));
                        Assert.That(data.Flags & EndStream, Is.Zero);
                        resume.TrySetResult(true);
                        var reset = await peer.UntilAsync(frame => frame.Id == 1 && frame.Type == Http2Reset);
                        Assert.That(reset.Payload.Length, Is.EqualTo(4));
                    }
                    else
                    {
                        using var body = new MemoryStream();
                        while (true)
                        {
                            var frame = await peer.ReceiveAsync();
                            Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));
                            if (frame.Id != 1) continue;
                            if (frame.Type == Http2Data) body.Write(frame.Payload);
                            if (frame.Type == Http2Reset || (frame.Flags & EndStream) != 0) break;
                        }
                        Assert.That(Encoding.UTF8.GetString(body.ToArray()), Does.Contain("within the configured time"));
                    }
                    await peer.AssertHealthyAsync(3);
                }
                else
                {
                    await using var peer = await H3Peer.ConnectAsync(host, certificate);
                    await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
                    var head = await H3Peer.ReadFrameAsync(request, host.Token);
                    Assert.That(head.Type, Is.EqualTo(1));
                    Assert.That(RawBodyQpackStatus(head.Payload), Is.EqualTo(committed ? 200 : 408));
                    if (committed)
                    {
                        var data = await H3Peer.ReadFrameAsync(request, host.Token);
                        Assert.That(data.Type, Is.Zero);
                        Assert.That(data.Payload, Is.EqualTo(new byte[] { (byte)'x' }));
                        resume.TrySetResult(true);
                        var failure = await Assert.CatchAsync<QuicException>(async () =>
                        {
                            if (!QuicConnection.IsSupported)
                                throw new AssertionException("The required QUIC transport became unavailable.");
                            _ = await H3Peer.ReadFrameAsync(request, host.Token, true);
                        });
                        Assert.That(failure, Is.Not.Null);
                        Assert.That(failure?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                    }
                    else
                    {
                        using var body = new MemoryStream();
                        while (true)
                        {
                            var frame = await H3Peer.ReadFrameAsync(request, host.Token, true);
                            if (frame.Type < 0) break;
                            Assert.That(frame.Type, Is.Zero);
                            body.Write(frame.Payload);
                        }
                        Assert.That(Encoding.UTF8.GetString(body.ToArray()), Does.Contain("within the configured time"));
                    }
                    await peer.AssertHealthyAsync(host.Token);
                }
                Assert.That(Volatile.Read(ref formatted), Is.EqualTo(committed ? 0 : 1),
                    "Only an uncommitted cleanup408 invokes error formatting, exactly once.");
                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { resume.TrySetResult(true); }
        }
    }
}
