using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        private static void AssertTrailerSnapshot(IHttpRequest request)
        {
            var capability = request as IHttpRequestTrailers ?? throw new AssertionException("Missing request trailer capability.");
            Assert.That(capability.TryGetTrailers(out var trailers), Is.True);
            Assert.That(trailers.Select(pair => pair.Value), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(request.Headers["x-check"], Is.Null);
            var mutable = trailers as IList<KeyValuePair<string, string>> ?? throw new AssertionException("Missing immutable list view.");
            Assert.That(mutable.IsReadOnly, Is.True);
            Assert.Throws<NotSupportedException>(() => mutable[0] = new KeyValuePair<string, string>("x-check", "changed"));
        }

        [Test]
        public async Task Http2TrailerSnapshotOwnsDecodedFieldsAndCancelingAReadDoesNotPublishEarly()
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var assembly = typeof(WebServer).Assembly;
            var bodyType = assembly.GetType("EmbedIO.Net.Internal.Http2.Http2RequestBody", true) ?? throw new AssertionException("Missing body.");
            var fieldType = assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true) ?? throw new AssertionException("Missing field.");
            var blockType = assembly.GetType("EmbedIO.Net.Internal.Http2.Http2HeaderBlock", true) ?? throw new AssertionException("Missing block.");
            using var body = (Stream)(Activator.CreateInstance(bodyType, flags, null, new object?[] { 1, null, (Action<int>)(_ => { }) }, null)
                ?? throw new AssertionException("Missing body instance."));
            var snapshotMethod = bodyType.GetMethod("TryGetTrailers", flags) ?? throw new AssertionException("Missing snapshot method.");
            bool Snapshot(out IReadOnlyList<KeyValuePair<string, string>> lines)
            {
                var arguments = new object?[] { null };
                var available = (bool)(snapshotMethod.Invoke(body, arguments) ?? throw new AssertionException("Missing availability."));
                lines = arguments[0] as IReadOnlyList<KeyValuePair<string, string>> ?? throw new AssertionException("Missing snapshot.");
                return available;
            }
            using var cancel = new System.Threading.CancellationTokenSource();
            var pending = body.ReadAsync(new byte[1], 0, 1, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
            Assert.That(Snapshot(out var unavailable), Is.False);
            Assert.That(unavailable, Is.Empty);
            var fields = Array.CreateInstance(fieldType, 1);
            object Field(string value) => Activator.CreateInstance(fieldType, flags, null, new object[] { "x-check", value, false }, null)
                ?? throw new AssertionException("Missing field instance.");
            fields.SetValue(Field("original"), 0);
            var block = Activator.CreateInstance(blockType, flags, null, new object[] { 1, true, fields, 0u }, null)
                ?? throw new AssertionException("Missing block instance.");
            (bodyType.GetMethod("CompleteTrailers", flags) ?? throw new AssertionException("Missing completion method.")).Invoke(body, new[] { block });
            fields.SetValue(Field("decoder-array-mutated"), 0);
            Assert.That(await body.ReadAsync(new byte[1]), Is.Zero);
            Assert.That(Snapshot(out var snapshot), Is.True);
            Assert.That(snapshot.Single().Value, Is.EqualTo("original"));
            body.Dispose();
            Assert.That(snapshot.Single().Value, Is.EqualTo("original"));
        }
        [TestCase(0, false)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public async Task Http1FixedLengthAndBodylessInputPublishAnEmptySnapshotWithoutEagerReads(int length, bool abandon)
        {
            var entered = new TaskCompletionSource<IHttpRequestTrailers>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                var capability = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing request capability.");
                try
                {
                    Assert.That(capability.TryGetTrailers(out var initial), Is.EqualTo(length == 0));
                    Assert.That(initial, Is.Empty);
                    entered.SetResult(capability);
                    if (abandon) context.Request.InputStream.Dispose();
                    using var content = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(content, context.CancellationToken);
                    Assert.That(content.Length, Is.EqualTo(length));
                    Assert.That(capability.TryGetTrailers(out var completed), Is.EqualTo(!abandon));
                    Assert.That(completed, Is.Empty);
                    finished.SetResult();
                }
                catch (Exception error) { entered.TrySetException(error); finished.SetException(error); throw; }
            },
            // Reading after disposal reaches the physical HTTP/1 body, which the handler gets
            // only without a body limit, read deadline or minimum data rate. With any of those
            // defaults, the request stream is a view that throws ObjectDisposedException once
            // disposed, as HTTP/2 and HTTP/3 bodies do; ReadingADisposedHttp1BodyThrowsUnderTheDefaults
            // pins that.
            options => { if (abandon) options.WithMaximumRequestBodyBytes(null).WithRequestBodyReadTimeout(null).WithMinimumRequestBodyDataRate(null); });
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nContent-Length: {length}\r\n\r\n"), host.Token);
            var capability = await entered.Task.WaitAsync(Settle);
            if (length != 0)
            {
                Assert.That(capability.TryGetTrailers(out var pending), Is.False);
                Assert.That(pending, Is.Empty);
                await tcp.GetStream().WriteAsync(Encoding.ASCII.GetBytes("abc"), host.Token);
            }
            await finished.Task.WaitAsync(Settle);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task Http1RequestTrailersPublishOnlyAfterAValidTerminalLine(bool malformed)
        {
            var entered = new TaskCompletionSource<IHttpRequestTrailers>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                var capability = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing request capability.");
                entered.SetResult(capability);
                try
                {
                    Assert.That(capability.TryGetTrailers(out var unavailable), Is.False);
                    Assert.That(unavailable, Is.Empty);
                    using var content = new MemoryStream();
                    if (malformed)
                    {
                        await Assert.ThrowsAsync<InvalidDataException>(async () => await context.Request.InputStream.CopyToAsync(content, context.CancellationToken));
                        Assert.That(capability.TryGetTrailers(out unavailable), Is.False);
                    }
                    else
                    {
                        await context.Request.InputStream.CopyToAsync(content, context.CancellationToken);
                        Assert.That(Encoding.ASCII.GetString(content.ToArray()), Is.EqualTo("abc"));
                        AssertTrailerSnapshot(context.Request);
                    }
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); throw; }
            });
            using var tcp = new TcpClient { NoDelay = true };
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
            var wire = tcp.GetStream();
            await wire.WriteAsync(Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n0\r\nX-Check: one\r\nX-Check: two\r\n"), host.Token);
            var capability = await entered.Task.WaitAsync(Settle);
            Assert.That(capability.TryGetTrailers(out var pending), Is.False);
            Assert.That(pending, Is.Empty);
            await wire.WriteAsync(Encoding.ASCII.GetBytes(malformed ? "Content-Length: 3\r\n\r\n" : "\r\n"), host.Token);
            await finished.Task.WaitAsync(Settle);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http2RequestTrailersWaitForQueuedBodyConsumptionAndNeverSurviveReset(bool reset)
        {
            var entered = new TaskCompletionSource<IHttpRequestTrailers>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                var capability = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing request capability.");
                entered.SetResult(capability);
                try
                {
                    await release.Task.WaitAsync(Settle);
                    Assert.That(capability.TryGetTrailers(out var pending), Is.False);
                    using var content = new MemoryStream();
                    if (reset)
                    {
                        var failure = await Assert.CatchAsync<Exception>(async () => await context.Request.InputStream.CopyToAsync(content));
                        // Reset cancels the context and fails its body. Context disposal
                        // races the resumed reader, so verify both concrete outcomes. The
                        // disposed object is the body, or the data-rate view the default
                        // rate wraps it in, which context close retires.
                        if (failure is ObjectDisposedException disposed)
                            Assert.That(disposed.ObjectName, Is.EqualTo("Http2RequestBody").Or.EqualTo("RequestBodyRateStream"));
                        else
                        {
                            Assert.That(failure, Is.TypeOf<IOException>());
                            Assert.That(failure?.InnerException, Is.TypeOf<IOException>());
                            Assert.That(failure?.InnerException?.Message, Is.EqualTo("Peer reset the HTTP/2 stream."));
                        }
                        Assert.That(context.CancellationToken.IsCancellationRequested, Is.True);
                        Assert.That(capability.TryGetTrailers(out pending), Is.False);
                    }
                    else
                    {
                        await context.Request.InputStream.CopyToAsync(content, context.CancellationToken);
                        Assert.That(content.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
                        AssertTrailerSnapshot(context.Request);
                    }
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); throw; }
            },
            // After a reset, the disposed object is the physical HTTP/2 body only without a body
            // limit or read deadline. With either default, context close retires the view in
            // front of it first, and the failed read names that view instead.
            options => { if (reset) options.WithMaximumRequestBodyBytes(null).WithRequestBodyReadTimeout(null); });
            await using var peer = await H2Peer.ConnectAsync(host);
            await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Request("POST", "/"));
            var capability = await entered.Task.WaitAsync(Settle);
            await peer.SendAsync(Http2Data, 0, 1, new byte[] { 1, 2, 3 });
            await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, H2Peer.Literal(("x-check", "one"), ("x-check", "two")));
            if (reset) await peer.SendAsync(Http2Reset, 0, 1, new byte[] { 0, 0, 0, 8 });
            await peer.SendAsync(Http2Ping, 0, 0, new byte[8]);
            await peer.UntilAsync(frame => frame.Type == Http2Ping && (frame.Flags & 1) != 0);
            Assert.That(capability.TryGetTrailers(out var queued), Is.False);
            Assert.That(queued, Is.Empty);
            release.SetResult();
            await finished.Task.WaitAsync(Settle);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http3ApplicationDisposalCannotPublishLateFinButKeepsCompletedSnapshot(bool abandon)
        {
            RequireQuic();
            if (!QuicConnection.IsSupported) return;
            await RunRequestTrailerDisposal(abandon);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task RunRequestTrailerDisposal(bool abandon)
        {
            var entered = new TaskCompletionSource<(Stream Input, Task<int> Read, IHttpRequestTrailers Request)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                try
                {
                    var request = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing request capability.");
                    var input = context.Request.InputStream;
                    var pending = input.ReadAsync(new byte[1], 0, 1, context.CancellationToken);
                    entered.SetResult((input, pending, request));
                    Assert.That(await pending, Is.Zero);
                    readSettled.SetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                }
                catch (Exception error)
                {
                    entered.TrySetException(error);
                    readSettled.TrySetException(error);
                    throw;
                }
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var stream = await peer.OpenRequestAsync("POST", "/", false);
            try
            {
                var observed = await entered.Task.WaitAsync(Settle);
                Assert.That(observed.Read.IsCompleted, Is.False, "The original body read must be pending before disposal or peer FIN.");
                Assert.That(observed.Request.TryGetTrailers(out var initial), Is.False);
                Assert.That(initial, Is.Empty);
                if (abandon)
                {
                    observed.Input.Dispose();
                    Assert.That(observed.Read.IsCompleted, Is.False, "Application disposal cannot settle or dispose the borrowed QUIC read.");
                }
                else
                    await H3Peer.SendFieldsAsync(stream, new[] { ("x-check", "one"), ("x-check", "two") }, false, host.Token);
                stream.CompleteWrites();
                await readSettled.Task.WaitAsync(Settle);
                Assert.That(observed.Read.IsCompletedSuccessfully, Is.True);
                if (abandon)
                {
                    Assert.That(observed.Request.TryGetTrailers(out var unavailable), Is.False,
                        "Late valid FIN cannot turn an application-abandoned input into successful metadata.");
                    Assert.That(unavailable, Is.Empty);
                }
                else
                {
                    AssertTrailerSnapshot(observed.Request);
                    observed.Input.Dispose();
                    AssertTrailerSnapshot(observed.Request);
                }
            }
            finally { release.TrySetResult(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http3RequestTrailersWaitForFinAndInvalidFollowingDataCannotPublish(bool malformed)
        {
            RequireQuic();
            if (!QuicConnection.IsSupported) return;
            await RunRequestTrailerFin(malformed);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task RunRequestTrailerFin(bool malformed)
        {
            var entered = new TaskCompletionSource<IHttpRequestTrailers>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                var capability = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing request capability.");
                entered.SetResult(capability);
                try
                {
                    using var content = new MemoryStream();
                    if (malformed)
                    {
                        await Assert.CatchAsync<IOException>(async () => await context.Request.InputStream.CopyToAsync(content, context.CancellationToken));
                        Assert.That(capability.TryGetTrailers(out var unavailable), Is.False);
                        Assert.That(unavailable, Is.Empty);
                    }
                    else
                    {
                        await context.Request.InputStream.CopyToAsync(content, context.CancellationToken);
                        AssertTrailerSnapshot(context.Request);
                    }
                    finished.SetResult();
                }
                catch (Exception error) { finished.SetException(error); throw; }
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var stream = await peer.OpenRequestAsync("POST", "/", false);
            var capability = await entered.Task.WaitAsync(Settle);
            await H3Peer.SendFieldsAsync(stream, new[] { ("x-check", "one"), ("x-check", "two") }, false, host.Token);
            Assert.That(capability.TryGetTrailers(out var pending), Is.False);
            Assert.That(pending, Is.Empty);
            Assert.That(finished.Task.IsCompleted, Is.False, "Trailers alone do not finish HTTP/3 input.");
            if (malformed) await stream.WriteAsync(new byte[] { 0, 1, 7 }, true, host.Token);
            else stream.CompleteWrites();
            await finished.Task.WaitAsync(Settle);
        }
    }
}
