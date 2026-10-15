using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #424: a minimum request body data rate, modelled on Kestrel's MinRequestBodyDataRate.
    // The heartbeat that checks it ticks once per second in production. These cases shorten it
    // to 200 ms through reflection, so a refusal takes well under a second, and restore it.
    public partial class Issue136_RequestLimits
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly Type HeartbeatType = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RequestBodyRateHeartbeat")
            ?? throw new AssertionException("Missing request body rate heartbeat.");
        // Valid only under the shortened heartbeat, so it is created where it is used.
        private static MinimumDataRate Tight => new(1000, TimeSpan.FromMilliseconds(400));

        private static int MonitoredBodies => (int)(HeartbeatType.GetProperty("MonitoredCount", Hidden)?.GetValue(null)
            ?? throw new AssertionException("Missing monitored body count."));

        private sealed class FastHeartbeat : IDisposable
        {
            private static readonly PropertyInfo Interval = HeartbeatType.GetProperty("Interval", Hidden)
                ?? throw new AssertionException("Missing heartbeat interval.");
            private static readonly TimeSpan Default = (TimeSpan)(HeartbeatType.GetField("DefaultInterval", Hidden)?.GetValue(null)
                ?? throw new AssertionException("Missing default heartbeat interval."));
            public FastHeartbeat()
            {
                Assert.That(MonitoredBodies, Is.Zero, "No body is monitored before the case starts.");
                Interval.SetValue(null, TimeSpan.FromMilliseconds(200));
            }
            public void Dispose() => Interval.SetValue(null, Default);
        }

        // ---------------------------------------------------------------- refusals

        // A client that trickles its body to a reading handler is refused with 408 once the rate,
        // measured over the time the handler waited, stays below the minimum past the grace
        // period. The connection closes; the server stays healthy. Both read paths are covered:
        // the handler's asynchronous read and a synchronous Read on the request stream.
        // The HTTP/1.1 clients here send three bytes 50 ms apart, well inside the 400 ms grace
        // period, and then stall: a client still sending when the server closes can see a
        // reset that discards the 408 already sent (CI run 38781253883 on Windows TLS), which
        // is the documented limitation of HTTP/1.1 refusals, not of the rate. The HTTP/2 and
        // HTTP/3 cases below keep trickling throughout.
        [TestCase(false)]
        [TestCase(true)]
        public async Task TrickledHttp1BodyIsRefusedAfterTheGracePeriod(bool synchronousRead)
        {
            using var fast = new FastHeartbeat();
            await using var host = Host.Start(o => o.WithMinimumRequestBodyDataRate(Tight),
                synchronousRead ? EchoLengthSynchronously : Host.EchoLength, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 100000\r\n\r\n");
            var clock = Stopwatch.StartNew();
            await TrickleBytesAsync(socket, 3, TimeSpan.FromMilliseconds(50));
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 408"));
            Assert.That(wire, Does.Contain("Connection: close"));
            Assert.That(wire, Does.Contain("minimum data rate"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)), "The refusal follows the grace period, not the client's pace.");
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
            Assert.That(MonitoredBodies, Is.Zero, "A refused body leaves the heartbeat.");
        }

        // Over TLS the canceled read leaves SslStream mid-record; the 408 is still written and
        // the connection closed, as it would be for a stalled read deadline.
        [Test]
        public async Task TrickledHttp1TlsBodyIsRefusedAfterTheGracePeriod()
        {
            using var fast = new FastHeartbeat();
            using var certificate = EmbedIO.PlatformTests.HttpsSmoke.CreateCertificate();
            var url = Resources.GetServerAddress().Replace("http://", "https://", StringComparison.Ordinal);
            var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate).WithMinimumRequestBodyDataRate(Tight))
                .WithModule(new EmbedIO.Actions.ActionModule("/", HttpVerbs.Any, Host.EchoLength));
            server.OnHttpException = HttpExceptionHandler.PlainTextResponse;
            await using var host = new Host(server, url);
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
            using var tls = new System.Net.Security.SslStream(client.GetStream(), false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync("localhost");
            var head = System.Text.Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 100000\r\n\r\n");
            await tls.WriteAsync(head, 0, head.Length);
            for (var i = 0; i < 3; ++i)
            {
                await tls.WriteAsync(new[] { (byte)'x' }, 0, 1);
                await Task.Delay(50);
            }
            var received = new MemoryStream();
            var buffer = new byte[4096];
            using var timeout = new CancellationTokenSource(Deadline);
            try
            {
                int read;
                while ((read = await tls.ReadAsync(buffer, 0, buffer.Length, timeout.Token)) > 0) received.Write(buffer, 0, read);
            }
            catch (IOException) { }
            var wire = System.Text.Encoding.Latin1.GetString(received.ToArray());
            Assert.That(wire, Does.StartWith("HTTP/1.1 408"));
            Assert.That(wire, Does.Contain("Connection: close"));
            Assert.That(wire, Does.Contain("minimum data rate"));
            Assert.That(MonitoredBodies, Is.Zero);
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task TrickledMultiplexedBodyIsRefusedAfterTheGracePeriod(int protocol)
        {
            using var fast = new FastHeartbeat();
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithMinimumRequestBodyDataRate(Tight), Host.EchoLength, HttpVerbs.Any);
            using var content = new SlowContent(1, TimeSpan.FromMilliseconds(50), 200, swallowErrors: true);
            var clock = Stopwatch.StartNew();
            using var response = await host.SendAsync(HttpMethod.Post, content);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestTimeout));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("minimum data rate"));
            Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
            using var healthy = await host.SendAsync(HttpMethod.Post, new ByteArrayContent(new byte[10]));
            Assert.That(await healthy.Content.ReadAsStringAsync(), Is.EqualTo("10"), "The connection carries on after the refused stream.");
        }

        // ---------------------------------------------------------------- acceptances

        // Fifty bytes every 100 ms is 500 bytes per second against a 100-byte minimum.
        [TestCase(1)]
        [TestCase(2)]
        public async Task BodyArrivingAtTheRateCompletes(int protocol)
        {
            using var fast = new FastHeartbeat();
            await using var host = Host.Start(o => o.WithMinimumRequestBodyDataRate(100, TimeSpan.FromMilliseconds(400)), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            using var response = await client.SendAsync(Post(client, host.Url, new SlowContent(50, TimeSpan.FromMilliseconds(100), 15)));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("750"));
            Assert.That(MonitoredBodies, Is.Zero, "A body read to its end leaves the heartbeat.");
        }

        // Time the handler spends not reading does not count: the client trickles for three
        // grace periods while the handler waits, then sends the rest, and only then is the
        // handler released to read the whole body. A fixed handler sleep raced the client: on a
        // slow runner the trickle outlasted it (CI run 38781253883, macOS), the handler then read
        // a body that really was arriving below the rate, and the monitor rightly refused it.
        [Test]
        public async Task BodyTheHandlerIsNotReadingIsNotRefused()
        {
            using var fast = new FastHeartbeat();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = Host.Start(o => o.WithMinimumRequestBodyDataRate(Tight), async context =>
            {
                await release.Task.WaitAsync(Deadline);
                await Host.EchoLength(context);
            }, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 2000\r\nConnection: close\r\n\r\n");
            await TrickleBytesAsync(socket, 24, TimeSpan.FromMilliseconds(50));
            await socket.SendBytesAsync(new byte[2000 - 24]);
            await Task.Delay(100);
            release.TrySetResult(true);
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire, Does.Contain("\r\n2000\r\n"), "The whole body was read.");
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task LargeUploadIsUnaffectedByTheDefaultRate(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, _ => { }, Host.EchoLength, HttpVerbs.Any);
            using var response = await host.SendAsync(HttpMethod.Post, new ByteArrayContent(new byte[4 * 1024 * 1024]));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("4194304"));
            Assert.That(MonitoredBodies, Is.Zero);
        }

        // Null disables the rate server-wide and a route can set one; a route can also lift
        // the server's rate with MinimumDataRate.None.
        [Test]
        public async Task NullDisablesTheRateAndRoutesReplaceIt()
        {
            using var fast = new FastHeartbeat();
            await using (var lenient = Host.Start(o => o.WithMinimumRequestBodyDataRate(null)
                .WithRouteRequestLimits("/strict", p => p.MinimumRequestBodyDataRate = Tight), Host.EchoLength, HttpVerbs.Any))
            {
                Assert.That(await TrickleRequestAsync(lenient, "/", 20), Does.StartWith("HTTP/1.1 200").And.Contain("\r\n20\r\n"), "Null leaves a trickle alone.");
                Assert.That(await TrickleRequestAsync(lenient, "/strict", 3), Does.StartWith("HTTP/1.1 408"), "The route sets a rate the server lacks.");
            }
            await using var strict = Host.Start(o => o.WithMinimumRequestBodyDataRate(Tight)
                .WithRouteRequestLimits("/free", p => p.MinimumRequestBodyDataRate = MinimumDataRate.None), Host.EchoLength, HttpVerbs.Any);
            Assert.That(await TrickleRequestAsync(strict, "/free", 20), Does.StartWith("HTTP/1.1 200").And.Contain("\r\n20\r\n"), "The route lifts the server's rate.");
            Assert.That(await TrickleRequestAsync(strict, "/other", 3), Does.StartWith("HTTP/1.1 408"));
        }

        // A body the handler never opens is not monitored: the response goes out at once and
        // the connection discards the body under the keep-alive deadline, as before.
        [Test]
        public async Task UnopenedBodyIsNotMonitoredAndKeepsTheKeepAliveDrain()
        {
            using var fast = new FastHeartbeat();
            await using var host = Host.Start(o => o.WithMinimumRequestBodyDataRate(Tight).WithConnectionLimits(null, keepAliveTimeout: TimeSpan.FromSeconds(1)), context =>
            {
                context.SetHandled();
                return Task.CompletedTask;
            }, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 100000\r\n\r\n");
            using var stop = new CancellationTokenSource();
            var trickle = TrickleAsync(socket, stop.Token, TimeSpan.FromMilliseconds(50));
            try
            {
                var clock = Stopwatch.StartNew();
                Assert.That(await socket.ReadHeadAsync(), Does.StartWith("HTTP/1.1 200"));
                Assert.That(MonitoredBodies, Is.Zero, "The discard after the response is not timed by the rate.");
                Assert.That(await socket.ReadToEndAsync(), Is.Empty);
                Assert.That(clock.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "The keep-alive deadline ends the discard.");
            }
            finally
            {
                stop.Cancel();
                await trickle;
            }
        }

        // ---------------------------------------------------------------- configuration

        [Test]
        public void DataRateDefaultsValidationAndLocking()
        {
            var options = new WebServerOptions();
            var rate = options.MinimumRequestBodyDataRate ?? throw new AssertionException("The rate is on by default.");
            Assert.That(rate.BytesPerSecond, Is.EqualTo(240d), "Kestrel's MinRequestBodyDataRate.");
            Assert.That(rate.GracePeriod, Is.EqualTo(TimeSpan.FromSeconds(5)));

            Assert.That(() => new MinimumDataRate(0, TimeSpan.FromSeconds(5)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new MinimumDataRate(-1, TimeSpan.FromSeconds(5)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new MinimumDataRate(double.NaN, TimeSpan.FromSeconds(5)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new MinimumDataRate(double.PositiveInfinity, TimeSpan.FromSeconds(5)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new MinimumDataRate(240, TimeSpan.FromSeconds(1)), Throws.InstanceOf<ArgumentOutOfRangeException>(), "The grace period must exceed the one-second check interval.");
            Assert.That(() => new MinimumDataRate(240, TimeSpan.Zero), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new MinimumDataRate(240, TimeSpan.FromDays(30)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(new MinimumDataRate(0.5, TimeSpan.FromMilliseconds(1001)).BytesPerSecond, Is.EqualTo(0.5));

            Assert.That(MinimumDataRate.None.BytesPerSecond, Is.Zero);
            Assert.That(MinimumDataRate.None.GracePeriod, Is.EqualTo(TimeSpan.Zero));
            Assert.That(MinimumDataRate.None.ToString(), Is.EqualTo("None"));
            Assert.That(rate.ToString(), Is.EqualTo("240 bytes per second after 00:00:05"));

            options.WithMinimumRequestBodyDataRate(MinimumDataRate.None);
            Assert.That(options.MinimumRequestBodyDataRate, Is.Null, "None on the server is the same as null.");
            options.WithMinimumRequestBodyDataRate(10, TimeSpan.FromSeconds(2));
            Assert.That(options.MinimumRequestBodyDataRate?.BytesPerSecond, Is.EqualTo(10d));
            var route = options.AddRouteRequestLimits("/a", _ => { });
            Assert.That(route.MinimumRequestBodyDataRate, Is.Null, "A route inherits the server rate until set.");
            route.MinimumRequestBodyDataRate = MinimumDataRate.None;
            Assert.That(route.MinimumRequestBodyDataRate, Is.SameAs(MinimumDataRate.None));

            options.WithUrlPrefix(Resources.GetServerAddress());
            using var server = new WebServer(options);
            Assert.That(() => options.MinimumRequestBodyDataRate = null, Throws.InvalidOperationException);
            var replacement = new MinimumDataRate(5, TimeSpan.FromSeconds(2));
            Assert.That(() => options.WithMinimumRequestBodyDataRate(replacement), Throws.InvalidOperationException);
            Assert.That(() => route.MinimumRequestBodyDataRate = replacement, Throws.InvalidOperationException);
            Assert.That(options.MinimumRequestBodyDataRate?.BytesPerSecond, Is.EqualTo(10d), "A locked rate is unchanged.");
        }

        // The monitor itself, without a server: only a pending read past the grace period with
        // too few bytes is starved, back pressure from the transport excuses it, failing it ends
        // the pending read with a sticky 408, and the reuse callback runs once.
        [Test]
        public async Task MonitorStarvesOnlyAPendingReadBelowTheRatePastTheGracePeriod()
        {
            var streamType = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RequestBodyRateStream")
                ?? throw new AssertionException("Missing request body rate stream.");
            var failures = 0;
            var backPressured = false;
            using var source = new BlockingSource();
            // The real heartbeat also checks this body; a one-hour grace period keeps it out of
            // the way, and the checks below pass a timestamp from beyond that hour instead.
            var hour = 3600 * Stopwatch.Frequency;
            var ticks = Stopwatch.Frequency;
            using var monitor = (Stream)(Activator.CreateInstance(streamType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object?[] { source, new MinimumDataRate(1000, TimeSpan.FromHours(1)), (Action)(() => failures++), (Func<bool>)(() => backPressured) }, null)
                ?? throw new AssertionException("Could not construct the monitor."));
            bool Starved(long now) => (bool)(streamType.GetMethod("IsStarved", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(monitor, new object[] { now })
                ?? throw new AssertionException("Missing starvation check."));

            Assert.That(Starved(Stopwatch.GetTimestamp() + hour + 10 * ticks), Is.False, "Nothing is pending.");
            var buffer = new byte[16];
            var pending = monitor.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            await source.Waiting.Task.WaitAsync(Deadline);
            Assert.That(Starved(Stopwatch.GetTimestamp() + hour / 2), Is.False, "Within the grace period.");
            Assert.That(Starved(Stopwatch.GetTimestamp() + hour + ticks), Is.True, "An hour of waiting for nothing is below 1000 bytes per second.");
            backPressured = true;
            Assert.That(Starved(Stopwatch.GetTimestamp() + hour + ticks), Is.False, "The transport, not the client, is holding the body back.");
            backPressured = false;

            source.Supply(8);
            Assert.That(await pending.WaitAsync(Deadline), Is.EqualTo(8));
            Assert.That(Starved(Stopwatch.GetTimestamp() + 10 * hour), Is.False, "No read is pending between reads.");
            pending = monitor.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
            await source.Waiting.Task.WaitAsync(Deadline);
            Assert.That(Starved(Stopwatch.GetTimestamp() + hour + ticks), Is.True, "Eight bytes over more than an hour is below the rate.");

            streamType.GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(monitor, null);
            var error = await Assert.ThrowsAsync<HttpException>(async () => await pending)
                ?? throw new AssertionException("The canceled read did not fail.");
            Assert.That(error.StatusCode, Is.EqualTo(408));
            Assert.That(error.Message, Does.Contain("minimum data rate"));
            Assert.That(source.CanceledReads, Is.EqualTo(1), "The pending transport read was canceled.");
            var again = await Assert.ThrowsAsync<HttpException>(async () =>
            {
                var late = await monitor.ReadAsync(buffer, 0, buffer.Length, CancellationToken.None);
                Assert.Fail("A later read returned " + late.ToString(CultureInfo.InvariantCulture) + " bytes instead of failing.");
            });
            Assert.That(again, Is.SameAs(error), "Every later read reports the same failure.");
            Assert.That(failures, Is.EqualTo(1), "The reuse callback ran once, on the reading side.");
            Assert.That(MonitoredBodies, Is.Zero);
        }

        // ---------------------------------------------------------------- helpers

        private static async Task EchoLengthSynchronously(IHttpContext context)
        {
            var total = 0L;
            var buffer = new byte[4096];
            int read;
            while ((read = context.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0) total += read;
            await context.SendStringAsync(total.ToString(CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding);
        }

        // Sends one body byte per pause until stopped or refused.
        private static async Task TrickleAsync(RawSocket socket, CancellationToken stop, TimeSpan pause)
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await socket.SendBytesAsync(new[] { (byte)'x' });
                    await Task.Delay(pause, stop);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        // Sends count body bytes one every pause, then stops; errors from a refused body are ignored.
        private static async Task TrickleBytesAsync(RawSocket socket, int count, TimeSpan pause)
        {
            try
            {
                for (var i = 0; i < count; ++i)
                {
                    await socket.SendBytesAsync(new[] { (byte)'x' });
                    await Task.Delay(pause);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        // Posts a 20-byte body, trickling sent bytes one every 50 ms and then stalling, and
        // returns the wire: 20 bytes complete the body, 3 leave the handler waiting past the
        // grace period with nothing unread when the server closes.
        private static async Task<string> TrickleRequestAsync(Host host, string path, int sent)
        {
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST {path} HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 20\r\nConnection: close\r\n\r\n");
            await TrickleBytesAsync(socket, sent, TimeSpan.FromMilliseconds(50));
            return await socket.ReadToEndAsync();
        }

        // Writes a fixed number of bytes per write with a pause between writes.
        private sealed class SlowContent : HttpContent
        {
            private readonly int _bytes;
            private readonly TimeSpan _pause;
            private readonly int _writes;
            private readonly bool _swallowErrors;
            public SlowContent(int bytes, TimeSpan pause, int writes, bool swallowErrors = false)
            { _bytes = bytes; _pause = pause; _writes = writes; _swallowErrors = swallowErrors; }
            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            {
                try
                {
                    for (var i = 0; i < _writes; ++i)
                    {
                        if (i > 0) await Task.Delay(_pause);
                        await stream.WriteAsync(new byte[_bytes], 0, _bytes);
                        await stream.FlushAsync();
                    }
                }
                catch (Exception error) when (_swallowErrors && error is IOException or ObjectDisposedException or OperationCanceledException or HttpRequestException)
                {
                }
            }
            protected override bool TryComputeLength(out long length) { length = (long)_bytes * _writes; return true; }
        }

        // A body source that completes a read only when supplied, honoring cancellation.
        private sealed class BlockingSource : Stream
        {
            private TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Waiting { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public int CanceledReads { get; private set; }
            public void Supply(int count) => _read.TrySetResult(count);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                var read = _read;
                Waiting.TrySetResult(true);
                using (cancellationToken.Register(() => { CanceledReads++; read.TrySetCanceled(cancellationToken); }))
                {
                    try { return await read.Task; }
                    finally
                    {
                        _read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => new(ReadAsync(new byte[buffer.Length], 0, buffer.Length, cancellationToken));
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
