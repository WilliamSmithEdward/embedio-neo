using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BoundedFrameReadGrowthTest
    {
        private static readonly Func<Stream, int, int, Task<byte[]>> Read =
            (typeof(WebServer).Assembly.GetType("EmbedIO.Internal.StreamExtensions", true)
                ?.GetMethod("ReadBytesAsync", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("Missing byte reader."))
            .CreateDelegate<Func<Stream, int, int, Task<byte[]>>>();

        [TestCase(65538, 210000)]
        [TestCase(131074, 405000)]
        public void GrowthBeyondPowerOfTwoDoesNotAllocateOversizedBackingAndFinalCopy(int length, long budget)
        {
            var data = new byte[length];
            new Random(190).NextBytes(data);
            using var stream = new MemoryStream(data, false);
            _ = Read(stream, length, 1024).GetAwaiter().GetResult();
            stream.Position = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = Read(stream, length, 1024);
            var completed = pending.IsCompletedSuccessfully;
            var result = pending.GetAwaiter().GetResult();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(completed, Is.True);
            Assert.That(result, Is.EqualTo(data));
            Assert.That(allocated, Is.LessThan(budget));
            result[0] ^= 255;
            Assert.That(result[0], Is.Not.EqualTo(data[0]));
        }

        [TestCase(4097, 4097)]
        [TestCase(65538, 65538)]
        [TestCase(65538, 65537)]
        [TestCase(131074, 131074)]
        [TestCase(131074, 65539)]
        [TestCase(int.MaxValue, 7)]
        public async Task ShortReadsKeepExactLengthAndIndependentOwnership(int requested, int available)
        {
            var data = new byte[available];
            new Random(190).NextBytes(data);
            using var stream = new ShortStream(data);
            var first = await Read(stream, requested, 1024);
            Assert.That(first, Is.EqualTo(data));
            stream.Position = 0;
            var second = await Read(stream, requested, 1024);
            first[0] ^= 255;
            Assert.That(second, Is.EqualTo(data));
        }

        [Test]
        public void LargeAdvertisedLengthWithImmediateEofKeepsAllocationBounded()
        {
            using var stream = new MemoryStream();
            _ = Read(stream, int.MaxValue, 1024).GetAwaiter().GetResult();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = Read(stream, int.MaxValue, 1024).GetAwaiter().GetResult();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(result, Is.Empty);
            Assert.That(allocated, Is.LessThan(4096));
        }

        [Test]
        public async Task HugeLengthPendingFirstReadDoesNotAllocatePayload()
        {
            using var stream = new PendingEofStream();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var pending = Read(stream, int.MaxValue, 1024);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            try
            {
                Assert.That(pending.IsCompleted, Is.False);
                Assert.That(allocated, Is.LessThan(8192));
            }
            finally { stream.Complete(); }
            Assert.That(await pending, Is.Empty);
        }

        private sealed class PendingEofStream : MemoryStream
        {
            private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Complete() => _completion.TrySetResult(0);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => _completion.Task;
        }

        private sealed class ShortStream : MemoryStream
        {
            internal ShortStream(byte[] data) : base(data, false) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => base.ReadAsync(buffer, offset, Math.Min(count, 13), cancellationToken);
        }
    }
}
