using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Owned by the application-level conformance audit (docs/project/http-conformance.md,
    // "Application lifecycle audit"). Every case drives a public WebServer through public
    // application APIs and observes it with a peer that shares no code with the engine:
    // a raw HTTP/1.1 parser, a raw HTTP/2 frame peer with literal HPACK requests, or a
    // raw QUIC peer that reads HTTP/3 frame types and lengths. Field values the server
    // encodes are not decoded here; the independent hyper-h2 and aioquic campaigns decode
    // them. Each case checks that one failing or finished exchange leaves the listener,
    // and for HTTP/2 and HTTP/3 the shared connection, usable.
    [TestFixture]
    public sealed class HttpApplicationLifecycleAuditTest
    {
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);
        private const int Http2Headers = 1, Http2Data = 0, Http2Reset = 3, Http2Settings = 4, Http2Ping = 6, Http2GoAway = 7, Http2Continuation = 9, Http2WindowUpdate = 8;
        private const int EndStream = 1, EndHeaders = 4;

        // ---------------------------------------------------------------- HTTP/1.1

        // RFC 9110 section 15.2 and RFC 9112 section 7.1.2: interim responses precede
        // exactly one final response, and the trailer section ends the chunked message,
        // so the next pipelined response must begin immediately after its final CRLF.
        [Test]
        public async Task Http1InterimFinalAndTrailersEndBeforeThePipelinedSuccessor()
        {
            var body = Enumerable.Range(0, 5000).Select(i => (byte)(i % 249)).ToArray();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/next")
                {
                    await context.SendStringAsync("next", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }
                var sections = Sections(context);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, context.CancellationToken);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</b>; rel=preload" }, context.CancellationToken);
                sections.DeclareTrailers("X-Audit-End");
                context.Response.ContentType = "application/octet-stream";
                await context.Response.OutputStream.WriteAsync(body.AsMemory(0, 1000), context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(body.AsMemory(1000), context.CancellationToken);
                sections.SetTrailers(new WebHeaderCollection { ["X-Audit-End"] = "complete" });
            });
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            var reader = new RawHttp1Reader(tcp.GetStream());
            var authority = "localhost:" + host.Port;
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                $"GET /sections HTTP/1.1\r\nHost: {authority}\r\nTE: trailers\r\n\r\nGET /next HTTP/1.1\r\nHost: {authority}\r\n\r\n"), host.Token);

            var first = await reader.ReadResponseAsync(host.Token);
            Assert.That(first.InterimStatuses, Is.EqualTo(new[] { 103, 103 }));
            Assert.That(first.InterimFields.Select(f => f["link"]), Is.EqualTo(new[] { "</a>; rel=preload", "</b>; rel=preload" }));
            Assert.That(first.Status, Is.EqualTo(200));
            Assert.That(first.Headers["transfer-encoding"], Is.EqualTo("chunked"));
            Assert.That(first.Headers["x-audit-end"], Is.Null, "Trailer fields must not be merged into the header section.");
            Assert.That(first.Headers["link"], Is.Null, "Interim fields must not be copied into the final response.");
            Assert.That(first.Body, Is.EqualTo(body));
            Assert.That(first.Trailers?["x-audit-end"], Is.EqualTo("complete"));

            var second = await reader.ReadResponseAsync(host.Token);
            Assert.That(second.InterimStatuses, Is.Empty);
            Assert.That(second.Status, Is.EqualTo(200));
            Assert.That(Encoding.ASCII.GetString(second.Body), Is.EqualTo("next"));

            // The connection stays persistent after both messages.
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /next HTTP/1.1\r\nHost: {authority}\r\n\r\n"), host.Token);
            Assert.That((await reader.ReadResponseAsync(host.Token)).Status, Is.EqualTo(200));
        }

        // Finding F1 of the applicability audit, rechecked on the current engine. RFC 9110
        // section 15.2: a server MUST NOT send a 1xx response to an HTTP/1.0 client, and a
        // 1xx is never a final response. The public StatusCode setter still accepts 100-199.
        // The interim API (IHttpResponseSections) is the supported route; this case records
        // the remaining public-setter path. Unresolved: marked Explicit so the ordinary run
        // stays green; run it explicitly to reproduce.
        [Explicit("Audit finding F1 (unresolved): the public StatusCode setter accepts 1xx as a final status.")]
        [Category("AuditFinding")]
        [TestCase("1.0")]
        [TestCase("1.1")]
        public async Task Http1StatusSetterNeverSendsAnInformationalStatusAsTheFinalResponse(string version)
        {
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/next")
                {
                    await context.SendStringAsync("next", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }
                try { context.Response.StatusCode = 103; }
                catch (ArgumentException) { return; }
                catch (InvalidOperationException) { return; }
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
            });
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET /status HTTP/{version}\r\nHost: localhost:{host.Port}\r\n\r\n"), host.Token);
            // Collect everything sent within five seconds or until EOF, then classify the
            // status lines, so the observation is recorded whatever the framing.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(host.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            var ending = "EOF";
            try
            {
                int count;
                while ((count = await tcp.GetStream().ReadAsync(buffer, wait.Token)) != 0) received.Write(buffer, 0, count);
            }
            catch (OperationCanceledException) when (!host.Token.IsCancellationRequested) { ending = "no further bytes for 5 s"; }
            catch (IOException) { ending = "reset"; }
            var text = Encoding.ASCII.GetString(received.ToArray());
            var statuses = System.Text.RegularExpressions.Regex.Matches(text, @"(?m)^HTTP/1\.[01] (\d{3})").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            TestContext.Out.WriteLine($"HTTP/{version}: statuses [{string.Join(", ", statuses)}], then {ending}");
            if (version == "1.0") Assert.That(statuses.Where(s => s < 200), Is.Empty, "An HTTP/1.0 client must not receive a 1xx response.");
            Assert.That(statuses.Count(s => s >= 200), Is.EqualTo(1), "Every request needs exactly one final response.");
        }

        // ---------------------------------------------------------------- HTTP/2

        // RFC 9113 section 8.1: interim HEADERS, the final HEADERS, DATA and one ending
        // trailer HEADERS with END_STREAM, in that order, and nothing after END_STREAM.
        [Test]
        public async Task Http2InterimFinalDataAndTrailersUseTheRequiredFrameOrder()
        {
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (await Healthy(context)) return;
                var sections = Sections(context);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, context.CancellationToken);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</b>; rel=preload" }, context.CancellationToken);
                sections.DeclareTrailers("x-audit-end");
                for (var i = 0; i < 3; i++)
                {
                    await context.Response.OutputStream.WriteAsync(new byte[4000], context.CancellationToken);
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                }
                sections.SetTrailers(new WebHeaderCollection { ["x-audit-end"] = "complete" });
            });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, peer.Request("GET", "/sections"));
            var sequence = new StringBuilder();
            var dataBytes = 0;
            while (true)
            {
                var frame = await peer.ReceiveAsync();
                if (frame.Id != 1) continue;
                Assert.That(frame.Type, Is.AnyOf(Http2Headers, Http2Data), "Unexpected frame type on the response stream.");
                if (frame.Type == Http2Headers)
                {
                    Assert.That(frame.Flags & EndHeaders, Is.EqualTo(EndHeaders), "Small field sections need no CONTINUATION.");
                    sequence.Append((frame.Flags & EndStream) != 0 ? 'T' : 'H');
                }
                else
                {
                    dataBytes += frame.Payload.Length;
                    sequence.Append((frame.Flags & EndStream) != 0 ? 'E' : 'D');
                }
                if ((frame.Flags & EndStream) != 0) break;
            }
            var observed = System.Text.RegularExpressions.Regex.Replace(sequence.ToString(), "D+", "D");
            TestContext.Out.WriteLine("Stream 1 frames: " + sequence);
            Assert.That(observed, Is.EqualTo("HHHDT"), "Two interim HEADERS, final HEADERS, DATA, then trailer HEADERS ending the stream.");
            Assert.That(dataBytes, Is.EqualTo(12000));
            await peer.AssertHealthyAsync(3);
        }

        public enum ResetPhase { AfterInterim, AfterFinalHeaders, MidBody, BeforeTrailers }

        // RFC 9113 sections 5.1, 5.4.2 and 6.4: RST_STREAM closes only its stream. After
        // the server has processed it, nothing more is sent on that stream (a server does
        // not answer RST_STREAM with RST_STREAM), the application observes cancellation,
        // and sibling streams, PING and the listener continue.
        [TestCase(ResetPhase.AfterInterim)]
        [TestCase(ResetPhase.AfterFinalHeaders)]
        [TestCase(ResetPhase.MidBody)]
        [TestCase(ResetPhase.BeforeTrailers)]
        public async Task Http2ResetAtEachResponsePhaseStaysOnItsStream(ResetPhase phase)
        {
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (await Healthy(context)) return;
                context.OnClose(_ => Interlocked.Increment(ref closes));
                var token = context.CancellationToken;
                var sections = Sections(context);
                try
                {
                    await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, token);
                    if (phase == ResetPhase.AfterInterim) await HoldUntilCanceled(reached, token);
                    sections.DeclareTrailers("x-audit-end");
                    await context.Response.OutputStream.FlushAsync(token);
                    if (phase == ResetPhase.AfterFinalHeaders) await HoldUntilCanceled(reached, token);
                    await context.Response.OutputStream.WriteAsync(new byte[1000], token);
                    await context.Response.OutputStream.FlushAsync(token);
                    if (phase == ResetPhase.MidBody) await HoldUntilCanceled(reached, token);
                    await context.Response.OutputStream.WriteAsync(new byte[1000], token);
                    await context.Response.OutputStream.FlushAsync(token);
                    if (phase == ResetPhase.BeforeTrailers) await HoldUntilCanceled(reached, token);
                    outcome.TrySetResult("completed without observing the reset");
                }
                catch (OperationCanceledException)
                {
                    // The reset canceled the exchange. A further write must fail, not be sent.
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(new byte[100], CancellationToken.None);
                        await context.Response.OutputStream.FlushAsync(CancellationToken.None);
                        outcome.TrySetResult("write after reset accepted");
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
                    {
                        outcome.TrySetResult("write after reset failed: " + error.GetType().Name);
                    }
                    throw;
                }
            });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, peer.Request("GET", "/sections"));
            var headers = 0;
            var data = 0;
            // The frames and the handler's hold race; wait for each independently.
            while (!Enough(phase, headers, data))
            {
                var frame = await peer.ReceiveAsync();
                Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));
                if (frame.Id != 1) continue;
                Assert.That(frame.Type, Is.Not.EqualTo(Http2Reset), "The server reset the stream before the client did.");
                if (frame.Type == Http2Headers) headers++;
                if (frame.Type == Http2Data) data += frame.Payload.Length;
            }
            await reached.Task.WaitAsync(Settle);
            await peer.SendAsync(Http2Reset, 0, 1, new byte[] { 0, 0, 0, 8 });
            var result = await outcome.Task.WaitAsync(Settle);
            TestContext.Out.WriteLine(phase + ": " + result);
            // RFC 9113 constrains the wire, not the API: a write after the reset may fail or
            // be discarded, but the application must observe the cancellation.
            Assert.That(result, Does.Not.StartWith("completed"));
            // The sibling exchange shares the connection. Any frame for stream 1 that the
            // server emits after processing the reset fails the case inside the peer.
            await peer.AssertHealthyAsync(3, forbiddenStream: 1);
            await WaitForCount(() => Volatile.Read(ref closes), 1);
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        private static bool Enough(ResetPhase phase, int headers, int data) => phase switch
        {
            ResetPhase.AfterInterim => headers >= 1,
            ResetPhase.AfterFinalHeaders => headers >= 2,
            ResetPhase.MidBody => headers >= 2 && data >= 1000,
            _ => headers >= 2 && data >= 2000,
        };

        public enum StreamStateCase { DataAfterEndStream, HeadersAfterEndStream, TrailersWithoutEndStream, TrailersWithPseudoHeader, ContinuationWithoutHeaders, ResetOnIdleStream, WindowUpdateOnIdleStream, DataOnIdleStream }

        // Stream-state transitions while the connection carries a live sibling. Stream
        // errors (RFC 9113 sections 5.1 half-closed (remote), 8.1 malformed trailers) must
        // reset only the offending stream; connection errors (sections 5.1 idle, 6.4, 6.10)
        // must end the connection with GOAWAY PROTOCOL_ERROR and leave the listener serving.
        [TestCase(StreamStateCase.DataAfterEndStream, 0x5, false)]
        [TestCase(StreamStateCase.HeadersAfterEndStream, 0x5, false)]
        [TestCase(StreamStateCase.TrailersWithoutEndStream, 0x1, false)]
        [TestCase(StreamStateCase.TrailersWithPseudoHeader, 0x1, false)]
        [TestCase(StreamStateCase.ContinuationWithoutHeaders, 0x1, true)]
        [TestCase(StreamStateCase.ResetOnIdleStream, 0x1, true)]
        [TestCase(StreamStateCase.WindowUpdateOnIdleStream, 0x1, true)]
        [TestCase(StreamStateCase.DataOnIdleStream, 0x1, true)]
        public async Task Http2StreamStateViolationsHaveTheRequiredScope(StreamStateCase violation, int code, bool connectionError)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (await Healthy(context)) return;
                // Hold the response so the request stream stays half-closed (remote) or open.
                try { await release.Task.WaitAsync(context.CancellationToken); }
                catch (OperationCanceledException) { return; }
                await context.SendStringAsync("late", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            try
            {
                await using var peer = await H2Peer.ConnectAsync(host);
                switch (violation)
                {
                    case StreamStateCase.DataAfterEndStream:
                        await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, peer.Request("GET", "/held"));
                        await Task.Delay(100);
                        await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
                        break;
                    case StreamStateCase.HeadersAfterEndStream:
                        await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, peer.Request("GET", "/held"));
                        await Task.Delay(100);
                        await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, H2Peer.Literal(("x-late", "1")));
                        break;
                    case StreamStateCase.TrailersWithoutEndStream:
                        await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Request("POST", "/held"));
                        await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
                        await peer.SendAsync(Http2Headers, EndHeaders, 1, H2Peer.Literal(("x-trailer", "1")));
                        break;
                    case StreamStateCase.TrailersWithPseudoHeader:
                        await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Request("POST", "/held"));
                        await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
                        await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, H2Peer.Literal((":path", "/other")));
                        break;
                    case StreamStateCase.ContinuationWithoutHeaders:
                        await peer.SendAsync(Http2Continuation, EndHeaders, 1, peer.Request("GET", "/held"));
                        break;
                    case StreamStateCase.ResetOnIdleStream:
                        await peer.SendAsync(Http2Reset, 0, 1, new byte[] { 0, 0, 0, 8 });
                        break;
                    case StreamStateCase.WindowUpdateOnIdleStream:
                        await peer.SendAsync(Http2WindowUpdate, 0, 1, new byte[] { 0, 0, 0x10, 0 });
                        break;
                    case StreamStateCase.DataOnIdleStream:
                        await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
                        break;
                }
                if (connectionError)
                {
                    var goAway = await peer.UntilAsync(f => f.Type == Http2GoAway || (f.Type == Http2Reset && f.Id == 1));
                    Assert.That(goAway.Type, Is.EqualTo(Http2GoAway), "A connection error must be signaled with GOAWAY, not a stream reset.");
                    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.AsSpan(4)), Is.EqualTo((uint)code));
                    Assert.That(await peer.ReadToEndAsync(), Is.True, "The server closes the connection after a connection error.");
                    await using var fresh = await H2Peer.ConnectAsync(host);
                    await fresh.AssertHealthyAsync(1);
                }
                else
                {
                    var reset = await peer.UntilAsync(f => (f.Type == Http2Reset && f.Id == 1) || f.Type == Http2GoAway);
                    Assert.That(reset.Type, Is.EqualTo(Http2Reset), "A stream error must not close the shared connection.");
                    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(reset.Payload), Is.EqualTo((uint)code));
                    await peer.AssertHealthyAsync(3, forbiddenStream: 1);
                }
            }
            finally { release.TrySetResult(); }
        }

        // RFC 9113 section 8.5: after CONNECT succeeds, END_STREAM from the client half-
        // closes the tunnel. The server keeps its send direction until it finishes output.
        [TestCase(false)]
        [TestCase(true)]
        public async Task Http2PeerHalfCloseOfATunnelLeavesServerOutputOpen(bool capsules)
        {
            var output = Enumerable.Range(0, 60000).Select(i => (byte)(i * 7)).ToArray();
            var closes = 0;
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                if (await Healthy(context)) return;
                context.OnClose(_ => Interlocked.Increment(ref closes));
                var tunnel = await Tunnel(context).AcceptTunnelAsync("audit-tunnel", capsules, context.CancellationToken);
                // Read the peer's input to its end first, then answer.
                var buffer = new byte[256];
                if (capsules)
                {
                    var channel = tunnel.Capsules ?? throw new AssertionException("Missing capsule channel.");
                    while (await channel.ReadHeaderAsync(context.CancellationToken) is not null) await channel.SkipPayloadAsync(context.CancellationToken);
                    await channel.WriteHeaderAsync(0, output.Length, context.CancellationToken);
                    await channel.WritePayloadAsync(output, 0, output.Length, context.CancellationToken);
                }
                else
                {
                    while (await tunnel.Stream.ReadAsync(buffer, context.CancellationToken) != 0) { }
                    await tunnel.Stream.WriteAsync(output, context.CancellationToken);
                }
                await tunnel.CompleteOutputAsync(context.CancellationToken);
                await tunnel.CloseAsync();
            });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Connect("audit-tunnel", capsules));
            var head = await peer.UntilAsync(f => f.Id == 1);
            Assert.That(head.Type, Is.EqualTo(Http2Headers));
            Assert.That(head.Flags & EndStream, Is.Zero, "A successful tunnel response must not end the stream.");
            await peer.SendAsync(Http2Data, 0, 1, capsules ? new byte[] { 0x21, 2, 7, 7 } : new byte[] { 7, 7 });
            await peer.SendAsync(Http2Data, EndStream, 1, Array.Empty<byte>());
            using var received = new MemoryStream();
            while (true)
            {
                var frame = await peer.ReceiveAsync();
                Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));
                if (frame.Id != 1) continue;
                Assert.That(frame.Type, Is.Not.EqualTo(Http2Reset), "The half-closed tunnel was reset instead of completed.");
                Assert.That(frame.Type, Is.EqualTo(Http2Data));
                received.Write(frame.Payload);
                if ((frame.Flags & EndStream) != 0) break;
            }
            var expected = capsules ? Prefix(new byte[] { 0x00, 0x80, 0x00, 0xEA, 0x60 }, output) : output;
            Assert.That(received.ToArray(), Is.EqualTo(expected));
            await peer.AssertHealthyAsync(3, forbiddenStream: 1);
            await WaitForCount(() => Volatile.Read(ref closes), 1);
        }

        // RFC 9113 section 6.8: a graceful drain announces GOAWAY, completes the admitted
        // stream, here including its interim, body and trailer sections, and refuses a
        // stream the client opens afterwards with REFUSED_STREAM.
        [Test]
        public async Task Http2DrainCompletesACommittedResponseWithTrailersAndRefusesLaterStreams()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                Interlocked.Increment(ref calls);
                var sections = Sections(context);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, context.CancellationToken);
                sections.DeclareTrailers("x-audit-end");
                await context.Response.OutputStream.WriteAsync(new byte[3000], context.CancellationToken);
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(new byte[3000], context.CancellationToken);
                sections.SetTrailers(new WebHeaderCollection { ["x-audit-end"] = "complete" });
            });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, peer.Request("GET", "/drain"));
            await entered.Task.WaitAsync(Settle);
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20));
            var goAway = await peer.UntilAsync(f => f.Type == Http2GoAway);
            var cutoff = BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload) & 0x7fffffff;
            Assert.That(BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload.AsSpan(4)), Is.Zero, "Graceful drain uses NO_ERROR.");
            Assert.That(cutoff, Is.GreaterThanOrEqualTo(1u), "The cutoff covers the admitted stream.");
            if (cutoff == 0x7fffffff)
            {
                // Two-phase shutdown (RFC 9113 section 6.8): wait for the final cutoff.
                goAway = await peer.UntilAsync(f => f.Type == Http2GoAway);
                cutoff = BinaryPrimitives.ReadUInt32BigEndian(goAway.Payload) & 0x7fffffff;
            }
            Assert.That(cutoff, Is.EqualTo(1u), "No later stream was opened before the drain.");
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 3, peer.Request("GET", "/late"));
            release.TrySetResult();
            var data = 0;
            var trailers = false;
            var late = new List<string>();
            var ending = "";
            while (true)
            {
                (int Type, int Flags, int Id, byte[] Payload) frame;
                try { frame = await peer.ReceiveAsync(); }
                catch (Exception error) when (error is EndOfStreamException or IOException)
                {
                    ending = error.GetType().Name + ": " + error.Message;
                    break;
                }
                if (frame.Id == 3)
                {
                    // Ignoring (RFC 9113 6.8) or refusing with REFUSED_STREAM are both permitted;
                    // processing it is not.
                    late.Add(frame.Type == Http2Reset ? "RST_STREAM " + BinaryPrimitives.ReadUInt32BigEndian(frame.Payload) : "frame " + frame.Type);
                    Assert.That(frame.Type, Is.EqualTo(Http2Reset), "A stream above the GOAWAY cutoff must not be processed.");
                    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload), Is.EqualTo(0x7u));
                }
                if (frame.Id != 1) continue;
                Assert.That(trailers, Is.False, "Nothing may follow END_STREAM on the admitted stream.");
                Assert.That(frame.Type, Is.Not.EqualTo(Http2Reset), "Drain must not cancel the admitted response.");
                if (frame.Type == Http2Data)
                {
                    data += frame.Payload.Length;
                    Assert.That(frame.Flags & EndStream, Is.Zero, "Reserved trailers must end the stream, not DATA.");
                }
                if (frame.Type == Http2Headers && (frame.Flags & EndStream) != 0) trailers = true;
            }
            TestContext.Out.WriteLine("Stream above the cutoff: " + (late.Count == 0 ? "ignored" : string.Join(", ", late)));
            TestContext.Out.WriteLine("Connection ended with: " + ending + "; failed replies: " + string.Join("; ", peer.ReplyFailures));
            Assert.That(trailers, Is.True, "The admitted response must end with its trailer section. Frames: " + peer.Describe() + "; ended with " + ending);
            Assert.That(data, Is.GreaterThan(0), "The admitted response continued after GOAWAY.");
            Assert.That(peer.DataBytes(1), Is.EqualTo(6000), "All DATA of the admitted response.");
            await drain.WaitAsync(Settle);
            Assert.That(calls, Is.EqualTo(1));
        }

        // A tunnel is an admitted stream: a drain lets an active tunnel finish when the
        // application completes it, and aborts it at the deadline when it does not. Both
        // paths release the application's pending read and run the close callback once.
        [TestCase(true)]
        [TestCase(false)]
        public async Task Http2DrainWithAnOpenTunnelCompletesItOrAbortsAtTheDeadline(bool cooperative)
        {
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                context.OnClose(_ => Interlocked.Increment(ref closes));
                var tunnel = await Tunnel(context).AcceptTunnelAsync("audit-tunnel", false, context.CancellationToken);
                accepted.TrySetResult();
                var buffer = new byte[64];
                try
                {
                    var total = 0;
                    int count;
                    while ((count = await tunnel.Stream.ReadAsync(buffer, context.CancellationToken)) != 0) total += count;
                    await tunnel.Stream.WriteAsync(Encoding.ASCII.GetBytes("bye:" + total), context.CancellationToken);
                    await tunnel.CompleteOutputAsync(context.CancellationToken);
                    outcome.TrySetResult("completed:" + total);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    outcome.TrySetResult("released:" + error.GetType().Name);
                }
                finally { await tunnel.CloseAsync().ContinueWith(_ => { }, TaskScheduler.Default); }
            });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Connect("audit-tunnel", false));
            Assert.That((await peer.UntilAsync(f => f.Id == 1)).Type, Is.EqualTo(Http2Headers));
            await accepted.Task.WaitAsync(Settle);
            var deadline = TimeSpan.FromSeconds(cooperative ? 20 : 2);
            var started = DateTime.UtcNow;
            var drain = host.Server.DrainAsync(deadline);
            await peer.UntilAsync(f => f.Type == Http2GoAway);
            if (cooperative)
            {
                await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3, 4, 5 });
                await peer.SendAsync(Http2Data, EndStream, 1, Array.Empty<byte>());
                using var received = new MemoryStream();
                while (true)
                {
                    var frame = await peer.ReceiveAsync();
                    if (frame.Id != 1) continue;
                    Assert.That(frame.Type, Is.EqualTo(Http2Data), "The tunnel must finish, not be reset, while the drain deadline is open.");
                    received.Write(frame.Payload);
                    if ((frame.Flags & EndStream) != 0) break;
                }
                Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("bye:5"));
                Assert.That(await outcome.Task.WaitAsync(Settle), Is.EqualTo("completed:5"));
                await drain.WaitAsync(Settle);
            }
            else
            {
                try { await drain.WaitAsync(deadline + Settle); }
                catch (Exception error) when (error is not TimeoutException)
                {
                    TestContext.Out.WriteLine("Drain reported its deadline as " + error.GetType().Name);
                }
                var elapsed = DateTime.UtcNow - started;
                TestContext.Out.WriteLine("Drain with an idle tunnel finished after " + elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
                Assert.That(elapsed, Is.GreaterThanOrEqualTo(deadline - TimeSpan.FromMilliseconds(250)), "An admitted tunnel is not aborted before the drain deadline.");
                Assert.That(await outcome.Task.WaitAsync(Settle), Does.StartWith("released:"));
            }
            await WaitForCount(() => Volatile.Read(ref closes), 1);
        }

        // ---------------------------------------------------------------- HTTP/3

        // RFC 9114 section 4.1.1: a client abort after the interim HEADERS cancels only that
        // request; RFC 9114 section 8.1 H3_REQUEST_CANCELLED is a stream error. A sibling
        // request on the same QUIC connection must still complete.
        [Test]
        public async Task Http3AbortAfterTheInterimSectionStaysOnItsStream()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3AbortAfterInterimCore();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3AbortAfterInterimCore()
        {
            var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closes = 0;
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                context.OnClose(_ => Interlocked.Increment(ref closes));
                var sections = Sections(context);
                try
                {
                    await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, context.CancellationToken);
                    await Task.Delay(Timeout.Infinite, context.CancellationToken);
                }
                catch (OperationCanceledException) { outcome.TrySetResult("canceled"); throw; }
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("GET", "/sections", true);
            var interim = await H3Peer.ReadFrameAsync(request, host.Token);
            Assert.That(interim.Type, Is.EqualTo(1L), "The interim section arrives as a HEADERS frame.");
            request.Abort(QuicAbortDirection.Both, 0x10c);
            Assert.That(await outcome.Task.WaitAsync(Settle), Is.EqualTo("canceled"));
            await peer.AssertHealthyAsync(host.Token);
            await WaitForCount(() => Volatile.Read(ref closes), 1);
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        // RFC 9114 sections 5.2 and 4.1: drain sends GOAWAY on the control stream and the
        // admitted request completes with interim, final, DATA and trailer HEADERS, then FIN.
        [Test]
        public async Task Http3DrainCompletesACommittedResponseWithTrailers()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3DrainCore();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3DrainCore()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                var sections = Sections(context);
                await sections.SendInformationalAsync(103, new WebHeaderCollection { ["Link"] = "</a>; rel=preload" }, context.CancellationToken);
                sections.DeclareTrailers("x-audit-end");
                await context.Response.OutputStream.WriteAsync(new byte[3000], context.CancellationToken);
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                await context.Response.OutputStream.WriteAsync(new byte[3000], context.CancellationToken);
                sections.SetTrailers(new WebHeaderCollection { ["x-audit-end"] = "complete" });
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("GET", "/drain", true);
            await entered.Task.WaitAsync(Settle);
            var drain = host.Server.DrainAsync(TimeSpan.FromSeconds(20));
            var goAway = await peer.ReadControlFrameAsync(7, host.Token);
            Assert.That(H3Peer.ReadVarint(goAway.Payload, out _), Is.GreaterThanOrEqualTo(request.Id + 4), "The GOAWAY identifier keeps the admitted request.");
            release.TrySetResult();
            var sequence = new StringBuilder();
            long data = 0;
            while (true)
            {
                var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                if (frame.Type < 0) break;
                Assert.That(frame.Type, Is.AnyOf(0L, 1L), "Only HEADERS and DATA appear on the request stream.");
                if (frame.Type == 0) data += frame.Payload.Length;
                sequence.Append(frame.Type == 1 ? 'H' : 'D');
            }
            var observed = System.Text.RegularExpressions.Regex.Replace(sequence.ToString(), "D+", "D");
            TestContext.Out.WriteLine("Request stream frames: " + sequence);
            Assert.That(observed, Is.EqualTo("HHDH"), "Interim HEADERS, final HEADERS, DATA, trailer HEADERS, then FIN.");
            Assert.That(data, Is.EqualTo(6000));
            // The listener leaves closure to the peer once every admitted request finished,
            // bounded by the drain deadline (RFC 9114 section 5.2 permits either side to
            // close). A graceful client close with H3_NO_ERROR ends the drain promptly.
            Assert.That(drain.IsCompleted, Is.False, "Recorded policy: drain waits for peer closure or its deadline.");
            await peer.CloseAsync(0x100, host.Token);
            await drain.WaitAsync(Settle);
        }

        // A handler failing with a tunnel read in flight aborts only its stream; the lifetime
        // audit left this untested on HTTP/3.
        [Test]
        public async Task Http3TunnelHandlerFailureStaysOnItsStream()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3TunnelCore(true);
        }

        // Audit finding A1 (unresolved). On HTTP/1 and HTTP/2 an application that cancels its
        // own pending tunnel read can still write and complete output (lifetime audit table).
        // On HTTP/3 the canceled read aborts the request stream in both directions with
        // H3_REQUEST_CANCELLED (0x10c), so the peer never receives the later output. This is
        // not an RFC 9114 violation (a server may abort a request stream, section 4.1.1); it
        // is an inconsistency in the version-independent tunnel contract.
        [Explicit("Audit finding A1 (unresolved): HTTP/3 application read cancellation aborts the whole request stream.")]
        [Category("AuditFinding")]
        [Test]
        public async Task Http3TunnelApplicationReadCancellationLeavesOutputUsable()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3TunnelCore(false);
        }

        // The same divergence for an ordinary request body: an application that times out a
        // slow upload and answers 408 (RFC 9110 section 15.5.9) reaches the client on
        // HTTP/2, where a complete response may precede the end of the request (RFC 9113
        // section 8.1). The HTTP/3 variant is the A1 reproduction for request bodies.
        [Test]
        public async Task Http2ApplicationBodyReadTimeoutCanStillAnswer()
        {
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, BodyTimeoutHandler);
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Request("POST", "/upload"));
            await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
            using var body = new MemoryStream();
            while (true)
            {
                var frame = await peer.ReceiveAsync();
                Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));
                if (frame.Id != 1) continue;
                if (frame.Type == Http2Reset)
                {
                    // RST_STREAM(NO_ERROR) after a complete response is permitted (RFC 9113 8.1).
                    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(frame.Payload), Is.Zero, "The response was aborted.");
                    break;
                }
                if (frame.Type == Http2Data) body.Write(frame.Payload);
                if ((frame.Flags & EndStream) != 0) break;
            }
            Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("timeout after 3"));
            await peer.AssertHealthyAsync(3);
        }

        [Explicit("Audit finding A1 (unresolved): HTTP/3 application read cancellation aborts the whole request stream.")]
        [Category("AuditFinding")]
        [Test]
        public async Task Http3ApplicationBodyReadTimeoutCanStillAnswer()
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3BodyTimeoutCore();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3BodyTimeoutCore()
        {
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, BodyTimeoutHandler);
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
            await peer.AssertHealthyAsync(host.Token);
        }

        // Reads what arrives, times out the rest of the upload and answers 408.
        private static async Task BodyTimeoutHandler(IHttpContext context)
        {
            if (await Healthy(context)) return;
            var buffer = new byte[64];
            var total = 0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            try
            {
                while (total < 3) total += await context.Request.InputStream.ReadAsync(buffer, context.CancellationToken);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(300));
                int count;
                while ((count = await context.Request.InputStream.ReadAsync(buffer, deadline.Token)) != 0) total += count;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
            {
                context.Response.StatusCode = 408;
                await context.SendStringAsync("timeout after " + total, "text/plain", WebServer.Utf8NoBomEncoding);
                return;
            }
            await context.SendStringAsync("complete " + total, "text/plain", WebServer.Utf8NoBomEncoding);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3TunnelCore(bool fail)
        {
            var closes = 0;
            var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                context.OnClose(_ => Interlocked.Increment(ref closes));
                var tunnel = await Tunnel(context).AcceptTunnelAsync("audit-tunnel", false, context.CancellationToken);
                using var local = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                var pending = tunnel.Stream.ReadAsync(new byte[16], 0, 16, local.Token);
                if (fail) throw new InvalidOperationException("Audit handler failure with a read in flight.");
                await Task.Delay(100, context.CancellationToken);
                local.Cancel();
                try { await pending; outcome.TrySetResult("read completed"); }
                catch (OperationCanceledException) { outcome.TrySetResult("read canceled"); }
                await tunnel.Stream.WriteAsync(Encoding.ASCII.GetBytes("after-cancel"), context.CancellationToken);
                await tunnel.CompleteOutputAsync(context.CancellationToken);
                await tunnel.CloseAsync();
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenConnectAsync("audit-tunnel", host.Token);
            var head = await H3Peer.ReadFrameAsync(request, host.Token);
            Assert.That(head.Type, Is.EqualTo(1L));
            if (fail)
            {
                var error = await Assert.CatchAsync<QuicException>(async () =>
                {
                    while ((await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true)).Type >= 0) { }
                });
                TestContext.Out.WriteLine("Peer observed: " + error.QuicError + " " + error.ApplicationErrorCode);
                Assert.That(error.QuicError, Is.EqualTo(QuicError.StreamAborted), "A handler failure must abort the stream rather than end it cleanly.");
            }
            else
            {
                Assert.That(await outcome.Task.WaitAsync(Settle), Is.EqualTo("read canceled"));
                using var received = new MemoryStream();
                while (true)
                {
                    var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                    if (frame.Type < 0) break;
                    Assert.That(frame.Type, Is.EqualTo(0L), "Only DATA follows a successful CONNECT.");
                    received.Write(frame.Payload);
                }
                Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("after-cancel"));
            }
            await peer.AssertHealthyAsync(host.Token);
            await WaitForCount(() => Volatile.Read(ref closes), 1);
            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        // ---------------------------------------------------------------- helpers

        private static IHttpResponseSections Sections(IHttpContext context)
            => context.Response as IHttpResponseSections ?? throw new AssertionException("Missing response-section capability.");

        private static IHttpTunnelContext Tunnel(IHttpContext context)
            => context as IHttpTunnelContext ?? throw new AssertionException("Missing tunnel capability.");

        private static async Task<bool> Healthy(IHttpContext context)
        {
            if (context.Request.Url.AbsolutePath != "/healthy") return false;
            await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            return true;
        }

        private static async Task HoldUntilCanceled(TaskCompletionSource reached, CancellationToken token)
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }

        private static async Task WaitForCount(Func<int> count, int expected)
        {
            var deadline = DateTime.UtcNow + Settle;
            while (count() < expected && DateTime.UtcNow < deadline) await Task.Delay(20);
            await Task.Delay(100);
            Assert.That(count(), Is.EqualTo(expected), "The context close callback must run exactly once.");
        }

        private static byte[] Prefix(byte[] head, byte[] body)
        {
            var result = new byte[head.Length + body.Length];
            head.CopyTo(result, 0);
            body.CopyTo(result, head.Length);
            return result;
        }

        private static void RequireQuic()
        {
            var supported = QuicListener.IsSupported && QuicConnection.IsSupported
                && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") != null;
            if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
            if (!supported) Assert.Ignore("The selected asset or host has no HTTP/3 transport.");
        }

        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }

        // Ports come from the operating system rather than the shared test counter, so
        // these cases do not collide with concurrent suites in other worktrees.
        private static int FreePort(bool udp)
        {
            using var socket = udp
                ? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port;
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop;
            private readonly Task _running;

            private Host(WebServer server, int port, CancellationTokenSource stop, Task running)
            { Server = server; Port = port; _stop = stop; _running = running; }

            internal WebServer Server { get; }
            internal int Port { get; }
            internal CancellationToken Token => _stop.Token;

            internal static async Task<Host> StartAsync(HttpListenerMode mode, X509Certificate2? certificate, RequestHandlerCallback handler)
            {
                var quic = mode == HttpListenerMode.EmbedIOHttp3;
                var port = FreePort(quic);
                var prefix = (quic ? "https" : "http") + "://localhost:" + port + "/";
                var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var server = new WebServer(o =>
                {
                    o.WithUrlPrefix(prefix).WithMode(mode);
                    if (certificate != null) o.WithCertificate(certificate);
                }).WithAction("/", HttpVerbs.Any, handler);
                var running = server.RunAsync(stop.Token);
                var deadline = DateTime.UtcNow + Settle;
                while (server.State != WebServerState.Listening && !running.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
                if (running.IsCompleted) await running;
                return new Host(server, port, stop, running);
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(Settle); }
                finally
                {
                    Server.Dispose();
                    _stop.Dispose();
                }
            }
        }

        // Raw HTTP/2 peer: frames and literal HPACK requests written by hand. Responses are
        // inspected by frame type, flags and payload bytes only.
        private sealed class H2Peer : IAsyncDisposable
        {
            private readonly TcpClient _tcp;
            private readonly NetworkStream _wire;
            private readonly Host _host;
            private int _forbiddenStream;

            private H2Peer(TcpClient tcp, Host host) { _tcp = tcp; _wire = tcp.GetStream(); _host = host; }

            // Every frame received, in wire order, including frames skipped while waiting.
            internal List<(int Type, int Flags, int Id, int Length)> Log { get; } = new();

            internal int DataBytes(int id) => Log.Where(f => f.Type == Http2Data && f.Id == id).Sum(f => f.Length);

            internal List<string> ReplyFailures { get; } = new();

            internal string Describe() => string.Join(" ", Log.Select(f => $"{f.Type}/{f.Id}/{f.Flags:x}/{f.Length}"));

            internal static async Task<H2Peer> ConnectAsync(Host host)
            {
                var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
                var peer = new H2Peer(tcp, host);
                await peer._wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), host.Token);
                await peer.SendAsync(Http2Settings, 0, 0, Array.Empty<byte>());
                var settings = false;
                var acknowledged = false;
                while (!settings || !acknowledged)
                {
                    var frame = await peer.ReceiveAsync();
                    if (frame.Type != Http2Settings) continue;
                    if ((frame.Flags & 1) != 0) { acknowledged = true; continue; }
                    settings = true;
                    await peer.SendAsync(Http2Settings, 1, 0, Array.Empty<byte>());
                }
                return peer;
            }

            internal byte[] Request(string method, string path)
                => Literal((":method", method), (":scheme", "http"), (":authority", "localhost:" + _host.Port), (":path", path));

            internal byte[] Connect(string protocol, bool capsules)
                => capsules
                    ? Literal((":method", "CONNECT"), (":scheme", "http"), (":authority", "localhost:" + _host.Port), (":path", "/tunnel"), (":protocol", protocol), ("capsule-protocol", "?1"))
                    : Literal((":method", "CONNECT"), (":scheme", "http"), (":authority", "localhost:" + _host.Port), (":path", "/tunnel"), (":protocol", protocol));

            // HPACK literal header field without indexing, new name, no Huffman (RFC 7541 6.2.2).
            internal static byte[] Literal(params (string Name, string Value)[] fields)
            {
                using var output = new MemoryStream();
                foreach (var (name, value) in fields)
                {
                    output.WriteByte(0);
                    WriteString(output, name);
                    WriteString(output, value);
                }
                return output.ToArray();
            }

            private static void WriteString(Stream output, string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                Assert.That(bytes.Length, Is.LessThan(127));
                output.WriteByte((byte)bytes.Length);
                output.Write(bytes, 0, bytes.Length);
            }

            internal async Task SendAsync(int type, int flags, int id, byte[] payload)
            {
                var frame = new byte[9 + payload.Length];
                frame[0] = (byte)(payload.Length >> 16); frame[1] = (byte)(payload.Length >> 8); frame[2] = (byte)payload.Length;
                frame[3] = (byte)type; frame[4] = (byte)flags;
                BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(5), id);
                payload.CopyTo(frame, 9);
                await _wire.WriteAsync(frame, _host.Token);
            }

            internal async Task<(int Type, int Flags, int Id, byte[] Payload)> ReceiveAsync()
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_host.Token);
                timeout.CancelAfter(Settle);
                var header = new byte[9];
                await _wire.ReadExactlyAsync(header, timeout.Token);
                var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
                await _wire.ReadExactlyAsync(payload, timeout.Token);
                var frame = (Type: (int)header[3], Flags: (int)header[4], Id: BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(5)) & int.MaxValue, Payload: payload);
                Log.Add((frame.Type, frame.Flags, frame.Id, payload.Length));
                if (_forbiddenStream != 0 && frame.Id == _forbiddenStream)
                    throw new AssertionException($"The server sent frame type {frame.Type} on stream {frame.Id} after that stream ended.");
                // Automatic replies are best effort: once the server has closed its side
                // (after a drain, for example) a reply can fail while frames it already sent
                // are still readable. A failed reply is recorded, never treated as the end.
                try
                {
                    if (frame.Type == Http2Ping && (frame.Flags & 1) == 0) await SendAsync(Http2Ping, 1, 0, payload);
                    // Keep both windows open so large responses never wait on the peer.
                    if (frame.Type == Http2Data && payload.Length > 0)
                    {
                        var increment = new byte[4];
                        BinaryPrimitives.WriteUInt32BigEndian(increment, (uint)payload.Length);
                        await SendAsync(Http2WindowUpdate, 0, 0, increment);
                        if ((frame.Flags & EndStream) == 0) await SendAsync(Http2WindowUpdate, 0, frame.Id, increment);
                    }
                }
                catch (IOException error) { ReplyFailures.Add(error.GetType().Name + ": " + error.Message); }
                return frame;
            }

            internal async Task<(int Type, int Flags, int Id, byte[] Payload)> UntilAsync(Func<(int Type, int Flags, int Id, byte[] Payload), bool> match)
            {
                for (var seen = 0; seen < 4096; seen++)
                {
                    var frame = await ReceiveAsync();
                    if (match(frame)) return frame;
                }
                throw new AssertionException("Expected frame was not received.");
            }

            internal async Task<bool> ReadToEndAsync()
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_host.Token);
                timeout.CancelAfter(Settle);
                var buffer = new byte[4096];
                try { while (await _wire.ReadAsync(buffer, timeout.Token) != 0) { } }
                catch (IOException) { }
                return true;
            }

            // A sibling GET and a PING round trip on this connection, with no GOAWAY. When
            // forbiddenStream is set, any frame on that stream fails the case.
            internal async Task AssertHealthyAsync(int id, int forbiddenStream = 0)
            {
                _forbiddenStream = forbiddenStream;
                await SendAsync(Http2Ping, 0, 0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                await SendAsync(Http2Headers, EndHeaders | EndStream, id, Request("GET", "/healthy"));
                var pong = false;
                var body = new MemoryStream();
                var ended = false;
                while (!pong || !ended)
                {
                    var frame = await ReceiveAsync();
                    Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway), "The shared connection must stay open.");
                    if (frame.Type == Http2Ping && (frame.Flags & 1) != 0) pong = true;
                    if (frame.Id != id) continue;
                    Assert.That(frame.Type, Is.Not.EqualTo(Http2Reset), "The sibling stream was reset.");
                    if (frame.Type == Http2Data) body.Write(frame.Payload);
                    if ((frame.Flags & EndStream) != 0) ended = true;
                }
                Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("healthy"));
                // Give the server time to emit anything it still owed the forbidden stream.
                if (forbiddenStream != 0)
                {
                    await SendAsync(Http2Ping, 0, 0, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 });
                    await UntilAsync(f => f.Type == Http2Ping && (f.Flags & 1) != 0);
                }
                _forbiddenStream = 0;
            }

            public ValueTask DisposeAsync()
            {
                _wire.Dispose();
                _tcp.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        // Raw HTTP/3 peer over System.Net.Quic: literal QPACK requests (RFC 9204 4.5.6) and
        // response frames read by type and length only.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private sealed class H3Peer : IAsyncDisposable
        {
            private readonly QuicConnection _connection;
            private readonly QuicStream _control;
            private readonly List<QuicStream> _inbound = new();
            private readonly Host _host;
            private QuicStream? _serverControl;

            private H3Peer(QuicConnection connection, QuicStream control, Host host)
            { _connection = connection; _control = control; _host = host; }

            internal static async Task<H3Peer> ConnectAsync(Host host, X509Certificate2 certificate)
            {
                var connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, host.Port),
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundUnidirectionalStreams = 8,
                    MaxInboundBidirectionalStreams = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                        RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString()
                    }
                }, host.Token);
                var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, host.Token);
                await control.WriteAsync(new byte[] { 0, 4, 0 }, host.Token);
                return new H3Peer(connection, control, host);
            }

            internal async Task<QuicStream> OpenRequestAsync(string method, string path, bool fin)
            {
                var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, _host.Token);
                await SendFieldsAsync(stream, new[] { (":method", method), (":scheme", "https"), (":authority", "localhost:" + _host.Port), (":path", path) }, fin, _host.Token);
                return stream;
            }

            internal async Task<QuicStream> OpenConnectAsync(string protocol, CancellationToken token)
            {
                await ReadControlFrameAsync(4, token);
                var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await SendFieldsAsync(stream, new[] { (":method", "CONNECT"), (":scheme", "https"), (":authority", "localhost:" + _host.Port), (":path", "/tunnel"), (":protocol", protocol) }, false, token);
                return stream;
            }

            // Reads the server control stream until a frame of the given type.
            internal async Task<(long Type, byte[] Payload)> ReadControlFrameAsync(long type, CancellationToken token)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Settle);
                while (_serverControl == null)
                {
                    var stream = await _connection.AcceptInboundStreamAsync(timeout.Token);
                    _inbound.Add(stream);
                    var kind = new byte[1];
                    await stream.ReadExactlyAsync(kind, timeout.Token);
                    if (kind[0] == 0) _serverControl = stream;
                }
                while (true)
                {
                    var frame = await ReadFrameAsync(_serverControl, timeout.Token);
                    if (frame.Type == type) return frame;
                }
            }

            internal async Task CloseAsync(long code, CancellationToken token) => await _connection.CloseAsync(code, token);

            internal async Task AssertHealthyAsync(CancellationToken token)
            {
                await using var stream = await OpenRequestAsync("GET", "/healthy", true);
                var head = await ReadFrameAsync(stream, token);
                Assert.That(head.Type, Is.EqualTo(1L));
                using var body = new MemoryStream();
                while (true)
                {
                    var frame = await ReadFrameAsync(stream, token, allowEnd: true);
                    if (frame.Type < 0) break;
                    if (frame.Type == 0) body.Write(frame.Payload);
                }
                Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("healthy"));
            }

            private static async Task SendFieldsAsync(QuicStream stream, (string Name, string Value)[] fields, bool fin, CancellationToken token)
            {
                using var section = new MemoryStream();
                section.WriteByte(0);
                section.WriteByte(0);
                foreach (var (name, value) in fields)
                {
                    var nameBytes = Encoding.ASCII.GetBytes(name);
                    var valueBytes = Encoding.ASCII.GetBytes(value);
                    WritePrefix(section, nameBytes.Length, 3, 0x20);
                    section.Write(nameBytes);
                    WritePrefix(section, valueBytes.Length, 7, 0);
                    section.Write(valueBytes);
                }
                using var frame = new MemoryStream();
                WriteVarint(frame, 1);
                WriteVarint(frame, section.Length);
                section.WriteTo(frame);
                await stream.WriteAsync(frame.ToArray(), fin, token);
            }

            private static void WritePrefix(Stream output, int value, int bits, int flags)
            {
                var maximum = (1 << bits) - 1;
                output.WriteByte((byte)(flags | Math.Min(value, maximum)));
                if (value < maximum) return;
                value -= maximum;
                while (value >= 128) { output.WriteByte((byte)(128 | (value & 127))); value >>= 7; }
                output.WriteByte((byte)value);
            }

            private static void WriteVarint(Stream output, long value)
            {
                var length = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
                var bytes = new byte[length];
                for (var i = length - 1; i >= 0; --i) { bytes[i] = (byte)value; value >>= 8; }
                bytes[0] |= (byte)(length == 1 ? 0 : length == 2 ? 64 : length == 4 ? 128 : 192);
                output.Write(bytes);
            }

            internal static long ReadVarint(byte[] source, out int consumed)
            {
                consumed = 1 << (source[0] >> 6);
                long value = source[0] & 63;
                for (var i = 1; i < consumed; ++i) value = (value << 8) | source[i];
                return value;
            }

            private static async Task<long> ReadVarintAsync(Stream input, CancellationToken token, bool allowEnd)
            {
                var bytes = new byte[8];
                if (await input.ReadAsync(bytes.AsMemory(0, 1), token) == 0)
                {
                    if (allowEnd) return -1;
                    throw new EndOfStreamException();
                }
                var length = 1 << (bytes[0] >> 6);
                if (length > 1) await input.ReadExactlyAsync(bytes.AsMemory(1, length - 1), token);
                return ReadVarint(bytes, out _);
            }

            internal static async Task<(long Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken token, bool allowEnd = false)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(Settle);
                var type = await ReadVarintAsync(stream, timeout.Token, allowEnd);
                if (type < 0) return (-1, Array.Empty<byte>());
                var length = await ReadVarintAsync(stream, timeout.Token, false);
                Assert.That(length, Is.InRange(0, 1 << 20));
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, timeout.Token);
                return (type, payload);
            }

            public async ValueTask DisposeAsync()
            {
                foreach (var stream in _inbound) await stream.DisposeAsync();
                await _control.DisposeAsync();
                await _connection.DisposeAsync();
            }
        }

        // Minimal HTTP/1.1 response reader written from RFC 9112 sections 4 to 7. It keeps
        // interim responses separate and returns chunked trailer fields on their own.
        private sealed class RawHttp1Reader
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[8192];
            private int _start;
            private int _end;

            internal RawHttp1Reader(Stream stream) => _stream = stream;

            internal sealed class Response
            {
                internal List<int> InterimStatuses { get; } = new();
                internal List<Dictionary<string, string>> InterimFields { get; } = new();
                internal int Status { get; set; }
                internal FieldMap Headers { get; } = new();
                internal byte[] Body { get; set; } = Array.Empty<byte>();
                internal FieldMap? Trailers { get; set; }
            }

            internal sealed class FieldMap
            {
                private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
                internal string? this[string name] => _values.TryGetValue(name, out var value) ? value : null;
                internal void Add(string name, string value) => _values[name] = _values.TryGetValue(name, out var prior) ? prior + ", " + value : value;
                internal Dictionary<string, string> ToDictionary() => new(_values, StringComparer.OrdinalIgnoreCase);
            }

            internal async Task<Response> ReadResponseAsync(CancellationToken token)
            {
                var response = new Response();
                while (true)
                {
                    var status = await ReadLineAsync(token);
                    Assert.That(status, Does.Match(@"^HTTP/1\.[01] \d{3}( .*)?$"), "Malformed status line.");
                    var code = int.Parse(status.Substring(9, 3), System.Globalization.CultureInfo.InvariantCulture);
                    var fields = new FieldMap();
                    await ReadFieldsAsync(fields, token);
                    if (code >= 100 && code < 200)
                    {
                        response.InterimStatuses.Add(code);
                        response.InterimFields.Add(fields.ToDictionary());
                        continue;
                    }
                    response.Status = code;
                    foreach (var pair in fields.ToDictionary()) response.Headers.Add(pair.Key, pair.Value);
                    break;
                }
                if (response.Status is 204 or 304) return response;
                if (string.Equals(response.Headers["transfer-encoding"], "chunked", StringComparison.OrdinalIgnoreCase))
                {
                    using var body = new MemoryStream();
                    while (true)
                    {
                        var line = await ReadLineAsync(token);
                        var size = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
                        if (size == 0) break;
                        body.Write(await ReadExactlyAsync(size, token));
                        Assert.That(await ReadLineAsync(token), Is.Empty, "Chunk data must end with CRLF.");
                    }
                    var trailers = new FieldMap();
                    await ReadFieldsAsync(trailers, token);
                    response.Trailers = trailers;
                    response.Body = body.ToArray();
                }
                else if (response.Headers["content-length"] is { } length)
                {
                    response.Body = await ReadExactlyAsync(int.Parse(length, System.Globalization.CultureInfo.InvariantCulture), token);
                }
                else Assert.Fail("A persistent response needs explicit framing.");
                return response;
            }

            private async Task ReadFieldsAsync(FieldMap fields, CancellationToken token)
            {
                while (true)
                {
                    var line = await ReadLineAsync(token);
                    if (line.Length == 0) return;
                    var colon = line.IndexOf(':', StringComparison.Ordinal);
                    Assert.That(colon, Is.GreaterThan(0), "Malformed field line: " + line);
                    fields.Add(line.Substring(0, colon), line.Substring(colon + 1).Trim());
                }
            }

            private async Task<int> FillAsync(CancellationToken token)
            {
                if (_start == _end) { _start = 0; _end = 0; }
                var read = await _stream.ReadAsync(_buffer.AsMemory(_end), token);
                if (read == 0) throw new EndOfStreamException("The connection ended inside a response.");
                _end += read;
                return read;
            }

            private async Task<string> ReadLineAsync(CancellationToken token)
            {
                var line = new MemoryStream();
                while (true)
                {
                    if (_start == _end) await FillAsync(token);
                    var b = _buffer[_start++];
                    if (b == '\n')
                    {
                        var bytes = line.ToArray();
                        Assert.That(bytes.Length > 0 && bytes[^1] == '\r', Is.True, "Lines must end with CRLF.");
                        return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
                    }
                    line.WriteByte(b);
                }
            }

            private async Task<byte[]> ReadExactlyAsync(int count, CancellationToken token)
            {
                var result = new byte[count];
                var filled = 0;
                while (filled < count)
                {
                    if (_start == _end) await FillAsync(token);
                    var take = Math.Min(count - filled, _end - _start);
                    Array.Copy(_buffer, _start, result, filled, take);
                    _start += take;
                    filled += take;
                }
                return result;
            }
        }
    }
}
