using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BrotliRequestStreamTest
    {
        private static Stream Decoder(Stream source)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.BrotliRequestStream")
                ?? throw new AssertionException("Missing Brotli request decoder in the .NET 10 asset.");
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { source }, null) as Stream ?? throw new AssertionException("Missing decoder instance.");
        }

        private static byte[] Encode(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionMode.Compress, true))
                compressor.Write(bytes);
            return output.ToArray();
        }

        private sealed class FragmentedSource(byte[] data, int chunk) : MemoryStream(data, false)
        {
            public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
            public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(Read(buffer.Span));
            }
        }

        private sealed class DelayedSource(byte[] data) : MemoryStream(data, false)
        {
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int DisposeCount;
            private bool _closed;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                ObjectDisposedException.ThrowIf(_closed, this);
                return base.Read(buffer.Span);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { DisposeCount++; _closed = true; Release.TrySetResult(); }
                base.Dispose(disposing);
            }
        }

        [TestCase(0, 1, false)]
        [TestCase(257, 1, false)]
        [TestCase(196608, 113, false)]
        [TestCase(0, 1, true)]
        [TestCase(257, 1, true)]
        [TestCase(196608, 113, true)]
        public async Task ValidStreamPreservesBytesAcrossInputAndOutputBoundaries(int length, int chunk, bool asynchronous)
        {
            var expected = new byte[length];
            new Random(20261008).NextBytes(expected);
            using var source = new FragmentedSource(Encode(expected), chunk);
            using var decoder = Decoder(source);
            using var actual = new MemoryStream();
            var buffer = new byte[257];
            int count;
            while ((count = asynchronous ? await decoder.ReadAsync(buffer.AsMemory()) : decoder.Read(buffer)) != 0)
                actual.Write(buffer, 0, count);
            Assert.That(actual.ToArray(), Is.EqualTo(expected));
            Assert.That(decoder.Read(buffer), Is.Zero);
        }

        [Test]
        public async Task ZeroLengthReadsDoNotConsumeOrValidateInput()
        {
            using var source = new MemoryStream(new byte[] { 0xff });
            using var decoder = Decoder(source);
            Assert.That(decoder.Read(Span<byte>.Empty), Is.Zero);
            Assert.That(await decoder.ReadAsync(Memory<byte>.Empty), Is.Zero);
            Assert.That(source.Position, Is.Zero);
            await Assert.ThatAsync(async () => await decoder.ReadAsync(new byte[1].AsMemory()), Throws.TypeOf<HttpException>());
        }

        [Test]
        public async Task ConcurrentReadIsRejectedWithoutCorruptingFirstRead()
        {
            var expected = new byte[] { 1, 2, 3 };
            var source = new DelayedSource(Encode(expected));
            using var decoder = Decoder(source);
            var buffer = new byte[8];
            var first = decoder.ReadAsync(buffer.AsMemory()).AsTask();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Assert.ThatAsync(async () => await decoder.ReadAsync(new byte[8].AsMemory()), Throws.TypeOf<InvalidOperationException>());
            source.Release.TrySetResult();
            Assert.That(await first.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(3));
            Assert.That(buffer[..3], Is.EqualTo(expected));
            Assert.That(await decoder.ReadAsync(buffer.AsMemory()), Is.Zero);
        }

        [Test]
        public async Task CanceledPendingReadPreservesTokenAndCannotResumePartialCoding()
        {
            var source = new DelayedSource(Encode(new byte[] { 1, 2, 3 }));
            using var decoder = Decoder(source);
            using var stop = new CancellationTokenSource();
            var pending = decoder.ReadAsync(new byte[8].AsMemory(), stop.Token).AsTask();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            stop.Cancel();
            var error = await Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(error.CancellationToken, Is.EqualTo(stop.Token));
            Assert.Throws<IOException>(() => decoder.ReadByte());
        }

        [Test]
        public async Task DisposeDuringPendingReadClosesSourceOnceAndRejectsSubsequentReads()
        {
            var source = new DelayedSource(Encode(new byte[] { 1, 2, 3 }));
            using var decoder = Decoder(source);
            var pending = decoder.ReadAsync(new byte[8].AsMemory()).AsTask();
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            decoder.Dispose();
            decoder.Dispose();
            await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(2)), Throws.TypeOf<ObjectDisposedException>());
            Assert.That(source.DisposeCount, Is.EqualTo(1));
            Assert.That(decoder.CanRead, Is.False);
            Assert.Throws<ObjectDisposedException>(() => decoder.ReadByte());
        }
    }
}
