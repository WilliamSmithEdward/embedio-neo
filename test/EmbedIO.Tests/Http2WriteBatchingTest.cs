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
        [TestCase(1)]
        [TestCase(16)]
        [TestCase(17)]
        [TestCase(40)]
        public async Task QueuedFramesBatchWithinBoundsAndAwaitTheTransport(int queuedCount)
        {
            using var output = new BatchOutput();
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 1, 0, new byte[8]));
            await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = Enumerable.Range(1, queuedCount).Select(i => Write(transport,
                Frame(6, 1, 0, new byte[] { 0, 0, 0, 0, 0, 0, 0, (byte)i }))).ToArray();
            try
            {
                output.FirstRelease.TrySetResult();
                await output.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(queued, Is.All.Matches<Task>(task => task != null && !task.IsCompleted));
                output.SecondRelease.TrySetResult();
                await Task.WhenAll(queued.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(output.Sizes, Has.Count.EqualTo(1 + (queuedCount + 15) / 16));
                Assert.That(output.Sizes, Is.All.LessThanOrEqualTo(65536));
                using var wire = new MemoryStream(output.ToArray());
                using var reader = (IDisposable)Transport(wire);
                for (var i = 0; i <= queuedCount; i++)
                {
                    var frame = await Read(reader) ?? throw new AssertionException("Missing queued frame.");
                    Assert.That(Property<byte[]>(frame, "Payload")[7], Is.EqualTo((byte)i));
                }
                Assert.That(await Read(reader), Is.Null);
            }
            finally { output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult(); await Task.WhenAll(queued.Prepend(first)); }
        }

        [Test]
        public async Task DataBatchBytesDoNotExceedTheCopyBound()
        {
            using var output = new BatchOutput();
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 1, 0, new byte[8]));
            await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = Enumerable.Range(0, 4).Select(i => Write(transport,
                Frame(0, 0, i * 2 + 1, Enumerable.Repeat((byte)(i + 1), 16384).ToArray()))).ToArray();
            output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult();
            await Task.WhenAll(queued.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(output.Sizes, Is.EqualTo(new[] { 17, 3 * 16393, 16393 }));
            using var wire = new MemoryStream(output.ToArray());
            using var reader = (IDisposable)Transport(wire);
            await Read(reader);
            for (var i = 0; i < 4; i++)
                Assert.That(Property<byte[]>(await Read(reader) ?? throw new AssertionException("Missing DATA."), "Payload"),
                    Is.All.EqualTo((byte)(i + 1)));
        }

        [Test]
        public async Task CanceledQueuedPayloadCanBeReusedBeforeSharedOutputUnblocks()
        {
            using var output = new BatchOutput();
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 1, 0, new byte[8]));
            await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancel = new CancellationTokenSource();
            var bytes = Enumerable.Repeat((byte)7, 32).ToArray();
            var frames = Array.CreateInstance(FrameType, 1);
            frames.SetValue(Frame(0, 0, 1, bytes), 0);
            var canceled = (Task)(TransportType.GetMethod("WriteAsync", Flags)?.Invoke(transport,
                new object[] { frames, 16384, cancel.Token }) ?? throw new AssertionException("Missing write."));
            try
            {
                cancel.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await canceled.WaitAsync(TimeSpan.FromSeconds(3)));
                Array.Fill(bytes, (byte)255);
                var healthy = Write(transport, Frame(6, 1, 0, new byte[8]));
                output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult();
                await Task.WhenAll(first, healthy).WaitAsync(TimeSpan.FromSeconds(3));
                Assert.That(output.Length, Is.EqualTo(34));
                Assert.That(Property<bool>(transport, "IsWriteFailed"), Is.False);
            }
            finally { output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult(); await first; }
        }

        [Test]
        public async Task CoalescingKeepsHeaderBlockFramesAdjacent()
        {
            using var output = new BatchOutput();
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 1, 0, new byte[8]));
            await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var headers = Write(transport, Frame(1, 0, 1, new byte[] { 0x88 }), Frame(9, 4, 1, Array.Empty<byte>()));
            var sibling = Write(transport, Frame(1, 4, 3, new byte[] { 0x88 }));
            output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult();
            await Task.WhenAll(first, headers, sibling).WaitAsync(TimeSpan.FromSeconds(3));
            using var wire = new MemoryStream(output.ToArray());
            using var reader = (IDisposable)Transport(wire);
            await Read(reader);
            var ids = new List<int>(); var types = new List<byte>();
            for (var i = 0; i < 3; i++)
            {
                var frame = await Read(reader) ?? throw new AssertionException("Missing header frame.");
                ids.Add(Property<int>(frame, "StreamId")); types.Add(Property<byte>(frame, "Type"));
            }
            Assert.That(ids, Is.EqualTo(new[] { 1, 1, 3 }));
            Assert.That(types, Is.EqualTo(new byte[] { 1, 9, 1 }));
        }

        [Test]
        public async Task PartialBatchFailureFailsCommittedAndWaitingWriters()
        {
            using var output = new BatchOutput { FailSecond = true };
            using var transport = (IDisposable)Transport(output);
            var first = Write(transport, Frame(6, 1, 0, new byte[8]));
            await output.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = Enumerable.Range(0, 20).Select(_ => Write(transport, Frame(6, 1, 0, new byte[8]))).ToArray();
            output.FirstRelease.TrySetResult(); output.SecondRelease.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(3));
            foreach (var task in queued)
                await Assert.ThrowsAsync<IOException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.That(Property<bool>(transport, "IsWriteFailed"), Is.True);
        }

        private sealed class BatchOutput : MemoryStream
        {
            internal List<int> Sizes { get; } = new();
            internal bool FailSecond { get; init; }
            internal TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                Sizes.Add(count);
                if (Sizes.Count == 1) { FirstStarted.TrySetResult(); await FirstRelease.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
                if (Sizes.Count == 2)
                {
                    SecondStarted.TrySetResult(); await SecondRelease.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    if (FailSecond) { base.Write(buffer, offset, 4); throw new IOException("Injected partial shared write."); }
                }
                base.Write(buffer, offset, count);
            }
        }
    }
}
