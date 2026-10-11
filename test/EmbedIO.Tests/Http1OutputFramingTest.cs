using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.Issues;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1OutputFramingTest
    {
        [TestCase("-1")]
        [TestCase("invalid")]
        [TestCase("")]
        public async Task EmptyCompletionPreservesValidAutomaticFramingForAnInvalidLengthField(string value)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false);
            fixture.Response.Headers[HttpHeaderNames.ContentLength] = value;
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.Contain("Content-Length: 0\r\n"));
            Assert.That(wire, Does.EndWith("\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentCompletionWaitsForTheAdmittedWriteAndCommitsOnce(bool chunked)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            fixture.Transport.BlockWrites = true;
            var writing = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ABCDEF"), 0, 6);
            await fixture.Transport.AsyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = Task.Run(() => { firstStarted.SetResult(); fixture.Stream.Dispose(); });
            var second = Task.Run(() => { secondStarted.SetResult(); fixture.Stream.Dispose(); });
            try
            {
                await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(second.IsCompleted, Is.False);
                Assert.That(fixture.Transport.Length, Is.Zero);
            }
            finally
            {
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
                await Task.WhenAll(writing, first, second).WaitAsync(TimeSpan.FromSeconds(5));
            }
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(boundary, Is.GreaterThan(0));
            Assert.That(wire[(boundary + 4)..], Is.EqualTo(chunked ? "6\r\nABCDEF\r\n0\r\n\r\n" : "ABCDEF"));
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
            Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(chunked ? 1 : 0));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task PartialSubmissionFailureNeverSendsRemainderOrTerminator(bool asynchronous, bool ignoreErrors)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors);
            fixture.Transport.PartialBytesBeforeError = 7;
            fixture.Transport.Error = new IOException("controlled partial submission");
            var payload = new byte[65537];
            if (asynchronous)
            {
                if (ignoreErrors) await fixture.Stream.WriteAsync(payload, 0, payload.Length);
                else await Assert.ThrowsAsync<IOException>(async () => await fixture.Stream.WriteAsync(payload, 0, payload.Length));
            }
            else if (ignoreErrors) fixture.Stream.Write(payload, 0, payload.Length);
            else Assert.Throws<IOException>(() => fixture.Stream.Write(payload, 0, payload.Length));
            fixture.Transport.Error = null;
            var submissions = fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites;
            Assert.That(submissions, Is.EqualTo(1));
            fixture.Stream.Dispose();
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.Length, Is.EqualTo(7));
            Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.EqualTo(submissions));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task IncompleteDeclaredOutputEndsTheTransportAndLeavesTheListenerHealthy(bool noOutput)
        {
            var url = new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = "http" }.Uri;
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, timeout.Token);
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), timeout.Token);
            var first = await listener.GetContextAsync(timeout.Token);
            first.Response.ContentLength64 = 10;
            if (!noOutput) await first.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("abc"), timeout.Token);
            first.Close();
            first.Close();
            using var bytes = new MemoryStream();
            await peer.GetStream().CopyToAsync(bytes, timeout.Token);
            var wire = Encoding.ASCII.GetString(bytes.ToArray());
            Assert.That(wire, Does.StartWith("HTTP/1.1 200 OK\r\n"));
            Assert.That(wire, Does.Contain("Content-Length: 10\r\n"));
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(boundary, Is.GreaterThan(0));
            Assert.That(wire[(boundary + 4)..], Is.EqualTo(noOutput ? "" : "abc"));

            using var healthy = new HttpClient();
            var accepting = listener.GetContextAsync(timeout.Token);
            var received = healthy.GetAsync(url, timeout.Token);
            var next = await accepting;
            next.Response.StatusCode = 204;
            next.Close();
            using var result = await received;
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ExcessBodyWriteIsRejectedBeforeTransportSubmission(bool asynchronous, bool afterPrefix)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false);
            fixture.Response.ContentLength64 = 3;
            var bytes = Encoding.ASCII.GetBytes("ABCDEF");
            if (afterPrefix) await fixture.Stream.WriteAsync(bytes, 0, 2);
            var submitted = fixture.Transport.Length;
            if (asynchronous)
                await Assert.ThrowsAsync<ProtocolViolationException>(async () => await fixture.Stream.WriteAsync(bytes, 0, 4));
            else
                Assert.Throws<ProtocolViolationException>(() => fixture.Stream.Write(bytes, 0, 4));
            Assert.That(fixture.Transport.Length, Is.EqualTo(submitted), "The rejected write must not commit even its prefix.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ATransportFailureNeverAllowsLaterPayloadOrClosingBytes(bool ignoreErrors)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true, ignoreErrors);
            fixture.Transport.Error = new IOException("controlled failed submission");
            var writing = fixture.Stream.WriteAsync(new byte[3], 0, 3);
            if (ignoreErrors) await writing;
            else await Assert.ThrowsAsync<IOException>(async () => await writing);
            fixture.Transport.Error = null;
            var submissions = fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites;
            if (ignoreErrors) await fixture.Stream.WriteAsync(new byte[3], 0, 3);
            else await Assert.ThrowsAsync<IOException>(async () => await fixture.Stream.WriteAsync(new byte[3], 0, 3));
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.AsyncWrites + fixture.Transport.SynchronousWrites, Is.EqualTo(submissions));
            Assert.That(fixture.Transport.Length, Is.Zero);
        }
    }
}
