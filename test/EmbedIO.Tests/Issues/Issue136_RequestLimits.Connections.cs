using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #136, third increment: TCP connection count, first-request header deadline and
    // keep-alive deadline on the managed listener, and a per-read request body deadline.
    public partial class Issue136_RequestLimits
    {
        private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

        // ---------------------------------------------------------------- connection count

        [Test]
        public async Task ConnectionsBeyondTheLimitAreClosedUntilOneEnds()
        {
            await using var host = Host.Start(o => o.WithConnectionLimits(2), context => context.SendStringAsync("served", "text/plain", WebServer.Utf8NoBomEncoding));
            using var first = await host.ConnectAsync();
            using var second = await host.ConnectAsync();
            await first.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\n\r\n");
            Assert.That(await first.ReadHeadAsync(), Does.StartWith("HTTP/1.1 200"), "The first connection is served and stays open.");

            using (var third = await host.ConnectAsync())
            {
                // The server closes the third connection without reading or answering it.
                Assert.That(await third.ReadToEndAsync(), Is.Empty);
            }

            second.Dispose();
            using var client = Host.CreateClient(1);
            var stop = DateTime.UtcNow + Deadline;
            string? body = null;
            while (body == null && DateTime.UtcNow < stop)
            {
                try { body = await client.GetStringAsync(host.Url); }
                catch (HttpRequestException) { await Task.Delay(20); }
            }
            Assert.That(body, Is.EqualTo("served"), "A slot frees when an open connection closes.");
        }

        [Test]
        public async Task ListenerWithConnectionLimitsDoesNotShareItsPort()
        {
            await using var limited = Host.Start(o => o.WithConnectionLimits(10), context => context.SendStringAsync("limited", "text/plain", WebServer.Utf8NoBomEncoding));
            using var other = new WebServer(o => o.WithUrlPrefix(limited.Url + "other/").WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("other", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(Deadline);
            Assert.That(async () => await other.RunAsync(stop.Token), Throws.InstanceOf<HttpListenerException>());
            Assert.That(await limited.ProbeAsync(), Is.EqualTo("healthy"), "The first server keeps its endpoint.");
        }

        // ---------------------------------------------------------------- connection deadlines

        [Test]
        public async Task SlowFirstRequestHeadIsClosedAtTheHeaderDeadline()
        {
            await using var host = Host.Start(o => o.WithConnectionLimits(null, requestHeadersTimeout: Short), Host.EchoLength);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\n");
            var watch = Stopwatch.StartNew();
            Assert.That(await socket.ReadToEndAsync(), Is.Empty, "The incomplete head gets no response.");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task IdleHttp1ConnectionClosesAtTheKeepAliveDeadline()
        {
            await using var host = Host.Start(o => o.WithConnectionLimits(null, keepAliveTimeout: TimeSpan.FromMilliseconds(1500)),
                context => context.SendStringAsync("served", "text/plain", WebServer.Utf8NoBomEncoding));
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\n\r\n");
            var watch = Stopwatch.StartNew();
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
            Assert.That(wire, Does.Contain("Keep-Alive: timeout=2"), "The header advertises whole seconds, rounded up.");
            Assert.That(watch.Elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(1000)).And.LessThan(TimeSpan.FromSeconds(6)));
        }

        [Test]
        public async Task DefaultKeepAliveIsUnchanged()
        {
            await using var host = Host.Start(_ => { }, context => context.SendStringAsync("served", "text/plain", WebServer.Utf8NoBomEncoding));
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"GET / HTTP/1.1\r\nHost: {host.Authority}\r\n\r\n");
            Assert.That(await socket.ReadHeadAsync(), Does.Contain("Keep-Alive: timeout=15"));
        }

        [Test]
        public async Task IdleHttp2ConnectionIsDrainedWithGoaway()
        {
            await using var host = Host.Start(o => o.WithConnectionLimits(null, keepAliveTimeout: Short), Host.EchoLength);
            using var socket = await host.ConnectAsync();
            // Prior-knowledge preface and an empty SETTINGS frame; no stream is ever opened.
            await socket.SendAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
            await socket.SendBytesAsync(new byte[] { 0, 0, 0, 4, 0, 0, 0, 0, 0 });
            var watch = Stopwatch.StartNew();
            var frames = await socket.ReadFrameTypesToEndAsync();
            Assert.That(frames, Does.Contain((byte)7), "The server sends GOAWAY before closing.");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(6)));
        }

        [Test]
        public async Task Http2ConnectionWithAnOpenStreamIsNotDrained()
        {
            using var gate = new Gate(1);
            await using var host = Host.Start(o => o.WithConnectionLimits(null, keepAliveTimeout: Short), gate.HandleAsync);
            using var client = Host.CreateClient(2);
            var held = client.GetStringAsync(host.Url);
            await gate.Entered.WaitAsync(Deadline);
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            gate.Release();
            Assert.That(await held, Is.EqualTo("released"), "A stream open longer than the idle deadline completes.");
        }

        [Test]
        public void ConnectionLimitDefaultsValidationLockingAndMode()
        {
            var options = new WebServerOptions();
            Assert.That(options.MaximumConnections, Is.Null);
            Assert.That(options.RequestHeadersTimeout, Is.Null);
            Assert.That(options.KeepAliveTimeout, Is.Null);
            Assert.That(options.RequestBodyReadTimeout, Is.Null);
            Assert.That(() => options.MaximumConnections = 0, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.RequestHeadersTimeout = TimeSpan.Zero, Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.KeepAliveTimeout = TimeSpan.FromDays(30), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.RequestBodyReadTimeout = TimeSpan.FromMilliseconds(-1), Throws.InstanceOf<ArgumentOutOfRangeException>());

            options.WithUrlPrefix(Resources.GetServerAddress()).WithConnectionLimits(5, Short, Short).WithRequestBodyReadTimeout(Short);
            using (var server = new WebServer(options))
            {
                Assert.That(() => options.MaximumConnections = 6, Throws.InvalidOperationException);
                Assert.That(() => options.KeepAliveTimeout = null, Throws.InvalidOperationException);
                Assert.That(() => options.RequestBodyReadTimeout = null, Throws.InvalidOperationException);
            }

            var quic = new WebServerOptions().WithUrlPrefix("https://localhost:9443/").WithMode(HttpListenerMode.EmbedIOHttp3).WithConnectionLimits(5);
            Assert.That(() => new WebServer(quic), Throws.InstanceOf<NotSupportedException>(), "TCP connection limits are rejected for an HTTP/3-only server.");
        }

        // ---------------------------------------------------------------- body read deadline

        [Test]
        public async Task StalledHttp1BodyReadAnswers408AndCloses()
        {
            await using var host = Host.Start(o => o.WithRequestBodyReadTimeout(Short), Host.EchoLength, HttpVerbs.Any);
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST / HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 100\r\n\r\n0123456789");
            var wire = await socket.ReadToEndAsync();
            Assert.That(wire, Does.StartWith("HTTP/1.1 408"));
            Assert.That(wire, Does.Contain("Connection: close"));
            Assert.That(await host.ProbeAsync(), Is.EqualTo("healthy"));
        }

        [TestCase(2)]
        [TestCase(3)]
        public async Task StalledMultiplexedBodyReadAnswers408(int protocol)
        {
            await using var host = await MultiHost.StartAsync(protocol, o => o.WithRequestBodyReadTimeout(Short), Host.EchoLength, HttpVerbs.Any);
            using var stall = new StallingContent(10);
            using var response = await host.SendAsync(HttpMethod.Post, stall);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestTimeout));
            Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain("not received within"));
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task SlowSteadyUploadIsNotCutOff(int protocol)
        {
            await using var host = Host.Start(o => o.WithRequestBodyReadTimeout(Short), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            // Ten chunks 200 ms apart: two seconds in total, each read well inside the deadline.
            using var response = await client.SendAsync(Post(client, host.Url, new TrickleContent(10, TimeSpan.FromMilliseconds(200))));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("100"));
        }

        // ---------------------------------------------------------------- helpers

        // Sends a prefix of its body and then waits until disposed.
        private sealed class StallingContent : HttpContent
        {
            private readonly int _prefix;
            private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public StallingContent(int prefix) { _prefix = prefix; }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                await stream.WriteAsync(new byte[_prefix], 0, _prefix);
                await stream.FlushAsync();
                await _release.Task;
            }
            protected override bool TryComputeLength(out long length) { length = -1; return false; }
            protected override void Dispose(bool disposing)
            {
                _release.TrySetResult(true);
                base.Dispose(disposing);
            }
        }

        // Writes ten-byte chunks with a pause between them.
        private sealed class TrickleContent : HttpContent
        {
            private readonly int _chunks;
            private readonly TimeSpan _pause;
            public TrickleContent(int chunks, TimeSpan pause) { _chunks = chunks; _pause = pause; }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                for (var i = 0; i < _chunks; ++i)
                {
                    if (i > 0) await Task.Delay(_pause);
                    await stream.WriteAsync(new byte[10], 0, 10);
                    await stream.FlushAsync();
                }
            }
            protected override bool TryComputeLength(out long length) { length = _chunks * 10L; return true; }
        }
    }
}
