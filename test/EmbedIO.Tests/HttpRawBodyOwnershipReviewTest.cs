using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        [TestCase(1, true)]
        [TestCase(1, false)]
        [TestCase(2, true)]
        [TestCase(2, false)]
        [TestCase(3, true)]
        [TestCase(3, false)]
        public async Task RawOwnershipDisposedBeforePhysicalEofCannotPublishTrailers(int protocol, bool disposeBeforeEof)
        {
            if (protocol == 3)
            {
                RequireQuic();
                if (!QuicConnection.IsSupported) return;
            }
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = new TaskCompletionSource<(int Count, bool Pending, bool Available, int Fields)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                protocol == 3 ? certificate : null, async context =>
                {
                    var input = context.Request.InputStream;
                    var read = input.ReadAsync(new byte[1], 0, 1, context.CancellationToken);
                    var pending = !read.IsCompleted;
                    if (disposeBeforeEof) input.Dispose();
                    entered.TrySetResult(true);
                    var count = await read;
                    if (!disposeBeforeEof) input.Dispose();
                    var trailers = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing trailer capability.");
                    var available = trailers.TryGetTrailers(out var fields);
                    outcome.TrySetResult((count, pending, available, fields.Count));
                    await release.Task.WaitAsync(context.CancellationToken);
                    context.SetHandled();
                }, options => options.WithMaximumRequestBodyBytes(2));
            try
            {
                await RawOwnershipSendAsync(protocol, host, certificate, entered.Task, Array.Empty<byte>(), true, async () =>
                {
                    var observed = await outcome.Task.WaitAsync(host.Token);
                    Assert.That(observed.Pending, Is.True, "The first framing read must precede the peer's delayed terminal section.");
                    Assert.That(observed.Count, Is.Zero, "The delayed input has no DATA payload.");
                    Assert.That(observed.Available, Is.EqualTo(!disposeBeforeEof), "Only physical completion before application disposal preserves the snapshot.");
                    Assert.That(observed.Fields, Is.EqualTo(disposeBeforeEof ? 0 : 1));
                });
            }
            finally { release.TrySetResult(true); }
        }

        [TestCase(1, false, 0)]
        [TestCase(1, true, 0)]
        [TestCase(1, false, 1)]
        [TestCase(1, true, 1)]
        [TestCase(2, false, 0)]
        [TestCase(2, true, 0)]
        [TestCase(2, false, 1)]
        [TestCase(2, true, 1)]
        [TestCase(3, false, 0)]
        [TestCase(3, true, 0)]
        [TestCase(3, false, 1)]
        [TestCase(3, true, 1)]
        public async Task RawOwnershipTelemetryCountsOnlyApplicationReadBytes(int protocol, bool limited, int applicationReads)
        {
            if (protocol == 3)
            {
                RequireQuic();
                if (!QuicConnection.IsSupported) return;
            }
            var name = "raw-review-" + Guid.NewGuid().ToString("N");
            using var capture = new RawOwnershipMetrics(name);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supplied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                protocol == 3 ? certificate : null, async context =>
                {
                    entered.TrySetResult(true);
                    await supplied.Task.WaitAsync(context.CancellationToken);
                    if (applicationReads != 0)
                    {
                        var bytes = new byte[applicationReads];
                        await context.Request.InputStream.ReadExactlyAsync(bytes, context.CancellationToken);
                    }
                    context.Response.KeepAlive = false;
                    context.SetHandled();
                }, options => options.WithMaximumRequestBodyBytes(limited ? 2 : null).WithTelemetry(name));
            await RawOwnershipSendAsync(protocol, host, certificate, entered.Task, new byte[] { 1, 2 }, false, async () =>
            {
                supplied.TrySetResult(true);
                var measured = await capture.Value.Task.WaitAsync(host.Token);
                Assert.That(measured, Is.EqualTo(applicationReads), "Internal cleanup must not become application-read telemetry.");
            });
        }

        [Test]
        public async Task RawOwnershipFinalHttp2ExcessProbeCannotPublishTrailersOrCountPrivateByte()
        {
            var name = "raw-review-" + Guid.NewGuid().ToString("N");
            using var capture = new RawOwnershipMetrics(name);
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource<(int Count, int Status, bool Available)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                entered.TrySetResult(true);
                var bytes = new byte[2];
                await context.Request.InputStream.ReadExactlyAsync(bytes, context.CancellationToken);
                var error = await Assert.CatchAsync<HttpException>(async () =>
                    _ = await context.Request.InputStream.ReadAsync(new byte[1], context.CancellationToken));
                var trailers = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing trailer capability.");
                observed.TrySetResult((bytes.Length, error?.StatusCode ?? 0, trailers.TryGetTrailers(out _)));
                context.SetHandled();
            }, options => options.WithMaximumRequestBodyBytes(2).WithTelemetry(name));
            await RawOwnershipSendAsync(2, host, certificate, entered.Task, new byte[] { 1, 2, 3 }, false, async () =>
            {
                var result = await observed.Task.WaitAsync(host.Token);
                var measured = await capture.Value.Task.WaitAsync(host.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(result.Count, Is.EqualTo(2));
                    Assert.That(result.Status, Is.EqualTo(413));
                    Assert.That(result.Available, Is.False, "Physical DATA completion is not successful raw-policy input.");
                    Assert.That(measured, Is.EqualTo(2), "The private excess-byte probe is never application data.");
                });
            });
        }

        private sealed class RawOwnershipMetrics : IDisposable
        {
            private readonly MeterListener _listener = new();
            internal readonly TaskCompletionSource<long> Value = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal RawOwnershipMetrics(string name)
            {
                _listener.InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == "EmbedIO.Server" && instrument.Name == "http.server.request.body.size")
                        listener.EnableMeasurementEvents(instrument);
                };
                _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
                {
                    foreach (var tag in tags)
                        if (tag.Key == "embedio.server.name" && Equals(tag.Value, name)) Value.TrySetResult(value);
                });
                _listener.Start();
            }
            public void Dispose() => _listener.Dispose();
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task RawOwnershipFinalPositivePhysicalCompletionSurvivesDisposal(int protocol)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = new TaskCompletionSource<(int Count, bool Before, bool After)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                var input = context.Request.InputStream;
                entered.TrySetResult(true);
                var count = await input.ReadAsync(new byte[2], context.CancellationToken);
                var trailers = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing trailer capability.");
                var before = trailers.TryGetTrailers(out _);
                input.Dispose();
                var after = trailers.TryGetTrailers(out _);
                outcome.TrySetResult((count, before, after));
                await release.Task.WaitAsync(context.CancellationToken);
                context.SetHandled();
            }, options => options.WithMaximumRequestBodyBytes(2));
            try
            {
                await RawOwnershipSendAsync(protocol, host, certificate, entered.Task, new byte[] { 1, 2 }, false, async () =>
                {
                    var observed = await outcome.Task.WaitAsync(host.Token);
                    Assert.That(observed.Count, Is.EqualTo(2), "The final read returns data, not a zero-byte EOF probe.");
                    Assert.That(observed.Before, Is.True, "The physical framing owner already validated completion.");
                    Assert.That(observed.After, Is.True, "Disposal must preserve a snapshot established by a final positive read.");
                }, fixedLength: true);
            }
            finally { release.TrySetResult(true); }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task RawOwnershipEmptyPhysicalCompletionSurvivesDisposal(int protocol)
        {
            if (protocol == 3)
            {
                RequireQuic();
                if (!QuicConnection.IsSupported) return;
            }
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = new TaskCompletionSource<(int Count, bool Before, bool After)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                protocol == 3 ? certificate : null, async context =>
                {
                    var input = context.Request.InputStream;
                    entered.TrySetResult(true);
                    var count = await input.ReadAsync(new byte[1], context.CancellationToken);
                    var trailers = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing trailer capability.");
                    var before = trailers.TryGetTrailers(out _);
                    input.Dispose();
                    var after = trailers.TryGetTrailers(out _);
                    outcome.TrySetResult((count, before, after));
                    await release.Task.WaitAsync(context.CancellationToken);
                    context.SetHandled();
                }, options => options.WithMaximumRequestBodyBytes(2));
            try
            {
                await RawOwnershipSendAsync(protocol, host, certificate, entered.Task, Array.Empty<byte>(), false, async () =>
                {
                    var observed = await outcome.Task.WaitAsync(host.Token);
                    Assert.That(observed.Count, Is.Zero);
                    Assert.That(observed.Before, Is.True);
                    Assert.That(observed.After, Is.True, "An already complete empty entity remains valid after disposal.");
                }, fixedLength: true);
            }
            finally { release.TrySetResult(true); }
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task RawOwnershipPendingPhysicalReadCannotPublishBeforeRawOutcome(int entityLength)
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = new TaskCompletionSource<(IHttpRequestTrailers Request, bool PhysicalAvailable)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settled = new TaskCompletionSource<(int Count, int Status, bool Available)>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new System.Threading.ManualResetEventSlim();
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIO, null, async context =>
            {
                var request = context.Request as IHttpRequestTrailers ?? throw new AssertionException("Missing trailer capability.");
                var exchange = context.Request.GetType().GetField("_exchange", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(context.Request) ?? throw new AssertionException("Missing physical exchange.");
                var physical = exchange.GetType().GetProperty("InputStream", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(exchange) as Stream ?? throw new AssertionException("Missing physical body.");
                var consumed = physical.GetType().GetField("_consumed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing physical consumption callback.");
                var original = consumed.GetValue(physical) as Action<int> ?? throw new AssertionException("Missing consumption owner.");
                consumed.SetValue(physical, new Action<int>(count =>
                {
                    original(count);
                    if (count != (entityLength == 2 ? 2 : 1)) return;
                    var arguments = new object?[] { null };
                    var available = physical.GetType().GetMethod("TryGetTrailers")?.Invoke(physical, arguments) as bool?
                        ?? throw new AssertionException("Missing physical trailer state.");
                    held.TrySetResult((request, available));
                    if (!release.Wait(Settle)) throw new TimeoutException("The controlled physical-read gate was not released.");
                }));
                entered.TrySetResult(true);
                var count = await context.Request.InputStream.ReadAsync(new byte[2], context.CancellationToken);
                var status = 0;
                if (entityLength == 3)
                {
                    var error = await Assert.CatchAsync<HttpException>(async () =>
                        _ = await context.Request.InputStream.ReadAsync(new byte[1], context.CancellationToken));
                    status = error?.StatusCode ?? 0;
                }
                settled.TrySetResult((count, status, request.TryGetTrailers(out _)));
                context.SetHandled();
            }, options => options.WithMaximumRequestBodyBytes(2));
            await RawOwnershipSendAsync(2, host, certificate, entered.Task, entityLength == 2 ? new byte[] { 1, 2 } : new byte[] { 1, 2, 3 }, false, async () =>
            {
                bool pendingAvailable;
                (IHttpRequestTrailers Request, bool PhysicalAvailable) phase;
                try
                {
                    phase = await held.Task.WaitAsync(host.Token);
                    pendingAvailable = phase.Request.TryGetTrailers(out _);
                }
                finally { release.Set(); }
                var outcome = await settled.Task.WaitAsync(host.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(phase.PhysicalAvailable, Is.True, "The owned H2 body published physical EOF before its ReadAsync returned.");
                    Assert.That(pendingAvailable, Is.False, "The raw read outcome is still pending, including a possible private excess byte.");
                    Assert.That(outcome.Count, Is.EqualTo(2));
                    Assert.That(outcome.Status, Is.EqualTo(entityLength == 2 ? 0 : 413));
                    Assert.That(outcome.Available, Is.EqualTo(entityLength == 2), "A settled final positive read remains valid; excess remains unavailable.");
                });
            });
        }

        private static async Task RawOwnershipSendAsync(int protocol, Host host, X509Certificate2 certificate,
            Task entered, byte[] payload, bool trailers, Func<Task> observe, bool fixedLength = false)
        {
            if (protocol == 1)
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
                var wire = peer.GetStream();
                var framing = fixedLength ? "Content-Length: " + payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "Transfer-Encoding: chunked";
                await wire.WriteAsync(Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: localhost:{host.Port}\r\n{framing}\r\n\r\n"), host.Token);
                await entered.WaitAsync(host.Token);
                var data = payload.Length == 0 ? "" : payload.Length.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" + Encoding.ASCII.GetString(payload) + "\r\n";
                var entity = fixedLength ? payload : Encoding.ASCII.GetBytes(data + "0\r\n" + (trailers ? "X-Ownership-End: done\r\n" : "") + "\r\n");
                await wire.WriteAsync(entity, host.Token);
                await observe();
            }
            else if (protocol == 2)
            {
                await using var peer = await H2Peer.ConnectAsync(host);
                await peer.SendAsync(Http2Headers, EndHeaders, 1, peer.Request("POST", "/"));
                await entered.WaitAsync(host.Token);
                if (payload.Length != 0) await peer.SendAsync(Http2Data, trailers ? 0 : EndStream, 1, payload);
                if (trailers) await peer.SendAsync(Http2Headers, EndHeaders | EndStream, 1, H2Peer.Literal(("x-ownership-end", "done")));
                else if (payload.Length == 0) await peer.SendAsync(Http2Data, EndStream, 1, Array.Empty<byte>());
                await observe();
            }
            else
            {
                RequireQuic();
                if (!QuicConnection.IsSupported) return;
                await using var peer = await H3Peer.ConnectAsync(host, certificate);
                await using var request = await peer.OpenRequestAsync("POST", "/", false);
                await entered.WaitAsync(host.Token);
                if (payload.Length != 0) await request.WriteAsync(new byte[] { 0, (byte)payload.Length }.Concat(payload).ToArray(), host.Token);
                if (trailers) await H3Peer.SendFieldsAsync(request, new[] { ("x-ownership-end", "done") }, true, host.Token);
                else request.CompleteWrites();
                await observe();
            }
        }
    }
}
