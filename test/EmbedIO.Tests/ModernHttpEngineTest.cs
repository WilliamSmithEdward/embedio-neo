using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ModernHttpEngineTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task FixedLengthAsyncReadNeverInvokesSynchronousTransport(bool memory)
        {
            using var source = new AsyncOnlyStream(Encoding.ASCII.GetBytes("bodyNEXT"));
            using var body = Body(source, Array.Empty<byte>(), 4);
            var bytes = new byte[10];
            var count = memory ? await body.ReadAsync(bytes.AsMemory()) : await body.ReadAsync(bytes, 0, bytes.Length);
            Assert.That(count, Is.EqualTo(4));
            Assert.That(Encoding.ASCII.GetString(bytes, 0, count), Is.EqualTo("body"));
            Assert.That(await body.ReadAsync(bytes), Is.Zero);
            Assert.That(source.Position, Is.EqualTo(4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationDoesNotConsumeBufferedBody(bool chunked)
        {
            using var source = new MemoryStream();
            using var body = chunked ? Chunked(source, Encoding.ASCII.GetBytes("4\r\nbody\r\n0\r\n\r\n"))
                : Body(source, Encoding.ASCII.GetBytes("body"), 4);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.ThatAsync(async () => await body.ReadAsync(new byte[4], 0, 4, stop.Token), Throws.InstanceOf<OperationCanceledException>());
            using var received = new MemoryStream();
            await body.CopyToAsync(received);
            Assert.That(Encoding.ASCII.GetString(received.ToArray()), Is.EqualTo("body"));
        }

        [TestCase(1, false)]
        [TestCase(7, false)]
        [TestCase(8192, false)]
        [TestCase(1, true)]
        [TestCase(7, true)]
        [TestCase(8192, true)]
        public async Task ChunkedBodyDecodesExtensionsTrailersAndPreservesSuccessor(int fragment, bool buffered)
        {
            var wire = Encoding.ASCII.GetBytes("3;name=token;quoted=\"a\\\"b\"\r\nabc\r\n2\r\nde\r\n0\r\nX-Checksum: ok\r\n\r\nNEXT");
            using var source = new AsyncOnlyStream(buffered ? Array.Empty<byte>() : wire, fragment);
            using var body = Chunked(source, buffered ? wire : Array.Empty<byte>());
            var empty = new byte[0];
            Assert.That(await body.ReadAsync(empty), Is.Zero);
            using var result = new MemoryStream();
            await body.CopyToAsync(result, 2);
            Assert.That(Encoding.ASCII.GetString(result.ToArray()), Is.EqualTo("abcde"));
            Assert.That(await body.ReadAsync(new byte[5]), Is.Zero);
            var tail = (ArraySegment<byte>)((body.GetType().GetProperty("BufferedRemainder", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(body) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            using var next = new MemoryStream();
            if (tail.Count > 0) next.Write((tail.Array ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), tail.Offset, tail.Count);
            await source.CopyToAsync(next);
            Assert.That(Encoding.ASCII.GetString(next.ToArray()), Is.EqualTo("NEXT"));
        }

        [TestCase("Z\r\n")]
        [TestCase("-1\r\n")]
        [TestCase("8000000000000000\r\n")]
        [TestCase("1\na\r\n0\r\n\r\n")]
        [TestCase("1\r\naX\n0\r\n\r\n")]
        [TestCase("3\r\nab")]
        [TestCase("1;=bad\r\na\r\n0\r\n\r\n")]
        [TestCase("0\r\nContent-Length: 0\r\n\r\n")]
        [TestCase("0\r\nHost: evil\r\n\r\n")]
        [TestCase("0\r\nBad Name: value\r\n\r\n")]
        public async Task MalformedChunkedBodiesFailAndRemainFailed(string wire)
        {
            using var source = new AsyncOnlyStream(Encoding.ASCII.GetBytes(wire), 1);
            using var body = Chunked(source, Array.Empty<byte>());
            await Assert.ThatAsync(async () => await body.CopyToAsync(Stream.Null), wire == "3\r\nab" ? Throws.InstanceOf<EndOfStreamException>() : Throws.InstanceOf<InvalidDataException>());
            await Assert.ThatAsync(async () => await body.ReadAsync(new byte[1]), Throws.InstanceOf<InvalidDataException>());
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(5)]
        public async Task ChunkedPipelinedRequestsSurvivePartialOrUnreadBodies(int consumed)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var listener = new Net.HttpListener();
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            listener.AddPrefix(url);
            listener.Start();
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
            var stream = client.GetStream();
            var accept = listener.GetContextAsync(stop.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST /body HTTP/1.1\r\nHost: 127.0.0.1\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2\r\nde\r\n0\r\nX-Trailer: ok\r\n\r\nGET /next HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"), stop.Token);
            var first = await accept;
            Assert.That(first.Request.HasEntityBody, Is.True);
            Assert.That(first.Request.ContentLength64, Is.EqualTo(-1));
            var data = new byte[consumed];
            await first.Request.InputStream.ReadExactlyAsync(data, stop.Token);
            Assert.That(Encoding.ASCII.GetString(data), Is.EqualTo("abcde".Substring(0, consumed)));
            var oldBody = first.Request.InputStream;
            first.Response.StatusCode = 204;
            first.Response.ContentLength64 = 0;
            first.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
            first.Close();
            var second = await listener.GetContextAsync(stop.Token);
            Assert.That(second.Request.RawTarget, Is.EqualTo("/next"));
            Assert.That(second.Request.Headers["X-Trailer"], Is.Null);
            Assert.That(await oldBody.ReadAsync(new byte[20], stop.Token), Is.Zero);
            second.Response.StatusCode = 204;
            second.Response.ContentLength64 = 0;
            second.Response.KeepAlive = false;
            second.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
            second.Close();
            using var replies = new MemoryStream();
            await stream.CopyToAsync(replies, stop.Token);
            Assert.That(Encoding.ASCII.GetString(replies.ToArray()).Split("HTTP/1.1 204", StringSplitOptions.None).Length, Is.EqualTo(3));
        }

        [TestCase("example.test:443")]
        [TestCase("127.0.0.1:443")]
        [TestCase("[::1]:443")]
        public async Task UnsupportedConnectClosesBeforeOptimisticSuccessor(string authority)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new Net.HttpListener();
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            listener.AddPrefix(url);
            listener.Start();
            var accept = listener.GetContextAsync(stop.Token);
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
            var stream = client.GetStream();
            var wire = "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority
                + "\r\n\r\nGET /must-not-dispatch HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(wire), stop.Token);
            using var replies = new MemoryStream();
            await stream.CopyToAsync(replies, stop.Token);
            var response = Encoding.ASCII.GetString(replies.ToArray());
            Assert.Multiple(() =>
            {
                Assert.That(response, Is.Empty, "Current unsupported authority-form parsing closes without a response.");
                Assert.That(accept.IsCompleted, Is.False, "Rejected CONNECT and optimistic bytes must not dispatch.");
            });

            using var healthy = new TcpClient();
            await healthy.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
            await healthy.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                "GET /healthy HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"), stop.Token);
            var context = await accept;
            Assert.That(context.Request.RawTarget, Is.EqualTo("/healthy"));
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
            context.Close();
            using var healthyReply = new MemoryStream();
            await healthy.GetStream().CopyToAsync(healthyReply, stop.Token);
            Assert.That(Encoding.ASCII.GetString(healthyReply.ToArray()), Does.StartWith("HTTP/1.1 204 "));
        }

        [TestCase("Content-Length: 1\r\nContent-Length: 2\r\n")]
        [TestCase("Content-Length: +1\r\n")]
        [TestCase("Content-Length: 1, 2\r\n")]
        [TestCase("Content-Length: 0\r\nTransfer-Encoding: chunked\r\n")]
        [TestCase("Transfer-Encoding: gzip, chunked\r\n")]
        [TestCase("Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n")]
        [TestCase("Host: duplicate\r\n")]
        [TestCase("Bad Name: value\r\n")]
        [TestCase("X-Value: a\0b\r\n")]
        [TestCase("X-Value: a\nb\r\n")]
        public async Task AmbiguousRequestsNeverReachApplication(string headers)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var listener = new Net.HttpListener();
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            listener.AddPrefix(url);
            listener.Start();
            var accept = listener.GetContextAsync(stop.Token);
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("POST / HTTP/1.1\r\nHost: 127.0.0.1\r\n" + headers + "\r\n"), stop.Token);
            try { Assert.That(await stream.ReadAsync(new byte[1], stop.Token), Is.Zero); }
            catch (IOException) { /* Reset is also an unambiguous terminal rejection. */ }
            Assert.That(accept.IsCompleted, Is.False);
            listener.Stop();
            await Assert.ThatAsync(async () => await accept, Throws.InstanceOf<System.Net.HttpListenerException>());
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(8192)]
        public void ChunkedDecodingMatchesAcrossEveryBufferedSplit(int readSize)
        {
            var wire = Encoding.ASCII.GetBytes("1\r\na\r\n4; x=y\r\nbcde\r\n0\r\nX-End: yes\r\n\r\nNEXT");
            for (var split = 0; split <= wire.Length; split++)
            {
                using var source = new MemoryStream(wire, split, wire.Length - split);
                using var body = Chunked(source, wire.Take(split).ToArray());
                using var decoded = new MemoryStream();
                var buffer = new byte[readSize];
                int read;
                while ((read = body.Read(buffer, 0, buffer.Length)) != 0) decoded.Write(buffer, 0, read);
                Assert.That(Encoding.ASCII.GetString(decoded.ToArray()), Is.EqualTo("abcde"), $"split {split}");
                var tail = (ArraySegment<byte>)((body.GetType().GetProperty("BufferedRemainder", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(body) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
                using var successor = new MemoryStream();
                if (tail.Count > 0) successor.Write((tail.Array ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")), tail.Offset, tail.Count);
                source.CopyTo(successor);
                Assert.That(Encoding.ASCII.GetString(successor.ToArray()), Is.EqualTo("NEXT"), $"split {split}");
            }
        }

        [Test]
        public async Task ChunkMetadataAndTrailersHaveIndependentBounds()
        {
            foreach (var wire in new[] { "1;" + new string('a', 8192) + "\r\nx\r\n0\r\n\r\n",
                "0\r\n" + string.Concat(Enumerable.Repeat("X: " + new string('a', 4000) + "\r\n", 9)) + "\r\n" })
            {
                using var source = new MemoryStream(Encoding.ASCII.GetBytes(wire));
                using var body = Chunked(source, Array.Empty<byte>());
                await Assert.ThatAsync(async () => await body.CopyToAsync(Stream.Null), Throws.InstanceOf<InvalidDataException>());
            }
        }

        [Test]
        public async Task PendingFixedLengthReadHonorsCancellationAndCanResume()
        {
            using var source = new CancellableSource();
            using var body = Body(source, Array.Empty<byte>(), 4);
            using var stop = new CancellationTokenSource();
            var pending = body.ReadAsync(new byte[4], 0, 4, stop.Token);
            Assert.That(source.Started.Task.IsCompleted, Is.True);
            stop.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            source.Resume = true;
            var buffer = new byte[10];
            Assert.That(await body.ReadAsync(buffer, 0, buffer.Length), Is.EqualTo(4));
            Assert.That(Encoding.ASCII.GetString(buffer, 0, 4), Is.EqualTo("body"));
            Assert.That(await body.ReadAsync(buffer, 0, buffer.Length), Is.Zero);
        }

        private sealed class CancellableSource : MemoryStream
        {
            internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Resume;
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous read.");
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                if (!Resume) await Task.Delay(Timeout.Infinite, cancellationToken);
                Assert.That(count, Is.EqualTo(4));
                Encoding.ASCII.GetBytes("body").CopyTo(buffer, offset);
                return 4;
            }
        }

        private static Stream Body(Stream source, byte[] buffered, long length)
            => (Stream)(Activator.CreateInstance((typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.RequestStream", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { source, buffered, 0, buffered.Length, length }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));

        private static Stream Chunked(Stream source, byte[] buffered)
            => (Stream)(Activator.CreateInstance((typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ChunkedRequestStream", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { source, buffered, 0, buffered.Length }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));

        private sealed class AsyncOnlyStream : MemoryStream
        {
            private readonly byte[] _bytes;
            private readonly int _fragment;
            internal AsyncOnlyStream(byte[] bytes, int fragment = int.MaxValue) : base(bytes)
            { _bytes = bytes; _fragment = fragment; }
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous transport read.");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(base.Read(buffer, offset, Math.Min(count, _fragment)));
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(Math.Min(buffer.Length, _fragment), Length - Position);
                _bytes.AsMemory((int)Position, count).CopyTo(buffer);
                Position += count;
                return new ValueTask<int>(count);
            }
        }
    }
}
