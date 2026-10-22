using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        private static readonly int[] _continueStatuses = [100];

        private sealed class RawBodyDeadlineTrace : System.Diagnostics.TraceListener
        {
            private readonly System.Diagnostics.SourceLevels _previous;
            internal readonly TaskCompletionSource<bool> Outcome = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            internal string? RequestId;

            internal RawBodyDeadlineTrace()
            {
                _previous = Diagnostics.Log.Source.Switch.Level;
                Diagnostics.Log.Source.Listeners.Add(this);
                Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.All;
            }

            public override void Write(string? message) => TestContext.Out.Write(message);

            public override void WriteLine(string? message)
            {
                TestContext.Out.WriteLine(message);

                if (
                    RequestId == null
                    || message == null
                    || !message.Contains("[" + RequestId + "]", StringComparison.Ordinal)
                ) return;

                if (message.Contains("Fatal exception.", StringComparison.Ordinal)) Outcome.TrySetResult(false);

                if (message.Contains("Owned request body cleanup canceled.", StringComparison.Ordinal))
                    Outcome.TrySetResult(true);
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

        [Test]
        public async Task RawBodyActorCleanupRetainsDeadlineCancellationAfterTokenRestoration()
        {
            using var trace = new RawBodyDeadlineTrace();
            var pending = new TaskCompletionSource<(Task Cleanup, CancellationToken Token)>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    trace.RequestId = context.Id;
                    context.OnClose(_ => closed.TrySetResult(true));
                    await context.SendStringAsync("started", "text/plain", WebServer.Utf8NoBomEncoding);
                    // The handler has not returned, so only the actual connection
                    // actor can reserve cleanup after this completed response.
                    var field = context.Request
                        .GetType()
                        .GetField("_bodyCleanup", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new AssertionException("Missing owned cleanup observation.");
                    Task? cleanup;

                    while ((cleanup = field.GetValue(context.Request) as Task) == null)
                        await Task.Delay(1, context.CancellationToken);

                    Assert.That(cleanup.IsCompleted, Is.False);
                    pending.TrySetResult((cleanup, context.CancellationToken));
                    await cleanup;
                },
                options => options
                    .WithMaximumRequestBodyBytes(2)
                    .WithMaximumConcurrentRequests(1)
                    .WithRequestTimeout(TimeSpan.FromSeconds(2))
            );
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            var wire = peer.GetStream();
            await wire.WriteAsync(
                Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nContent-Length: 2\r\n\r\n"),
                host.Token
            );
            var response = await new RawHttp1Reader(wire).ReadResponseAsync(host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.Status, Is.EqualTo(200));
                Assert.That(Encoding.ASCII.GetString(response.Body), Is.EqualTo("started"));
            }

            var (Cleanup, Token) = await pending.Task.WaitAsync(host.Token);
            var cancellation = await Assert.CatchAsync<OperationCanceledException>(async () => await Cleanup)
                ?? throw new AssertionException("Cleanup did not cancel.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Cleanup.IsCanceled, Is.True);
                Assert.That(Token.IsCancellationRequested, Is.True);
                Assert.That(cancellation.CancellationToken, Is.EqualTo(Token));
                Assert.That(
                    await trace.Outcome.Task.WaitAsync(host.Token),
                    Is.True,
                    "Owned cleanup cancellation must retain its cause after the server token is restored, without a listener-fatal error."
                );
            }

            await closed.Task.WaitAsync(host.Token);
            var eof = new byte[1];
            Assert.That(await wire.ReadAsync(eof, host.Token), Is.Zero);
            using var successor = new TcpClient();
            await successor.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            await successor
                .GetStream()
                .WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"GET / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nConnection: close\r\n\r\n"
                    ),
                    host.Token
                );
            var healthy = await new RawHttp1Reader(successor.GetStream()).ReadResponseAsync(host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(healthy.Status, Is.EqualTo(200));
                Assert.That(Encoding.ASCII.GetString(healthy.Body), Is.EqualTo("healthy"));
                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            }
        }

        private sealed class RawBodyDiagnosticTrace : System.Diagnostics.TraceListener
        {
            private readonly System.Diagnostics.SourceLevels _previous;

            internal RawBodyDiagnosticTrace()
            {
                _previous = Diagnostics.Log.Source.Switch.Level;
                Diagnostics.Log.Source.Listeners.Add(this);
                Diagnostics.Log.Source.Switch.Level = System.Diagnostics.SourceLevels.Error;
            }

            public override void Write(string? message) => TestContext.Out.Write(message);

            public override void WriteLine(string? message) => TestContext.Out.WriteLine(message);

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

        [TestCase(2)]
        [TestCase(3)]
        public async Task RawBodyCleanupCallersJoinOriginalOutcomeAcrossRetirement(int length)
        {
            var exceptionResponses = 0;
            var entered = new TaskCompletionSource<(object Request, Task Cleanup)>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            static Task Complete(object request, CancellationToken token) =>
                request
                    .GetType()
                    .GetMethod("CompleteBodyInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(request, [token]) as Task
                    ?? throw new AssertionException("Missing request-owned cleanup.");
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    var cleanup = Complete(context.Request, context.CancellationToken);
                    Assert.That(Complete(context.Request, CancellationToken.None), Is.SameAs(cleanup));
                    context.Request.InputStream.Dispose();
                    entered.TrySetResult((context.Request, cleanup));
                    await cleanup;
                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(2),
                server =>
                {
                    async Task Respond(IHttpContext context, IHttpException error)
                    {
                        Interlocked.Increment(ref exceptionResponses);
                        await HttpExceptionHandler.HtmlResponse(context, error);
                    }

                    server.OnHttpException = Respond;

                    foreach (var module in server.Modules) module.OnHttpException = Respond;
                }
            );
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            var wire = peer.GetStream();
            await wire.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nTransfer-Encoding: chunked\r\n\r\n"
                ),
                host.Token
            );
            var (Request, Cleanup) = await entered.Task.WaitAsync(host.Token);
            Assert.That(Cleanup.IsCompleted, Is.False, "Cleanup must await the delayed entity bytes.");
            await wire.WriteAsync(
                Encoding.ASCII.GetBytes($"{length:x}\r\n{new string('a', length)}\r\n0\r\n\r\n"),
                host.Token
            );
            var reader = new RawHttp1Reader(wire);
            var response = await reader.ReadResponseAsync(host.Token);
            Assert.That(response.Status, Is.EqualTo(length == 2 ? 200 : 413));
            Exception? failure = null;

            if (length == 2)
            {
                await Cleanup;
                await wire.WriteAsync(
                    Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: localhost:{host.Port}\r\n\r\n"),
                    host.Token
                );
                Assert.That(
                    Encoding.ASCII.GetString((await reader.ReadResponseAsync(host.Token)).Body),
                    Is.EqualTo("healthy")
                );
            }

            else
            {
                failure = await Assert.CatchAsync<Exception>(async () => await Cleanup);
                Assert.That(
                    failure,
                    Is
                        .AssignableTo<IHttpException>()
                        .With.Property("StatusCode")
                        .EqualTo(413)
                );
                var eof = new byte[1];
                Assert.That(await wire.ReadAsync(eof, host.Token), Is.Zero);
            }

            Assert.That(Complete(Request, host.Token), Is.SameAs(Cleanup));

            if (failure != null)
                Assert.That(
                    await Assert.CatchAsync<Exception>(async () => await Complete(Request, host.Token)),
                    Is.SameAs(failure)
                );

            Assert.That(
                Volatile.Read(ref exceptionResponses),
                Is.EqualTo(length == 2 ? 0 : 1),
                "The shared outcome must not invoke a second error response."
            );
        }

        private sealed class UnknownLengthContent : HttpContent
        {
            private readonly byte[] _bytes;

            internal UnknownLengthContent(byte[] bytes) => _bytes = bytes;

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                foreach (var value in _bytes)
                    await stream
                        .WriteAsync(new byte[] { value })
                        .ConfigureAwait(false);
            }
        }

        [Test]
        public async Task RawBodyConcurrentFirstGettersShareFramingAndOneApplicationView()
        {
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    var launch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var getters = new Task<Stream>[8];

                    for (var i = 0; i < getters.Length; i++)
                        getters[i] = Task.Run(async () =>
                        {
                            await launch.Task;
                            return context.Request.InputStream;
                        });

                    launch.TrySetResult(true);
                    var inputs = await Task.WhenAll(getters);

                    foreach (var input in inputs) Assert.That(input, Is.SameAs(inputs[0]));

                    Assert.That(inputs[0].ReadByte(), Is.EqualTo((byte)'a'));
                    var bytes = new byte[2];

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(await inputs[7].ReadAsync(bytes, 0, 2, context.CancellationToken), Is.EqualTo(1));
                        Assert.That(bytes[0], Is.EqualTo((byte)'b'));
                        Assert.That(inputs[3].ReadByte(), Is.EqualTo(-1));
                    }

                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(2)
            );
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            await peer
                .GetStream()
                .WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nContent-Length: 2\r\n\r\nab"
                    ),
                    host.Token
                );
            var reply = await new RawHttp1Reader(peer.GetStream()).ReadResponseAsync(host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(reply.Status, Is.EqualTo(200));
                Assert.That(Encoding.ASCII.GetString(reply.Body), Is.EqualTo("accepted"));
            }
        }

        [TestCase("gzip", "exact")]
        [TestCase("gzip", "raw")]
        [TestCase("gzip", "decoded")]
        [TestCase("deflate", "exact")]
        [TestCase("deflate", "raw")]
        [TestCase("deflate", "decoded")]
        [TestCase("br", "exact")]
        [TestCase("br", "raw")]
        [TestCase("br", "decoded")]
        public async Task RawBodyEncodedLimitRemainsIndependentOfDecodedLimit(string coding, string dimension)
        {
            using var diagnostics = new RawBodyDiagnosticTrace();
            var payload = Encoding.UTF8.GetBytes(new string('x', 2048));
            using var encoded = new MemoryStream();

            using (Stream compressor = coding switch
            {
                "gzip" => new GZipStream(encoded, CompressionMode.Compress, true),
                "deflate" => new DeflateStream(encoded, CompressionMode.Compress, true),
                "br" => new BrotliStream(encoded, CompressionMode.Compress, true),
                _ => throw new AssertionException("Unknown coding.")
            }) compressor.Write(payload);

            var contentBytes = encoded.ToArray();
            var entered = 0;
            var accepted = 0;
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    Interlocked.Increment(ref entered);
                    Assert.That(await context.GetRequestBodyAsByteArrayAsync(), Is.EqualTo(payload));
                    Interlocked.Increment(ref accepted);
                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options
                    .WithSupportCompressedRequests(true)
                    .WithMaximumRequestBodyBytes(contentBytes.LongLength - (dimension == "raw" ? 1 : 0))
                    .WithMaximumDecompressedRequestBodyBytes(payload.LongLength - (dimension == "decoded" ? 1 : 0))
            );
            using var handler = new SocketsHttpHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = Settle };
            client.DefaultRequestHeaders.Host = "localhost:" + host.Port;
            var address = new Uri("http://127.0.0.1:" + host.Port + "/");
            using var content = new ByteArrayContent(contentBytes);
            content.Headers.ContentEncoding.Add(coding);
            using var response = await client.PostAsync(address, content, host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(dimension == "exact" ? 200 : 413));
                Assert.That(Volatile.Read(ref entered), Is.EqualTo(dimension == "raw" ? 0 : 1));
                Assert.That(Volatile.Read(ref accepted), Is.EqualTo(dimension == "exact" ? 1 : 0));
                Assert.That(await client.GetStringAsync(address, host.Token), Is.EqualTo("healthy"));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task RawBodyExpectUsesActualSharedRouteOwnerBeforeContinue(bool limitedCreatesEndpoint)
        {
            var port = FreePort(false);
            var limitedCalls = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var limited = new WebServer(options => options
                .WithUrlPrefix($"http://localhost:{port}/limited/")
                .WithMode(HttpListenerMode.EmbedIO)
                .WithMaximumRequestBodyBytes(2));
            using var free = new WebServer(options => options
                .WithUrlPrefix($"http://localhost:{port}/free/")
                .WithMode(HttpListenerMode.EmbedIO));
            limited.OnAny(async context =>
            {
                if (context.Request.HttpMethod == "POST") Interlocked.Increment(ref limitedCalls);

                await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            free.OnAny(async context =>
            {
                var body = await context.GetRequestBodyAsByteArrayAsync();
                Assert.That(body, Is.EqualTo(Encoding.ASCII.GetBytes("abc")));
                await context.SendStringAsync("free", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            var first = (limitedCreatesEndpoint ? limited : free).RunAsync(stop.Token);
            var second = (limitedCreatesEndpoint ? free : limited).RunAsync(stop.Token);

            try
            {
                using var denied = new TcpClient();
                await denied.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                await denied
                    .GetStream()
                    .WriteAsync(
                        Encoding.ASCII.GetBytes(
                            $"POST /limited/ HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 3\r\nExpect: 100-continue\r\n\r\n"
                        ),
                        stop.Token
                    );
                var rejected = await new RawHttp1Reader(denied.GetStream()).ReadResponseAsync(stop.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(rejected.Status, Is.EqualTo(413));
                    Assert.That(rejected.InterimStatuses, Is.Empty);
                    Assert.That(Volatile.Read(ref limitedCalls), Is.Zero);
                }

                using var allowed = new TcpClient();
                await allowed.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                await allowed
                    .GetStream()
                    .WriteAsync(
                        Encoding.ASCII.GetBytes(
                            $"POST /free/ HTTP/1.1\r\nHost: localhost:{port}\r\nContent-Length: 3\r\nExpect: 100-continue\r\n\r\nabc"
                        ),
                        stop.Token
                    );
                var accepted = await new RawHttp1Reader(allowed.GetStream()).ReadResponseAsync(stop.Token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(accepted.Status, Is.EqualTo(200));
                    Assert.That(accepted.InterimStatuses, Is.EqualTo(_continueStatuses));
                    Assert.That(Encoding.ASCII.GetString(accepted.Body), Is.EqualTo("free"));
                    Assert.That(limited.State, Is.EqualTo(WebServerState.Listening));
                    Assert.That(free.State, Is.EqualTo(WebServerState.Listening));
                }
            }

            finally
            {
                stop.Cancel();
                await Task
                    .WhenAll(first, second)
                    .WaitAsync(Settle);
            }
        }

        [TestCase(0)]
        [TestCase(2)]
        public async Task RawBodyChunkSyntaxAndTrailersDoNotConsumeEntityBudget(int length)
        {
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    var trailers = context.Request as IHttpRequestTrailers
                        ?? throw new AssertionException("Missing trailer capability.");
                    Assert.That(trailers.TryGetTrailers(out _), Is.False);
                    var body = await context.GetRequestBodyAsByteArrayAsync();

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(body, Is.EqualTo(length == 0 ? [] : Encoding.ASCII.GetBytes("ab")));
                        Assert.That(trailers.TryGetTrailers(out var ending), Is.True);
                        Assert.That(
                            ending,
                            Has.Some
                                .Property("Key")
                                .EqualTo("X-Raw-End")
                        );
                    }

                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(length)
            );
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            var data = length == 0 ? "" : "1\r\na\r\n1\r\nb\r\n";
            await peer
                .GetStream()
                .WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nTransfer-Encoding: chunked\r\nTrailer: X-Raw-End\r\n\r\n{data}0\r\nX-Raw-End: done\r\n\r\nGET /healthy HTTP/1.1\r\nHost: localhost:{host.Port}\r\n\r\n"
                    ),
                    host.Token
                );
            var reader = new RawHttp1Reader(peer.GetStream());
            var upload = await reader.ReadResponseAsync(host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(upload.Status, Is.EqualTo(200));
                Assert.That(Encoding.ASCII.GetString(upload.Body), Is.EqualTo("accepted"));
            }

            var successor = await reader.ReadResponseAsync(host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(successor.Status, Is.EqualTo(200));
                Assert.That(Encoding.ASCII.GetString(successor.Body), Is.EqualTo("healthy"));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RawBodyUnreadCleanupCannotBypassLimitAfterPublicDisposal(bool dispose)
        {
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                context =>
                {
                    if (dispose) context.Request.InputStream.Dispose();

                    context.SetHandled();
                    return Task.CompletedTask;
                },
                options => options.WithMaximumRequestBodyBytes(2)
            );
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            await peer
                .GetStream()
                .WriteAsync(
                    Encoding.ASCII.GetBytes(
                        $"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\n\r\n"
                    ),
                    host.Token
                );
            var reply = await new RawHttp1Reader(peer.GetStream()).ReadResponseAsync(host.Token);

            if (dispose)
            {
                using (Assert.EnterMultipleScope())
                {
                    // Disposing opened the body, so cleanup validates the rest before the response.
                    Assert.That(reply.Status, Is.EqualTo(413));
                    Assert.That(reply.Headers["connection"], Is.EqualTo("close"));
                }
            }

            else
            {
                // An unopened body does not hold back the handler's response. The connection
                // discards it afterwards, finds the excess byte and closes instead of reusing.
                Assert.That(reply.Status, Is.EqualTo(200));
                var end = new byte[1];

                try
                {
                    Assert.That(
                        await peer
                            .GetStream()
                            .ReadAsync(end, host.Token),
                        Is.Zero,
                        "The over-limit body closes the connection."
                    );
                }

                catch (IOException) { }
            }

            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
        }

        [Test]
        public async Task RawBodyHttp2RejectsKnownLengthBeforeContinueAndCountsPaddingSeparately()
        {
            var entered = 0;
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIO,
                null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    Interlocked.Increment(ref entered);
                    var bytes = await context.GetRequestBodyAsByteArrayAsync();
                    Assert.That(bytes, Is.EqualTo(Encoding.ASCII.GetBytes("ab")));
                    var trailers = context.Request as IHttpRequestTrailers
                        ?? throw new AssertionException("Missing H2 trailers.");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(trailers.TryGetTrailers(out var fields), Is.True);
                        Assert.That(
                            fields,
                            Has.Some
                                .Property("Key")
                                .EqualTo("x-raw-end")
                        );
                    }

                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(2)
            );
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Settings, 0, 0, [0, 1, 0, 0, 0, 0]);
            await peer.UntilAsync(frame => frame.Type == Http2Settings && (frame.Flags & 1) != 0);
            await peer.SendAsync(
                Http2Headers,
                EndHeaders,
                1,
                H2Peer.Literal(
                    (":method", "POST"),
                    (":scheme", "http"),
                    (":authority", "localhost:" + host.Port),
                    (":path", "/"),
                    ("content-length", "3"),
                    ("expect", "100-continue")
                )
            );
            var rejected = await peer.UntilAsync(frame => frame.Id == 1 && frame.Type == Http2Headers);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(RawBodyHpackStatus(rejected.Payload), Is.EqualTo(413));
                Assert.That(Volatile.Read(ref entered), Is.Zero);
            }

            await peer.AssertHealthyAsync(3);
            await peer.SendAsync(
                Http2Headers,
                EndHeaders,
                5,
                H2Peer.Literal(
                    (":method", "POST"),
                    (":scheme", "http"),
                    (":authority", "localhost:" + host.Port),
                    (":path", "/"),
                    ("content-length", "2")
                )
            );
            await peer.SendAsync(
                Http2Data,
                8,
                5,
                [
                    3,
                    (byte)'a',
                    0,
                    0,
                    0
                ]
            );
            await peer.SendAsync(Http2Data, 0, 5, [(byte)'b']);
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 5, H2Peer.Literal(("x-raw-end", "done")));
            using var response = new MemoryStream();

            while (true)
            {
                var frame = await peer.ReceiveAsync();
                Assert.That(frame.Type, Is.Not.EqualTo(Http2GoAway));

                if (frame.Id != 5) continue;

                Assert.That(frame.Type, Is.Not.EqualTo(Http2Reset));

                if (frame.Type == Http2Data) response.Write(frame.Payload);

                if ((frame.Flags & EndStream) != 0) break;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Encoding.ASCII.GetString(response.ToArray()), Is.EqualTo("accepted"));
                Assert.That(Volatile.Read(ref entered), Is.EqualTo(1));
            }

            await peer.AssertHealthyAsync(7);
        }

        private static int RawBodyQpackStatus(byte[] section)
        {
            Assert.That(section, Has.Length.GreaterThanOrEqualTo(3));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(section[0], Is.Zero);
                Assert.That(section[1], Is.Zero);
            }

            var offset = 2;

            if ((section[2] & 0xc0) == 0xc0)
                return RawBodyPrefix(section, ref offset, 6) switch
                {
                    25 => 200,
                    63 => 100,
                    var index => throw new AssertionException("Unexpected status index " + index)
                };

            using (Assert.EnterMultipleScope())
            {
                Assert.That(section[2] & 0xc0, Is.EqualTo(0x40));
                Assert.That(section[2] & 0x10, Is.EqualTo(0x10));
                Assert.That(RawBodyPrefix(section, ref offset, 4), Is.EqualTo(24), "Static :status name.");
                Assert.That(section[offset] & 0x80, Is.Zero, "The expected 413 has no shorter Huffman representation.");
                Assert.That(RawBodyPrefix(section, ref offset, 7), Is.EqualTo(3));
            }

            return int.Parse(Encoding.ASCII.GetString(section, offset, 3), CultureInfo.InvariantCulture);
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RawBodyFailureAfterCommitTerminatesAffectedTransportAndPreservesHealthyRequests(int version)
        {
            if (version == 3)
            {
                RequireQuic();

                if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            }

            var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ports = new ConcurrentQueue<int>();
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(
                version == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                version == 3 ? certificate : null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        ports.Enqueue(context.Request.RemoteEndPoint.Port);
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    context.Response.ContentLength64 = 10;
                    await context.Response.OutputStream.WriteAsync(new byte[] { (byte)'x' }, context.CancellationToken);
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                    await resume.Task.WaitAsync(context.CancellationToken);
                    _ = await context.GetRequestBodyAsByteArrayAsync();
                },
                options => options.WithMaximumRequestBodyBytes(2)
            );
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                        == certificate.GetCertHashString()
                }
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Settle,
                DefaultRequestVersion = new Version(version, version == 1 ? 1 : 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            client.DefaultRequestHeaders.Host = "localhost:" + host.Port;
            var address = new Uri((version == 3 ? "https" : "http") + "://127.0.0.1:" + host.Port + "/");

            try
            {
                Assert.That(await client.GetStringAsync(address, host.Token), Is.EqualTo("healthy"));
                using var upload = new HttpRequestMessage(HttpMethod.Post, address)
                {
                    Version = client.DefaultRequestVersion,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Content = new UnknownLengthContent([10, 20, 30])
                };
                using var response = await client.SendAsync(
                    upload,
                    HttpCompletionOption.ResponseHeadersRead,
                    host.Token
                );

                using (Assert.EnterMultipleScope())
                {
                    Assert.That((int)response.StatusCode, Is.EqualTo(200));
                    Assert.That(response.Version.Major, Is.EqualTo(version));
                }

                using var input = await response.Content.ReadAsStreamAsync(host.Token);
                var one = new byte[1];

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(await input.ReadAsync(one, host.Token), Is.EqualTo(1));
                    Assert.That(one[0], Is.EqualTo((byte)'x'));
                }

                resume.TrySetResult(true);
                var failure = await Assert.CatchAsync<Exception>(
                    async () => Assert.That(
                        await input.ReadAsync(one, host.Token),
                        Is.Zero,
                        "No extra response bytes are expected."
                    )
                );

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        failure,
                        Is
                            .InstanceOf<IOException>()
                            .Or.InstanceOf<HttpRequestException>(),
                        "Incomplete committed response must fail, not become a second final status or an unbounded wait."
                    );
                    Assert.That(await client.GetStringAsync(address, host.Token), Is.EqualTo("healthy"));
                }

                var endpoints = ports.ToArray();
                Assert.That(endpoints, Has.Length.EqualTo(2));

                if (version > 1) Assert.That(endpoints[1], Is.EqualTo(endpoints[0]));

                Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            }

            finally
            {
                resume.TrySetResult(true);
            }
        }

        [Test]
        public async Task RawBodyHttp3RejectsKnownLengthBeforeContinueAndExcludesFramingAndTrailers()
        {
            RequireQuic();

            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;

            var entered = 0;
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(
                HttpListenerMode.EmbedIOHttp3,
                certificate,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    Interlocked.Increment(ref entered);
                    Assert.That(
                        await context.GetRequestBodyAsByteArrayAsync(),
                        Is.EqualTo(Encoding.ASCII.GetBytes("ab"))
                    );
                    var trailers = context.Request as IHttpRequestTrailers
                        ?? throw new AssertionException("Missing H3 trailers.");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(trailers.TryGetTrailers(out var fields), Is.True);
                        Assert.That(
                            fields,
                            Has.Some
                                .Property("Key")
                                .EqualTo("x-raw-end")
                        );
                    }

                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(2)
            );
            await using var connection = await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, host.Port),
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundUnidirectionalStreams = 8,
                    ClientAuthenticationOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = new() { new System.Net.Security.SslApplicationProtocol("h3") },
                        RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                            == certificate.GetCertHashString()
                    }
                },
                host.Token
            );
            await using var control = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional,
                host.Token
            );
            await control.WriteAsync(new byte[] { 0, 4, 0 }, host.Token);

            async Task<(int Status, string Body)> Request(string method, int declared, bool data)
            {
                await using var stream = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    host.Token
                );
                var fields = new List<(string Name, string Value)>
                {
                    (":method", method),
                    (":scheme", "https"),
                    (":authority", "localhost:" + host.Port),
                    (":path", "/")
                };

                if (declared >= 0) fields.Add(("content-length", declared.ToString(CultureInfo.InvariantCulture)));

                if (declared == 3) fields.Add(("expect", "100-continue"));

                await H3Peer.SendFieldsAsync(stream, [.. fields], method == "GET", host.Token);

                if (data)
                {
                    // Unknown frame payload and frame headers are not entity bytes.
                    await stream.WriteAsync(
                        new byte[]
                        {
                            0x21,
                            3,
                            1,
                            2,
                            3,
                            0,
                            1,
                            (byte)'a',
                            0,
                            1,
                            (byte)'b'
                        },
                        host.Token
                    );
                    await H3Peer.SendFieldsAsync(stream, [("x-raw-end", "done")], true, host.Token);
                }

                var head = await H3Peer.ReadFrameAsync(stream, host.Token);
                Assert.That(head.Type, Is.EqualTo(1));
                var status = RawBodyQpackStatus(head.Payload);
                using var response = new MemoryStream();

                while (true)
                {
                    var frame = await H3Peer.ReadFrameAsync(stream, host.Token, true);

                    if (frame.Type < 0) break;

                    Assert.That(frame.Type, Is.Zero);
                    response.Write(frame.Payload);
                }

                return (status, Encoding.ASCII.GetString(response.ToArray()));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That((await Request("POST", 3, false)).Status, Is.EqualTo(413));
                Assert.That(Volatile.Read(ref entered), Is.Zero);
                Assert.That(await Request("GET", -1, false), Is.EqualTo((200, "healthy")));
                Assert.That(await Request("POST", -1, true), Is.EqualTo((200, "accepted")));
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Volatile.Read(ref entered), Is.EqualTo(1));
                Assert.That(await Request("GET", -1, false), Is.EqualTo((200, "healthy")));
            }

            await connection.CloseAsync(0x100, host.Token);
        }

        [TestCase(1, true, 2, 2)]
        [TestCase(1, true, 3, 2)]
        [TestCase(1, false, 2, 2)]
        [TestCase(1, false, 3, 2)]
        [TestCase(2, true, 2, 2)]
        [TestCase(2, true, 3, 2)]
        [TestCase(2, false, 2, 2)]
        [TestCase(2, false, 3, 2)]
        [TestCase(3, true, 2, 2)]
        [TestCase(3, true, 3, 2)]
        [TestCase(3, false, 2, 2)]
        [TestCase(3, false, 3, 2)]
        [TestCase(1, true, 0, 0)]
        [TestCase(1, false, 0, 0)]
        [TestCase(1, false, 1, 0)]
        [TestCase(2, true, 0, 0)]
        [TestCase(2, false, 0, 0)]
        [TestCase(2, false, 1, 0)]
        [TestCase(3, true, 0, 0)]
        [TestCase(3, false, 0, 0)]
        [TestCase(3, false, 1, 0)]
        public async Task RawEntityLimitExactAndOverPreserveHealthySiblingRequests(
            int version,
            bool knownLength,
            int length,
            int maximum
        )
        {
            if (version == 3)
            {
                RequireQuic();

                if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            }

            var body = new byte[length];

            for (var i = 0; i < body.Length; i++) body[i] = (byte)((i + 1) * 10);

            var entered = 0;
            var accepted = 0;
            var healthyPorts = new ConcurrentQueue<int>();
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(
                version == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                version == 3 ? certificate : null,
                async context =>
                {
                    if (context.Request.HttpMethod == "GET")
                    {
                        healthyPorts.Enqueue(context.Request.RemoteEndPoint.Port);
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                        return;
                    }

                    Interlocked.Increment(ref entered);
                    var input = context.Request.InputStream;
                    Assert.That(context.Request.InputStream, Is.SameAs(input));
                    var actual = await context.GetRequestBodyAsByteArrayAsync();
                    Assert.That(actual, Is.EqualTo(body));
                    Interlocked.Increment(ref accepted);
                    await context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding);
                },
                options => options.WithMaximumRequestBodyBytes(maximum)
            );

            if (version == 3)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var session = host.Server.Listener
                    .GetType()
                    .GetField("_session", flags)
                    ?.GetValue(host.Server.Listener)
                    ?? throw new AssertionException("Missing actual HTTP3 session.");
                var bindings = session
                    .GetType()
                    .GetField("_listeners", flags)
                    ?.GetValue(session) as IDictionary
                    ?? throw new AssertionException("Missing actual HTTP3 binding.");
                Assert.That(bindings, Has.Count.GreaterThan(0));

                foreach (var binding in bindings.Keys)
                {
                    Assert.That(binding.GetType().Name, Is.EqualTo("EmbedIOQuicListenerBinding"));
                    TestContext.Out.WriteLine("Actual HTTP3 provider=" + binding.GetType().FullName);
                }
            }

            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                        == certificate.GetCertHashString()
                }
            };
            using var client = new HttpClient(handler)
            {
                Timeout = Settle,
                DefaultRequestVersion = new Version(version, version == 1 ? 1 : 0),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            client.DefaultRequestHeaders.Host = "localhost:" + host.Port;
            var address = new Uri((version == 3 ? "https" : "http") + "://127.0.0.1:" + host.Port + "/");
            Assert.That(await client.GetStringAsync(address, host.Token), Is.EqualTo("healthy"));
            using var request = new HttpRequestMessage(HttpMethod.Post, address)
            {
                Version = new Version(version, version == 1 ? 1 : 0),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = knownLength ? new ByteArrayContent(body) : new UnknownLengthContent(body)
            };
            using var response = await client.SendAsync(request, host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.Version.Major, Is.EqualTo(version));
                Assert.That((int)response.StatusCode, Is.EqualTo(length <= maximum ? 200 : 413));
                Assert.That(Volatile.Read(ref accepted), Is.EqualTo(length <= maximum ? 1 : 0));
                Assert.That(Volatile.Read(ref entered), Is.EqualTo(knownLength && length > maximum ? 0 : 1));
            }

            using var healthy = new HttpRequestMessage(HttpMethod.Get, address)
            {
                Version = request.Version,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            using var successor = await client.SendAsync(healthy, host.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(successor.Version.Major, Is.EqualTo(version));
                Assert.That(await successor.Content.ReadAsStringAsync(host.Token), Is.EqualTo("healthy"));
            }

            var ports = healthyPorts.ToArray();
            Assert.That(ports, Has.Length.EqualTo(2));

            if (version > 1)
                Assert.That(ports[1], Is.EqualTo(ports[0]), "Healthy siblings must use the original shared transport.");

            Assert.That(host.Server.State, Is.EqualTo(WebServerState.Listening));
            TestContext.Out.WriteLine(
                $"Raw limit{maximum}; HTTP/{version}; known={knownLength}; entity={length}; listener={host.Server.Listener.GetType().FullName}; peer={typeof(HttpClient).Assembly.FullName}"
            );
        }
    }
}
