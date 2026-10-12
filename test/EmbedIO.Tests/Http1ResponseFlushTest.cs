using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using EmbedIO.Tests.Issues;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1ResponseFlushTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ABodyWriteReachesThePeerBeforeFlushOrCompletion(bool chunked)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri;
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
            var transport = peer.GetStream();
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), timeout.Token);
            var context = await listener.GetContextAsync(timeout.Token);
            try
            {
                context.Response.SendChunked = chunked;
                if (!chunked) context.Response.ContentLength64 = 6;
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("ABC"), timeout.Token);
                var wire = await ReadThroughAsync(transport, chunked ? "3\r\nABC\r\n" : "\r\n\r\nABC", timeout.Token);
                Assert.That(wire, Does.StartWith("HTTP/1.1 200 OK\r\n"));
                Assert.That(wire, Does.EndWith(chunked ? "3\r\nABC\r\n" : "\r\n\r\nABC"));
                // No flush and no completion has occurred: the peer already has the body.
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("DEF"), timeout.Token);
            }
            finally { context.Close(); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FlushBeforeTheFirstBodyWriteCommitsHeadersWithoutCompletingTheResponse(bool asynchronous)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri;
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
            var transport = peer.GetStream();
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), timeout.Token);
            var context = await listener.GetContextAsync(timeout.Token);
            try
            {
                context.Response.ContentLength64 = 3;
                if (asynchronous) await context.Response.OutputStream.FlushAsync(timeout.Token);
                else context.Response.OutputStream.Flush();
                var head = await ReadThroughAsync(transport, "\r\n\r\n", timeout.Token);
                Assert.That(head, Does.Contain("Content-Length: 3\r\n"));
                Assert.That(head, Does.EndWith("\r\n\r\n"));
                Assert.Throws<InvalidOperationException>(() => context.Response.ContentLength64 = 4);
                Assert.Throws<InvalidOperationException>(() => context.Response.StatusCode = 201);
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("ABC"), timeout.Token);
                Assert.That(await ReadThroughAsync(transport, "ABC", timeout.Token), Is.EqualTo("ABC"));
            }
            finally { context.Close(); }
        }

        [Test]
        public async Task AQueuedCanceledFlushDoesNotPassTheAdmittedWrite()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            fixture.Transport.BlockWrites = true;
            var writing = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var flushing = fixture.Stream.FlushAsync(cancellation.Token);
            Assert.That(flushing.IsCompleted, Is.False);
            cancellation.Cancel();
            try
            {
                await Assert.CatchAsync<OperationCanceledException>(async () => await flushing);
                Assert.That(fixture.Transport.Length, Is.Zero);
            }
            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await writing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(Encoding.ASCII.GetString(fixture.Transport.ToArray()), Does.EndWith("3\r\nABC\r\n"));
        }

        [Test]
        public async Task PreCanceledFlushLeavesHeadersMutable()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await fixture.Stream.FlushAsync(cancellation.Token));
            Assert.That(fixture.Transport.Length, Is.Zero);
            fixture.Response.ContentLength64 = 0;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task AFlushTransportFailurePoisonsLaterOutput(bool asynchronous, bool ignoreErrors)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            using var transport = new FailedFlushTransport();
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ResponseStream") ?? throw new AssertionException("Missing response output.");
            using var output = (Stream)(Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { transport, fixture.Response, ignoreErrors }, null) ?? throw new AssertionException("Missing output instance."));
            if (asynchronous)
            {
                if (ignoreErrors) await output.FlushAsync();
                else await Assert.ThrowsAsync<IOException>(async () => await output.FlushAsync());
            }
            else if (ignoreErrors) output.Flush();
            else Assert.Throws<IOException>(() => output.Flush());
            var before = transport.ToArray();
            if (ignoreErrors) await output.WriteAsync(Encoding.ASCII.GetBytes("BAD"));
            else await Assert.ThrowsAsync<IOException>(async () => await output.WriteAsync(Encoding.ASCII.GetBytes("BAD")));
            output.Dispose();
            Assert.That(transport.ToArray(), Is.EqualTo(before), "No body or terminating chunk may follow a failed flush.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletedOutputIsNotWritableAndRejectsFlush(bool asynchronous)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false);
            fixture.Response.ContentLength64 = 0;
            fixture.Stream.Dispose();
            Assert.That(fixture.Stream.CanWrite, Is.False);
            if (asynchronous) await Assert.ThrowsAsync<ObjectDisposedException>(async () => await fixture.Stream.FlushAsync());
            else Assert.Throws<ObjectDisposedException>(() => fixture.Stream.Flush());
        }
        [Test]
        public async Task ApplicationOwnedOutputCompletionLeavesTheServerAndNextResponseHealthy()
        {
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri.ToString();
            using var server = new WebServer(options => options.WithUrlPrefix(url))
                .OnGet("/completed", context =>
                {
                    context.Response.ContentLength64 = 0;
                    context.Response.OutputStream.Dispose();
                    return Task.CompletedTask;
                })
                .OnGet("/next", context => context.SendStringAsync("healthy", MimeType.PlainText, Encoding.UTF8));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(timeout.Token);
            try
            {
                using var client = new HttpClient();
                using var first = await client.GetAsync(url + "completed", timeout.Token);
                Assert.That(first.Content.Headers.ContentLength, Is.Zero);
                Assert.That(await first.Content.ReadAsByteArrayAsync(timeout.Token), Is.Empty);
                Assert.That(await client.GetStringAsync(url + "next", timeout.Token), Is.EqualTo("healthy"));
            }
            finally { timeout.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
        [Test]
        public async Task AnUncanceledFlushWaitsBehindTheAdmittedBodyWrite()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            fixture.Transport.BlockWrites = true;
            var writing = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ABC"), 0, 3);
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var flushing = fixture.Stream.FlushAsync();
            try { Assert.That(flushing.IsCompleted, Is.False); }
            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await writing.WaitAsync(TimeSpan.FromSeconds(5));
                await flushing.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.That(Encoding.ASCII.GetString(fixture.Transport.ToArray()), Does.EndWith("3\r\nABC\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HeadFlushCommitsHeadersAndPreservesTheNextResponse(bool asynchronous)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri;
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
            var transport = peer.GetStream();
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), timeout.Token);
            var context = await listener.GetContextAsync(timeout.Token);
            try
            {
                context.Response.ContentLength64 = 3;
                context.Response.SetCookie(new Cookie("flush", "head"));
                if (asynchronous) await context.Response.OutputStream.FlushAsync(timeout.Token);
                else context.Response.OutputStream.Flush();
                // The peer receives headers while the application still owns this
                // context; response completion cannot manufacture the observation.
                var head = await ReadThroughAsync(transport, "\r\n\r\n", timeout.Token);
                Assert.That(head, Does.Contain("Content-Length: 3\r\n"));
                Assert.That(head, Does.Contain("Set-Cookie: flush=head"));
                Assert.Throws<InvalidOperationException>(() => context.Response.ContentLength64 = 5);
                Assert.Throws<InvalidOperationException>(() => context.Response.StatusCode = 201);
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("HEAD_BODY_MUST_NOT_BE_SENT"), timeout.Token);
            }
            finally { context.Close(); }
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"GET /next HTTP/1.1\r\nHost: {url.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
            var next = await listener.GetContextAsync(timeout.Token);
            try
            {
                next.Response.ContentLength64 = 7;
                await next.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("healthy"), timeout.Token);
            }
            finally { next.Close(); }
            var nextHead = await ReadThroughAsync(transport, "\r\n\r\n", timeout.Token);
            Assert.That(nextHead, Does.StartWith("HTTP/1.1 200"), "HEAD payload or chunk bytes cannot precede the next response.");
            Assert.That(await ReadThroughAsync(transport, "healthy", timeout.Token), Is.EqualTo("healthy"));
            Assert.That(await transport.ReadAsync(new byte[1], timeout.Token), Is.Zero);
        }

        [TestCase(204)]
        [TestCase(205)]
        [TestCase(304)]
        public async Task BodylessFlushPublishesTheHeadWithoutChunkFraming(int status)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false);
            fixture.Response.StatusCode = status;
            await fixture.Stream.FlushAsync();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.StartWith("HTTP/1.1 " + status));
            Assert.That(wire, Does.EndWith("\r\n\r\n"));
            Assert.That(wire, Does.Not.Contain("Transfer-Encoding"));
            fixture.Stream.Dispose();
            Assert.That(Encoding.ASCII.GetString(fixture.Transport.ToArray()), Is.EqualTo(wire));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EmptyResponseFlushRetainsItsDeclaredCompletionFraming(bool chunked)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            if (!chunked) fixture.Response.ContentLength64 = 0;
            fixture.Response.SetCookie(new Cookie("flush", "committed"));
            await fixture.Stream.FlushAsync();
            var head = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(head, Does.EndWith("\r\n\r\n"));
            Assert.That(head, Does.Contain("Set-Cookie: flush=committed"));
            Assert.That(head, Does.Contain(chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: 0\r\n"));
            fixture.Stream.Dispose();
            Assert.That(Encoding.ASCII.GetString(fixture.Transport.ToArray()), Is.EqualTo(head + (chunked ? "0\r\n\r\n" : "")));
        }

        private sealed class FailedFlushTransport : MemoryStream
        {
            public override void Flush() => throw new IOException("Controlled flush failure.");
            public override Task FlushAsync(CancellationToken token) => Task.FromException(new IOException("Controlled flush failure."));
        }
        private static async Task<string> ReadThroughAsync(Stream stream, string ending, CancellationToken token)
        {
            var result = new StringBuilder();
            var one = new byte[1];
            while (!result.ToString().EndsWith(ending, StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(one, 0, 1, token) == 0) throw new EndOfStreamException();
                result.Append((char)one[0]);
                if (result.Length > 32768) throw new InvalidDataException("Response boundary not found within the wire-test limit.");
            }
            return result.ToString();
        }
    }
}
