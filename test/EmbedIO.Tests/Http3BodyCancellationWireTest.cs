using System;
using System.Diagnostics;
using System.IO;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpApplicationLifecycleAuditTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task Http3CanceledReadSettlesBeforeReusingItsBuffer(bool disposeBody)
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3CanceledReadBufferCore(disposeBody);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3CanceledReadBufferCore(bool disposeBody)
        {
            using var certificate = Certificate();
            var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var verify = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                var buffer = new byte[16];
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                var prefix = 0;
                while (prefix < 3)
                {
                    var count = await context.Request.InputStream.ReadAsync(buffer.AsMemory(0, 3 - prefix), stop.Token);
                    if (count == 0) throw new EndOfStreamException("Missing race fixture prefix.");
                    prefix += count;
                }
                var pending = context.Request.InputStream.ReadAsync(buffer, stop.Token).AsTask();
                Assert.That(pending.IsCompleted, Is.False, "The provider must be waiting for the DATA payload.");
                reading.TrySetResult(true);
                await cancel.Task.WaitAsync(context.CancellationToken);
                stop.Cancel();
                if (disposeBody) context.Request.InputStream.Dispose();
                try { _ = await pending.WaitAsync(Settle); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                Array.Fill(buffer, (byte)0xa5);
                settled.TrySetResult(true);
                await verify.Task.WaitAsync(context.CancellationToken);
                Assert.That(buffer, Is.All.EqualTo((byte)0xa5), "No abandoned provider read can write into a reused caller buffer.");
                await context.SendStringAsync("settled", "text/plain", WebServer.Utf8NoBomEncoding);
            });
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
            // Leave the parser waiting inside DATA, so the provider borrows the
            // handler's actual buffer rather than only its frame-header scratch space.
            await request.WriteAsync(new byte[] { 0, 19, 1, 2, 3 }, host.Token);
            await reading.Task.WaitAsync(host.Token);
            try
            {
                cancel.TrySetResult(true);
                try { await request.WriteAsync(new byte[8], host.Token); }
                catch (QuicException error) when (error.QuicError == QuicError.StreamAborted && error.ApplicationErrorCode == 0x100) { }
                await settled.Task.WaitAsync(host.Token);
                try { await request.WriteAsync(new byte[8], host.Token); }
                catch (QuicException error) when (error.QuicError == QuicError.StreamAborted && error.ApplicationErrorCode == 0x100) { }
            }
            finally { verify.TrySetResult(true); }
            var head = await H3Peer.ReadFrameAsync(request, host.Token);
            Assert.That(RawBodyQpackStatus(head.Payload), Is.EqualTo(200));
            using var body = new MemoryStream();
            while (true)
            {
                var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                if (frame.Type < 0) break;
                if (frame.Type == 0) body.Write(frame.Payload);
            }
            Assert.That(Encoding.UTF8.GetString(body.ToArray()), Is.EqualTo("settled"));
            await peer.AssertHealthyAsync(host.Token);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http3RequestWideCancellationRetainsFatalStopCode(bool native)
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3RequestWideCancellationCore(native);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3RequestWideCancellationCore(bool native)
        {
            using var certificate = Certificate();
            var reading = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                var buffer = new byte[16];
                try
                {
                    var pending = context.Request.InputStream.ReadAsync(buffer, context.CancellationToken);
                    reading.TrySetResult(true);
                    while (await pending != 0)
                        pending = context.Request.InputStream.ReadAsync(buffer, context.CancellationToken);
                }
                finally { canceled.TrySetResult(context.CancellationToken.IsCancellationRequested); }
            }, native: native);
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
            await reading.Task.WaitAsync(host.Token);
            request.Abort(QuicAbortDirection.Read, 0x10c);
            var stopped = await Assert.CatchAsync<QuicException>(async () => await request.WritesClosed.WaitAsync(host.Token));
            Assert.That(stopped?.QuicError, Is.EqualTo(QuicError.StreamAborted));
            Assert.That(stopped?.ApplicationErrorCode, Is.EqualTo(0x10cL), "A request-wide failure is not graceful input abandonment.");
            Assert.That(await canceled.Task.WaitAsync(host.Token), Is.True);
            await peer.AssertHealthyAsync(host.Token);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task PrecanceledHttp3BodyReadDoesNotAbortUpload(bool native)
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await PrecanceledHttp3BodyReadCore(native);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task PrecanceledHttp3BodyReadCore(bool native)
        {
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                var buffer = new byte[16];
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    _ = await context.Request.InputStream.ReadAsync(buffer, new CancellationToken(true)));
                var total = 0;
                int count;
                while ((count = await context.Request.InputStream.ReadAsync(buffer, context.CancellationToken)) != 0) total += count;
                await context.SendStringAsync(total.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
            }, native: native);
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
            await request.WriteAsync(new byte[] { 0, 3, 1, 2, 3 }, completeWrites: true, host.Token);
            var head = await H3Peer.ReadFrameAsync(request, host.Token);
            Assert.That(RawBodyQpackStatus(head.Payload), Is.EqualTo(200));
            using var body = new MemoryStream();
            while (true)
            {
                var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                if (frame.Type < 0) break;
                if (frame.Type == 0) body.Write(frame.Payload);
            }
            Assert.That(Encoding.UTF8.GetString(body.ToArray()), Is.EqualTo("3"));
            await request.WritesClosed.WaitAsync(host.Token);
            await peer.AssertHealthyAsync(host.Token);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http3RateExpiryPreservesResponseAndGracefulUploadStop(bool native)
        {
            RequireQuic();
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported) return;
            await Http3RateExpiryCore(native);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task Http3RateExpiryCore(bool native)
        {
            using var certificate = Certificate();
            await using var host = await Host.StartAsync(HttpListenerMode.EmbedIOHttp3, certificate, async context =>
            {
                if (await Healthy(context)) return;
                var buffer = new byte[16];
                while (await context.Request.InputStream.ReadAsync(buffer, context.CancellationToken) != 0) { }
            }, options => options.WithMinimumRequestBodyDataRate(1000, TimeSpan.FromMilliseconds(1100)),
                server => server.OnHttpException = HttpExceptionHandler.PlainTextResponse, native: native);
            await using var peer = await H3Peer.ConnectAsync(host, certificate);
            await using var request = await peer.OpenRequestAsync("POST", "/upload", false);
            var clock = Stopwatch.StartNew();
            await request.WriteAsync(new byte[] { 0, 3, 1, 2, 3 }, host.Token);
            var head = await H3Peer.ReadFrameAsync(request, host.Token);
            Assert.That(RawBodyQpackStatus(head.Payload), Is.EqualTo(408));
            using var body = new MemoryStream();
            while (true)
            {
                var frame = await H3Peer.ReadFrameAsync(request, host.Token, allowEnd: true);
                if (frame.Type < 0) break;
                if (frame.Type == 0) body.Write(frame.Payload);
            }
            Assert.That(Encoding.UTF8.GetString(body.ToArray()), Does.Contain("minimum data rate"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)), "The production one-second heartbeat ends the stalled read.");
            var stopped = await Assert.CatchAsync<QuicException>(async () => await request.WritesClosed.WaitAsync(host.Token));
            Assert.That(stopped?.QuicError, Is.EqualTo(QuicError.StreamAborted));
            Assert.That(stopped?.ApplicationErrorCode, Is.EqualTo(0x100L));
            await peer.AssertHealthyAsync(host.Token);
        }
    }
}
