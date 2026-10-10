using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        // Frames for one stream until a PING acknowledgement proves nothing else is pending.
        private static async Task<List<(byte Type, byte Flags, int Id, byte[] Payload)>> StreamFramesThroughPing(Stream wire, int id, CancellationToken token)
        {
            await SendWire(wire, 6, 0, 0, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }, token);
            var frames = new List<(byte Type, byte Flags, int Id, byte[] Payload)>();
            while (true)
            {
                var frame = await ReceiveWire(wire, token);
                if (frame.Type == 6 && (frame.Flags & 1) != 0) return frames;
                Assert.That(frame.Type, Is.Not.EqualTo(3), "Unexpected stream reset.");
                if (frame.Id == id) frames.Add(frame);
            }
        }

        [Test]
        public async Task ContentLengthResponseSendsHeadersAndEndingDataInOneWrite()
        {
            var body = new byte[] { 1, 2, 3, 4, 5 };
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            WriteRecorder? recorder = null;
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                await completed.Task.WaitAsync(token);
                var frames = await StreamFramesThroughPing(wire, 1, token);
                Assert.That(frames.Select(frame => (frame.Type, frame.Flags)), Is.EqualTo(new[] { ((byte)1, (byte)4), ((byte)0, (byte)1) }),
                    "A complete Content-Length body ends with its last DATA frame, without a separate empty frame.");
                Assert.That(frames[1].Payload, Is.EqualTo(body));
                var writes = (recorder ?? throw new AssertionException("Missing recorder.")).Writes;
                Assert.That(writes.Any(write => write.Contains(1) && write.Contains(0)), Is.True,
                    "Response headers and their first DATA frame must share one transport write.");
            }, app: async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body, 0, body.Length, context.CancellationToken);
                    Assert.That(GetEnded(exchange), Is.True, "The exchange ends once its declared body is written.");
                }
                finally { context.Close(); }
                completed.TrySetResult();
            }, wrapTransport: stream => recorder = new WriteRecorder(stream));
        }

        [Test]
        public async Task WriteBeyondACompletedContentLengthStillFailsAsOverLength()
        {
            var observed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                Assert.That(await observed.Task.WaitAsync(token), Is.InstanceOf<InvalidDataException>());
                var frames = await StreamFramesThroughPing(wire, 1, token);
                Assert.That(frames.Where(frame => frame.Type == 0).SelectMany(frame => frame.Payload), Is.EqualTo(new byte[] { 7, 8, 9 }));
                Assert.That(frames.Last().Flags & 1, Is.EqualTo(1));
            }, app: async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    context.Response.ContentLength64 = 3;
                    await context.Response.OutputStream.WriteAsync(new byte[] { 7, 8, 9 }, 0, 3, context.CancellationToken);
                    try
                    {
                        await context.Response.OutputStream.WriteAsync(new byte[] { 10 }, 0, 1, context.CancellationToken);
                        observed.TrySetResult(null);
                    }
                    catch (InvalidDataException error) { observed.TrySetResult(error); }
                }
                finally { context.Close(); }
            });
        }

        [Test]
        public async Task ExplicitFlushSendsHeadersBeforeAnyBodyAndUnknownLengthEndsAtClose()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithRawServer(Array.Empty<byte>(), async (wire, token) =>
            {
                try
                {
                    await SendWire(wire, 1, 5, 1, RequestBlock(), token);
                    var headers = await Until(wire, 1, 1, token);
                    Assert.That(headers.Flags & 1, Is.Zero, "Flushed headers must leave the stream open.");
                    release.TrySetResult();
                    var data = await Until(wire, 0, 1, token);
                    Assert.That((data.Payload, data.Flags & 1), Is.EqualTo((new byte[] { 65, 66 }, 0)));
                    var end = await Until(wire, 0, 1, token);
                    Assert.That((end.Payload.Length, end.Flags & 1), Is.EqualTo((0, 1)), "A body of unknown length ends at close.");
                }
                finally { release.TrySetResult(); }
            }, app: async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    context.Response.SendChunked = true;
                    await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await context.Response.OutputStream.WriteAsync(new byte[] { 65, 66 }, 0, 2, context.CancellationToken);
                }
                finally { context.Close(); }
            });
        }

        private static bool GetEnded(object exchange)
            => (bool)((exchange.GetType().GetProperty("Ended") ?? throw new AssertionException("Missing Ended.")).GetValue(exchange)
                ?? throw new AssertionException("Missing Ended value."));

        // Records the frame types each transport write starts frames with.
        private sealed class WriteRecorder(Stream inner) : Stream
        {
            internal List<HashSet<byte>> Writes { get; } = new();
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                var types = new HashSet<byte>();
                for (var position = offset; position + 9 <= offset + count;)
                {
                    types.Add(buffer[position + 3]);
                    position += 9 + ((buffer[position] << 16) | (buffer[position + 1] << 8) | buffer[position + 2]);
                }
                lock (Writes) Writes.Add(types);
                await inner.WriteAsync(buffer.AsMemory(offset, count), token);
            }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => inner.ReadAsync(buffer, offset, count, token);
            public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
