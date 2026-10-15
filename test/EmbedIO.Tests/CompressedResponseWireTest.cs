using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A compressed HTTP/1 response hands the compressor's final block to the output with the
    // body's ending. The chunks on the wire must still be exactly the compressor's writes, one
    // chunk per write in order, as when the compressor wrote to the output directly.
    public sealed class CompressedResponseWireTest
    {
        [TestCase("br", 3, false)]
        [TestCase("br", 3, true)]
        [TestCase("br", 300000, true)]
        [TestCase("gzip", 3, false)]
        [TestCase("gzip", 4096, true)]
        [TestCase("gzip", 300000, false)]
        [TestCase("gzip", 300000, true)]
        [TestCase("deflate", 300000, true)]
        public async Task ChunksAreTheCompressorWrites(string coding, int size, bool asynchronousDisposal)
        {
            // Incompressible bytes make the final block span several compressor writes.
            var body = new byte[size];
            new Random(size).NextBytes(body);
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var uri = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                context.Response.ContentType = "application/octet-stream";
                var output = context.OpenResponseStream(preferCompression: true);
                try { await output.WriteAsync(body, 0, body.Length, stop.Token); }
                finally
                {
                    if (asynchronousDisposal) await output.DisposeAsync();
                    else output.Dispose();
                }
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                var expected = await CompressorWritesAsync(coding, body);
                var wire = await RequestAsync(uri, coding, stop.Token);
                var head = Encoding.ASCII.GetString(wire, 0, HeadLength(wire));
                Assert.That(head, Does.Contain("Transfer-Encoding: chunked"));
                Assert.That(head, Does.Contain("Content-Encoding: " + coding));
                Assert.That(Chunks(wire, HeadLength(wire)), Is.EqualTo(expected));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static async Task<List<byte[]>> CompressorWritesAsync(string coding, byte[] body)
        {
            var recorder = new WriteRecorder();
            Stream compressor = coding switch
            {
                "br" => new BrotliStream(recorder, CompressionLevel.Fastest, leaveOpen: true),
                "gzip" => new GZipStream(recorder, CompressionMode.Compress, leaveOpen: true),
                _ => new DeflateStream(recorder, CompressionMode.Compress, leaveOpen: true),
            };
            await compressor.WriteAsync(body, 0, body.Length);
            await compressor.DisposeAsync();
            Assert.That(recorder.Writes.Count, Is.GreaterThan(0));
            return recorder.Writes;
        }

        private static async Task<byte[]> RequestAsync(Uri uri, string coding, CancellationToken token)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, token);
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\nAccept-Encoding: {coding}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, 0, request.Length, token);
            using var received = new MemoryStream();
            await stream.CopyToAsync(received, 81920, token);
            return received.ToArray();
        }

        private static int HeadLength(byte[] wire)
        {
            for (var i = 3; i < wire.Length; i++)
                if (wire[i - 3] == 13 && wire[i - 2] == 10 && wire[i - 1] == 13 && wire[i] == 10) return i + 1;
            throw new AssertionException("The response head did not end.");
        }

        // Every data chunk, in order; the terminating zero-length chunk must end the response.
        private static List<byte[]> Chunks(byte[] wire, int position)
        {
            var chunks = new List<byte[]>();
            while (true)
            {
                var lineEnd = Array.IndexOf(wire, (byte)13, position);
                var size = int.Parse(Encoding.ASCII.GetString(wire, position, lineEnd - position), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                position = lineEnd + 2;
                if (size == 0)
                {
                    Assert.That(wire.Length, Is.EqualTo(position + 2), "The terminator ends the response.");
                    return chunks;
                }
                chunks.Add(wire.AsSpan(position, size).ToArray());
                position += size;
                Assert.That(wire[position], Is.EqualTo((byte)13));
                Assert.That(wire[position + 1], Is.EqualTo((byte)10));
                position += 2;
            }
        }

        private sealed class WriteRecorder : Stream
        {
            internal List<byte[]> Writes { get; } = new();
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer) { if (!buffer.IsEmpty) Writes.Add(buffer.ToArray()); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Write(buffer.AsSpan(offset, count));
                return Task.CompletedTask;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Write(buffer.Span);
                return default;
            }
        }
    }
}
