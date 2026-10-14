using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
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
                    // The handler deliberately does not read the entity. HTTP/1 cleanup must
                    // enforce both the raw policy and the body-read deadline before it can
                    // reuse the connection; HTTP/2 and HTTP/3 leave an unopened body to the
                    // transport, which ends the stream after the response.
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
                    Assert.That(RawBodyHpackStatus(head.Payload), Is.EqualTo(200));
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
                        // The handler's empty response ends the stream, then RST_STREAM
                        // NO_ERROR asks the client to stop sending (RFC 9113 section 8.1).
                        var ended = (head.Flags & EndStream) != 0;
                        using var body = new MemoryStream();
                        while (true)
                        {
                            var frame = await peer.ReceiveAsync();
                            Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));
                            if (frame.Id != 1) continue;
                            if (frame.Type == Http2Data)
                            {
                                body.Write(frame.Payload);
                                ended |= (frame.Flags & EndStream) != 0;
                            }
                            if (frame.Type != Http2Reset) continue;
                            Assert.That(ended, Is.True, "The response ends before the reset.");
                            Assert.That(frame.Payload, Is.EqualTo(new byte[4]), "RST_STREAM NO_ERROR.");
                            break;
                        }
                        Assert.That(body.Length, Is.Zero);
                    }
                    await peer.AssertHealthyAsync(3);
                }
                else
                {
                    await using var peer = await H3Peer.ConnectAsync(host, certificate);
                    await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
                    var head = await H3Peer.ReadFrameAsync(request, host.Token);
                    Assert.That(head.Type, Is.EqualTo(1));
                    Assert.That(RawBodyQpackStatus(head.Payload), Is.EqualTo(200));
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
                        // The handler's empty response ends with FIN, then STOP_SENDING with
                        // H3_NO_ERROR asks the client to stop sending (RFC 9114 section 4.1).
                        var writesClosed = request.WritesClosed;
                        using var body = new MemoryStream();
                        while (true)
                        {
                            var frame = await H3Peer.ReadFrameAsync(request, host.Token, true);
                            if (frame.Type < 0) break;
                            Assert.That(frame.Type, Is.Zero);
                            body.Write(frame.Payload);
                        }
                        Assert.That(body.Length, Is.Zero);
                        var stopped = await Assert.CatchAsync<QuicException>(async () => await writesClosed.WaitAsync(host.Token));
                        if (!QuicConnection.IsSupported)
                            throw new AssertionException("The required QUIC transport became unavailable.");
                        Assert.That(stopped?.ApplicationErrorCode, Is.EqualTo(0x100L), "STOP_SENDING with H3_NO_ERROR.");
                    }
                    await peer.AssertHealthyAsync(host.Token);
                }
                Assert.That(Volatile.Read(ref formatted), Is.EqualTo(protocol == 1 && !committed ? 1 : 0),
                    "Only the uncommitted HTTP/1 cleanup 408 invokes error formatting, exactly once.");
                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally { resume.TrySetResult(true); }
        }

        // A handler that gives up on a slow HTTP/3 upload by canceling its own read still
        // answers, and cleanup leaves the abandoned body to the transport. Under the default
        // body limit cleanup used to drain it, which could only fail, and logged the request as
        // a failed stream.
        [Test]
        public async Task HandlerCanceledHttp3ReadLeavesTheBodyToTheTransport()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await HandlerCanceledHttp3ReadCore();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task HandlerCanceledHttp3ReadCore()
        {
            using var trace = new RequestOutcomeTrace();
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                trace.RequestId = context.Id;
                var buffer = new byte[64];
                var total = 0;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                try
                {
                    while (total < 3)
                    {
                        var prefix = await context.Request.InputStream.ReadAsync(buffer, context.CancellationToken);
                        if (prefix == 0) throw new EndOfStreamException("The handler expects three body bytes first.");
                        total += prefix;
                    }
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(300));
                    int count;
                    while ((count = await context.Request.InputStream.ReadAsync(buffer, deadline.Token)) != 0) total += count;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
                {
                    context.Response.StatusCode = 408;
                    await context.SendStringAsync("timeout after " + total, "text/plain", WebServer.Utf8NoBomEncoding);
                }
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
            await request.WriteAsync(new byte[] { 0, 3, 1, 2, 3 }, host.Token);
            Assert.That((await H3Peer.ReadFrameAsync(request, host.Token)).Type, Is.EqualTo(1L), "Final response HEADERS.");
            using var body = new MemoryStream();
            while (true)
            {
                var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                if (frame.Type < 0) break;
                if (frame.Type == 0) body.Write(frame.Payload);
            }
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("timeout after 3"));
            Assert.That(await trace.Outcome.Task.WaitAsync(Settle), Is.EqualTo("completed"),
                "Cleanup must not fail on a body the transport stopped reading.");
            Assert.That(trace.Errors, Is.Empty, "The request logs no error.");
            await peer.AssertHealthyAsync(host.Token);
        }

        // Records every error one request logs, and how it ended: its completion log line, or the
        // stream failure that replaces it when cleanup throws.
        private sealed class RequestOutcomeTrace : System.Diagnostics.TraceListener
        {
            private readonly System.Diagnostics.SourceLevels _previous;
            private string? _requestId;
            internal readonly TaskCompletionSource<string> Outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ConcurrentQueue<string> Errors = new();
            internal RequestOutcomeTrace()
            {
                _previous = Diagnostics.Log.Source.Switch.Level;
                Diagnostics.Log.Source.Listeners.Add(this);
                Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.All;
            }
            internal string? RequestId
            {
                get => Volatile.Read(ref _requestId);
                set => Volatile.Write(ref _requestId, value);
            }
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void TraceEvent(System.Diagnostics.TraceEventCache? eventCache, string source,
                System.Diagnostics.TraceEventType eventType, int id, string? message)
                => Record(eventType, message);
            public override void TraceEvent(System.Diagnostics.TraceEventCache? eventCache, string source,
                System.Diagnostics.TraceEventType eventType, int id, string? format, params object?[]? args)
                => Record(eventType, format == null || args == null ? format : string.Format(CultureInfo.InvariantCulture, format, args));
            private void Record(System.Diagnostics.TraceEventType type, string? message)
            {
                var id = RequestId;
                if (id == null || message == null || !message.Contains("[" + id + "]", StringComparison.Ordinal)) return;
                if (type <= System.Diagnostics.TraceEventType.Error) Errors.Enqueue(message);
                if (message.Contains("Multiplexed request stream failed", StringComparison.Ordinal)) Outcome.TrySetResult("stream failed");
                else if (message.Contains(" sent in ", StringComparison.Ordinal)) Outcome.TrySetResult("completed");
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Diagnostics.Log.Source.Listeners.Remove(this);
                    Diagnostics.Log.Source.Switch.Level = _previous;
                }
                base.Dispose(disposing);
            }
        }
    }
}
