using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.Issues;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Transport submissions and exact wire bytes of the managed HTTP/1 response
    // stream: the first write carries the head with the body in one segment, bounded
    // chunks stay single writes on both the asynchronous and synchronous paths, and
    // oversized heads or bodies fall back to direct writes without changing framing.
    public class ResponseWriteBatchingTest
    {
        private static byte[] Pattern(int count)
        {
            var payload = new byte[count];
            for (var index = 0; index < count; index++) payload[index] = (byte)('A' + index % 26);
            return payload;
        }

        private static string Body(Issue574_ResponseWrites.RecordingTransport transport)
        {
            var wire = Encoding.ASCII.GetString(transport.ToArray());
            var boundary = wire.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            Assert.That(boundary, Is.GreaterThan(0));
            Assert.That(wire, Does.StartWith("HTTP/1.1 200 OK\r\n"));
            return wire[(boundary + 4)..];
        }

        private static string Chunk(byte[] payload, int offset, int count)
            => count.ToString("x", CultureInfo.InvariantCulture) + "\r\n" + Encoding.ASCII.GetString(payload, offset, count) + "\r\n";

        [TestCase(false, 1)]
        [TestCase(false, 16384)]
        [TestCase(false, 65536)]
        [TestCase(true, 1)]
        [TestCase(true, 16384)]
        [TestCase(true, 65536)]
        public async Task FirstWriteCommitsHeadAndWholeBoundedBodyInOneTransportWrite(bool chunked, int count)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            if (!chunked) fixture.Response.ContentLength64 = count;
            var payload = Pattern(count + 2);
            await fixture.Stream.WriteAsync(payload, 1, count);
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
            Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo(chunked
                ? Chunk(payload, 1, count) + "0\r\n\r\n"
                : Encoding.ASCII.GetString(payload, 1, count)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FirstWriteBeyondTheBoundSendsPrefixThenRemainderDirectly(bool chunked)
        {
            const int Count = 65536 + 4097;
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            if (!chunked) fixture.Response.ContentLength64 = Count;
            var payload = Pattern(Count + 3);
            await fixture.Stream.WriteAsync(payload, 3, Count);
            // Head plus a 16 KiB body prefix, the remaining bytes directly, and for chunked the CRLF.
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(chunked ? 3 : 2));
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo(chunked
                ? Chunk(payload, 3, Count) + "0\r\n\r\n"
                : Encoding.ASCII.GetString(payload, 3, Count)));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OversizedHeadIsSentAloneBeforeTheBody(bool chunked)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked);
            if (!chunked) fixture.Response.ContentLength64 = 6;
            fixture.Response.Headers.Set("X-Large", new string('x', 70000));
            var payload = Encoding.ASCII.GetBytes("ABCDEF");
            await fixture.Stream.WriteAsync(payload, 0, payload.Length);
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(2));
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.Contain("X-Large: " + new string('x', 70000) + "\r\n"));
            Assert.That(wire, Does.EndWith(chunked ? "\r\n\r\n6\r\nABCDEF\r\n0\r\n\r\n" : "\r\n\r\nABCDEF"));
        }

        [Test]
        public async Task CompleteSmallChunkedResponseUsesOneAsyncWriteAndTheSynchronousTerminator()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            var payload = Encoding.ASCII.GetBytes("hello");
            await fixture.Stream.WriteAsync(payload, 0, payload.Length);
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
            Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(1), "Only disposal writes synchronously.");
            Assert.That(Body(fixture.Transport), Is.EqualTo("5\r\nhello\r\n0\r\n\r\n"));
        }

        [TestCase(1)]
        [TestCase(15)]
        [TestCase(16)]
        [TestCase(255)]
        [TestCase(256)]
        [TestCase(4095)]
        [TestCase(4096)]
        [TestCase(65535)]
        [TestCase(65536)]
        public async Task SynchronousBoundedChunksCommitOneTransportWriteEach(int count)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            await fixture.Stream.WriteAsync(Array.Empty<byte>(), 0, 0);
            var payload = Pattern(count + 1);
            fixture.Stream.Write(payload, 1, count);
            fixture.Stream.Write(payload, 0, count);
            Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(2));
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(3));
            Assert.That(Body(fixture.Transport), Is.EqualTo(Chunk(payload, 1, count) + Chunk(payload, 0, count) + "0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ChunksBeyondTheBoundUseThreeWritesOnBothPaths(bool asynchronous)
        {
            const int Count = 65537;
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            await fixture.Stream.WriteAsync(Array.Empty<byte>(), 0, 0);
            var payload = Pattern(Count);
            if (asynchronous)
            {
                await fixture.Stream.WriteAsync(payload, 0, Count);
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(4));
            }
            else
            {
                fixture.Stream.Write(payload, 0, Count);
                Assert.That(fixture.Transport.SynchronousWrites, Is.EqualTo(3));
            }
            fixture.Stream.Dispose();
            Assert.That(Body(fixture.Transport), Is.EqualTo(Chunk(payload, 0, Count) + "0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CanceledMergedFirstWriteSendsNothingAndReleasesTheWriter(bool chunked)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(chunked, ignoreErrors: true);
            if (!chunked) fixture.Response.ContentLength64 = 16384 + 2;
            fixture.Transport.BlockWrites = true;
            using var cancel = new CancellationTokenSource();
            var pending = fixture.Stream.WriteAsync(Pattern(16384), 0, 16384, cancel.Token);
            try
            {
                Assert.That(fixture.Transport.AsyncEntered.Task.IsCompleted, Is.True, "The merged first segment is submitted immediately.");
                Assert.That(pending.IsCompleted, Is.False);
                cancel.Cancel();
                var error = await Assert.CatchAsync<OperationCanceledException>(async () => await pending);
                Assert.That(error.CancellationToken, Is.EqualTo(cancel.Token));
                Assert.That(fixture.Transport.Length, Is.Zero, "A canceled segment never reaches the transport.");
            }
            finally
            {
                cancel.Cancel();
                fixture.Transport.BlockWrites = false;
                fixture.Transport.ReleaseWrites.TrySetResult();
            }
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("OK"));
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Is.EqualTo(chunked ? "2\r\nOK\r\n0\r\n\r\n" : "OK"), "Headers were committed by the canceled write; later bytes follow its framing.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task MergedFirstWriteTransportFailureRespectsIgnoreWriteExceptions(bool ignoreErrors)
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(false, ignoreErrors);
            fixture.Response.ContentLength64 = 16384;
            fixture.Transport.Error = new IOException("controlled transport failure");
            var writing = fixture.Stream.WriteAsync(Pattern(16384), 0, 16384);
            if (ignoreErrors) await writing;
            else await Assert.ThrowsAsync<IOException>(async () => await writing);
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
            Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
            fixture.Transport.Error = null;
            fixture.Stream.Dispose();
            Assert.That(fixture.Transport.Length, Is.Zero, "The failed segment is not retried.");
        }

        [Test]
        public async Task SuppressedBodyWritesCommitOnlyTheHead()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            fixture.Response.StatusCode = 204;
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("ignored"));
            Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1));
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.StartWith("HTTP/1.1 204 No Content\r\n"));
            Assert.That(wire, Does.Not.Contain("Transfer-Encoding").IgnoreCase);
            Assert.That(wire, Does.EndWith("\r\n\r\n"));
            Assert.That(wire, Does.Not.Contain("ignored"));
        }

        [Test]
        public async Task ReservedTrailersFollowTheMergedFirstChunk()
        {
            using var fixture = await Issue574_ResponseWrites.Fixture.Create(true);
            var sections = (IHttpResponseSections)fixture.Response;
            sections.DeclareTrailers("X-Checksum");
            var payload = Encoding.ASCII.GetBytes("payload");
            await fixture.Stream.WriteAsync(payload, 0, payload.Length);
            sections.SetTrailers(new WebHeaderCollection { ["X-Checksum"] = "abc123" });
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            // Declared trailer names are normalized to lowercase on the wire.
            Assert.That(wire, Does.Contain("Trailer: x-checksum\r\n").IgnoreCase);
            Assert.That(Body(fixture.Transport), Is.EqualTo("7\r\npayload\r\n0\r\nx-checksum: abc123\r\n\r\n").IgnoreCase);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task EndToEndTwentyKilobyteResponsesAreExactOverHttpAndHttps(bool https, bool chunked)
        {
            var payload = Pattern(20000);
            var url = https ? HttpsSmoke.GetUrl() : Resources.GetServerAddress();
            using var certificate = https ? HttpsSmoke.CreateCertificate() : null;
            using var stop = new CancellationTokenSource();
            var options = new WebServerOptions().WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
            if (certificate != null) options.WithCertificate(certificate);
            using var server = new WebServer(options);
            server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                context.Response.ContentType = "application/octet-stream";
                if (chunked) context.Response.SendChunked = true;
                else context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload, 0, payload.Length, context.CancellationToken);
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                var wire = await ReadWire(url, certificate);
                Assert.That(Encoding.ASCII.GetString(wire, 0, 12), Is.EqualTo("HTTP/1.1 200"));
                var boundary = wire.AsSpan().IndexOf("\r\n\r\n"u8);
                Assert.That(boundary, Is.GreaterThan(0));
                var head = Encoding.ASCII.GetString(wire, 0, boundary);
                var body = wire.AsSpan(boundary + 4).ToArray();
                if (chunked)
                {
                    Assert.That(head, Does.Contain("Transfer-Encoding: chunked").IgnoreCase);
                    Assert.That(body, Is.EqualTo(Encoding.ASCII.GetBytes("4e20\r\n").Concat(payload).Concat(Encoding.ASCII.GetBytes("\r\n0\r\n\r\n"))));
                }
                else
                {
                    Assert.That(head, Does.Contain("Content-Length: 20000"));
                    Assert.That(body, Is.EqualTo(payload));
                }
                using var client = certificate == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : HttpsSmoke.CreateClient(certificate);
                Assert.That(await client.GetByteArrayAsync(url), Is.EqualTo(payload));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task<byte[]> ReadWire(string url, X509Certificate2? certificate)
        {
            var uri = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = new TcpClient();
            await socket.ConnectAsync(uri.Host, uri.Port, stop.Token);
            Stream stream = socket.GetStream();
            using var tls = certificate == null ? null : new SslStream(stream, false, (_, peer, _, _) => peer?.GetCertHashString() == certificate.Thumbprint);
            if (tls != null)
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, stop.Token);
                stream = tls;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n"), stop.Token);
            using var wire = new MemoryStream();
            await stream.CopyToAsync(wire, stop.Token);
            return wire.ToArray();
        }
    }
}
