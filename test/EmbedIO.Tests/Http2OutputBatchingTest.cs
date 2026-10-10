using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        private static readonly Type FieldType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)
            ?? throw new AssertionException("Missing HPACK field.");

        private static Array Fields(params (string Name, string Value)[] fields)
        {
            var array = Array.CreateInstance(FieldType, fields.Length);
            for (var i = 0; i < fields.Length; i++)
                array.SetValue(Activator.CreateInstance(FieldType, Flags, null, new object[] { fields[i].Name, fields[i].Value, false }, null), i);
            return array;
        }

        private static Task SendHeaders(object connection, int id, Array fields, CancellationToken token)
            => (Task)((ConnectionType.GetMethod("SendHeadersAsync", Flags) ?? throw new AssertionException("Missing header writer."))
                .Invoke(connection, new object[] { id, fields, true, token }) ?? throw new AssertionException("Missing header task."));

        private static async Task<List<object>> ReadAll(byte[] bytes)
        {
            using var wire = new MemoryStream(bytes);
            using var reader = (IDisposable)Transport(wire);
            var frames = new List<object>();
            object? frame;
            while ((frame = await Read(reader)) != null) frames.Add(frame);
            return frames;
        }

        [Test]
        public async Task WritesQueuedBehindABlockedWriteShareOneTransportWriteInOrder()
        {
            using var output = new BlockingFirstWrite();
            using var connection = (IDisposable)Connection(output, error => Assert.Fail("Output failed: " + error));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8]));
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = Enumerable.Range(1, 10).Select(index =>
            {
                var payload = new byte[8];
                payload[0] = (byte)index;
                return SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, payload));
            }).ToArray();
            Assert.That(queued.Any(task => task.IsCompleted), Is.False, "Queued writes complete only once their bytes are written.");
            output.Release.TrySetResult(true);
            await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(output.Writes, Is.EqualTo(new[] { 17, 170 }), "Queued frames must be coalesced into one transport write.");
            var frames = await ReadAll(output.ToArray());
            Assert.That(frames.Select(frame => Property<byte[]>(frame, "Payload")[0]), Is.EqualTo(Enumerable.Range(0, 11).Select(i => (byte)i)),
                "Coalescing must preserve submission order.");
        }

        [Test]
        public async Task CoalescedOutputStaysBoundedAndCompleteForLargeFrames()
        {
            using var output = new BlockingFirstWrite();
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 0, 0, new byte[8]));
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = Enumerable.Range(0, 9).Select(index =>
                Write(transport, Frame(0, 0, 1, Enumerable.Repeat((byte)index, 16384).ToArray()))).ToArray();
            output.Release.TrySetResult(true);
            await Task.WhenAll(queued.Append(first)).WaitAsync(TimeSpan.FromSeconds(2));
            var bound = (int)(TransportType.GetField("OutputBatchBytes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null)
                ?? throw new AssertionException("Missing batch bound."));
            Assert.That(output.Writes.Skip(1), Has.All.LessThanOrEqualTo(bound), "One batch must not exceed the output bound.");
            Assert.That(output.Writes.Count, Is.GreaterThan(2), "Nine 16 KiB frames cannot share one bounded batch.");
            var frames = await ReadAll(output.ToArray());
            Assert.That(frames, Has.Count.EqualTo(10));
            for (var i = 0; i < 9; i++)
                Assert.That(Property<byte[]>(frames[i + 1], "Payload"), Is.EqualTo(Enumerable.Repeat((byte)i, 16384)));
        }

        [Test]
        public async Task CanceledQueuedHeaderBlockNeverChangesTheHpackTable()
        {
            using var output = new BlockingFirstWrite();
            var failures = 0;
            using var connection = (IDisposable)Connection(output, _ => Interlocked.Increment(ref failures));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8]));
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var fields = Fields(("x-indexed", "only-if-sent"));
            using var reset = new CancellationTokenSource();
            var queued = SendHeaders(connection, 1, fields, reset.Token);
            reset.Cancel();
            await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
            output.Release.TrySetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            await SendHeaders(connection, 3, fields, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            var frames = await ReadAll(output.ToArray());
            Assert.That(frames.Select(frame => Property<int>(frame, "StreamId")), Is.EqualTo(new[] { 0, 3 }), "The canceled block must not be written.");
            // A fresh decoder sees exactly the bytes on the wire. If the canceled
            // block had been encoded, stream 3 would refer to an entry it never received.
            var decoderType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackDecoder", true)
                ?? throw new AssertionException("Missing HPACK decoder.");
            var decoder = Activator.CreateInstance(decoderType, Flags, null, new object[] { 32768 }, null)
                ?? throw new AssertionException("Missing HPACK decoder constructor.");
            var decoded = (Array)((decoderType.GetMethod("Decode", Flags) ?? throw new AssertionException("Missing decode."))
                .Invoke(decoder, new object[] { Property<byte[]>(frames[1], "Payload") }) ?? throw new AssertionException("Missing decoded fields."));
            var field = decoded.GetValue(0) ?? throw new AssertionException("Missing field.");
            Assert.That(FieldType.GetProperty("Value")?.GetValue(field), Is.EqualTo("only-if-sent"));
            Assert.That(failures, Is.Zero);
        }

        [Test]
        public async Task RejectedWriteInsideABatchLeavesNoBytesAndSiblingsSucceed()
        {
            using var output = new BlockingFirstWrite();
            var failures = 0;
            using var connection = (IDisposable)Connection(output, _ => Interlocked.Increment(ref failures));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8]));
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var before = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 }));
            // HPACK carries octets only; encoding fails at commit, inside the batch.
            var invalid = SendHeaders(connection, 1, Fields(("x-invalid", "Ā")), CancellationToken.None);
            var after = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }));
            output.Release.TrySetResult(true);
            await Task.WhenAll(first, before, after).WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThatAsync(async () => await invalid.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<ArgumentException>());
            var frames = await ReadAll(output.ToArray());
            Assert.That(frames.Select(frame => Property<byte[]>(frame, "Payload")[0]), Is.EqualTo(new byte[] { 0, 1, 2 }));
            Assert.That(failures, Is.Zero, "An invalid application header is not a transport failure.");
            await SendHeaders(connection, 3, Fields(("x-valid", "after")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }

        [Test]
        public async Task TransportFailureFailsTheBatchAndEveryQueuedWrite()
        {
            using var output = new BlockingFirstWrite { FailAfterFirst = true };
            var failures = 0;
            using var connection = (IDisposable)Connection(output, _ => Interlocked.Increment(ref failures));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8]));
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var queued = Enumerable.Range(0, 4).Select(_ => SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8]))).ToArray();
            output.Release.TrySetResult(true);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
            foreach (var task in queued)
                await Assert.ThatAsync(async () => await task.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<IOException>());
            Assert.That(failures, Is.GreaterThanOrEqualTo(1));
            await Assert.ThatAsync(async () => await SendRequest(connection, CancellationToken.None, Frame(6, 0, 0, new byte[8])),
                Throws.InstanceOf<IOException>(), "Output stays terminal after a failed batch.");
        }

        // Holds the first write until released, then records each write's size.
        private sealed class BlockingFirstWrite : MemoryStream
        {
            private int _writes;
            internal bool FailAfterFirst;
            internal List<int> Writes { get; } = new();
            internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Interlocked.Increment(ref _writes) == 1)
                {
                    Started.TrySetResult(true);
                    await Release.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
                }
                else if (FailAfterFirst) throw new IOException("Injected batch failure.");
                lock (Writes) Writes.Add(count);
                Write(buffer, offset, count);
            }
        }
    }
}
