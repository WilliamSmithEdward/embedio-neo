using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class RawRequestBodyStreamTest
    {
        [Test]
        public void RawAndDecodedOptionsHaveIndependentNullPreservingSnapshots()
        {
            var options = new WebServerOptions();
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.Null);
            Assert.That(options.WithMaximumRequestBodyBytes(0), Is.SameAs(options));
            options.MaximumDecompressedRequestBodyBytes = 7;
            Assert.That(options.MaximumRequestBodyBytes, Is.Zero);
            options.MaximumRequestBodyBytes = long.MaxValue;
            Assert.That(options.MaximumDecompressedRequestBodyBytes, Is.EqualTo(7));
            options.MaximumRequestBodyBytes = null;
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
            options.Lock();
            Assert.That(() => options.WithMaximumRequestBodyBytes(1), Throws.TypeOf<InvalidOperationException>());
            Assert.That(options.MaximumRequestBodyBytes, Is.Null);
        }

        [Test]
        public void NegativeConfigurationCannotReplaceAnExistingPolicy()
        {
            var options = new WebServerOptions().WithMaximumRequestBodyBytes(3);
            Assert.That(() => options.MaximumRequestBodyBytes = -1,
                Throws.TypeOf<ArgumentOutOfRangeException>().With.Property("ParamName").EqualTo("value"));
            Assert.That(options.MaximumRequestBodyBytes, Is.EqualTo(3));
        }

        private sealed class PendingSource : MemoryStream
        {
            private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private byte[]? _bytes;
            private int _offset;
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                _bytes = buffer; _offset = offset;
                return _completion.Task;
            }
            internal void Complete()
            {
                if (_bytes != null) _bytes[_offset] = 50;
                _completion.TrySetResult(1);
            }
        }
        private static Stream Limited(Stream input, long maximum)
            => Activator.CreateInstance(typeof(WebServer).Assembly.GetType("EmbedIO.Internal.RawRequestBodyStream")
                ?? throw new AssertionException("Missing raw body view."), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { input, maximum }, null) as Stream
                ?? throw new AssertionException("Missing raw body constructor.");

        private static Task Cleanup(Stream input)
            => input.GetType().GetMethod("CompleteInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(input, new object[] { CancellationToken.None }) as Task
                ?? throw new AssertionException("Missing bounded cleanup.");

        private static async Task<int> Read(Stream input, byte[] bytes, string mode)
            => mode switch
            {
                "array" => input.Read(bytes, 0, bytes.Length),
                "async-array" => await input.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false),
                "span" => input.Read(bytes.AsSpan()),
                "memory" => await input.ReadAsync(bytes.AsMemory()).ConfigureAwait(false),
                "byte" => ReadOne(input, bytes),
                _ => throw new AssertionException("Unknown read mode."),
            };

        private static int ReadOne(Stream input, byte[] bytes)
        {
            var value = input.ReadByte();
            if (value < 0) return 0;
            bytes[0] = (byte)value;
            return 1;
        }

        [TestCase("array")]
        [TestCase("async-array")]
        [TestCase("span")]
        [TestCase("memory")]
        [TestCase("byte")]
        public async Task ExcessByteIsNeverExposedAndFailureIsSticky(string mode)
        {
            using var source = new MemoryStream(new byte[] { 10, 20, 30 });
            using var input = Limited(source, 2);
            var bytes = new byte[] { 99, 99, 99 };
            Assert.That(await Read(input, bytes, mode).ConfigureAwait(false), Is.EqualTo(mode == "byte" ? 1 : 2));
            if (mode == "byte") Assert.That(await Read(input, bytes, mode).ConfigureAwait(false), Is.EqualTo(1));
            Array.Fill(bytes, (byte)99);
            await Assert.ThatAsync(async () => await Read(input, bytes, mode).ConfigureAwait(false), Throws.InstanceOf<HttpException>()).ConfigureAwait(false);
            Assert.That(bytes, Is.All.EqualTo(99));
            Assert.That(source.Position, Is.EqualTo(3));
            await Assert.ThatAsync(async () => await input.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false),
                Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413)).ConfigureAwait(false);
            Assert.That(source.Position, Is.EqualTo(3));
        }

        [TestCase(0)]
        [TestCase(2)]
        public async Task ExactBoundaryNeedsActualEofAndKeepsBorrowedSource(long maximum)
        {
            using var source = new MemoryStream(maximum == 0 ? Array.Empty<byte>() : new byte[] { 10, 20 });
            var input = Limited(source, maximum);
            var bytes = new byte[3];
            Assert.That(await input.ReadAsync(bytes, 0, bytes.Length).ConfigureAwait(false), Is.EqualTo(maximum));
            Assert.That(await input.ReadAsync(bytes.AsMemory()).ConfigureAwait(false), Is.Zero);
            input.Dispose();
            await Cleanup(input).ConfigureAwait(false);
            Assert.That(source.CanRead, Is.True);
        }

        [Test]
        public async Task DisposedApplicationViewStillHasBoundedCleanup()
        {
            using var source = new MemoryStream(new byte[] { 10, 20, 30, 40 });
            var input = Limited(source, 2);
            Assert.That(input.ReadByte(), Is.EqualTo(10));
            input.Dispose();
            Assert.That(() => input.ReadByte(), Throws.TypeOf<ObjectDisposedException>());
            await Assert.ThatAsync(() => Cleanup(input), Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413)).ConfigureAwait(false);
            Assert.That(source.Position, Is.EqualTo(3), "Cleanup must stop after the first established excess byte.");
            Assert.That(source.CanRead, Is.True);
            await Assert.ThatAsync(() => Cleanup(input), Throws.InstanceOf<HttpException>()).ConfigureAwait(false);
            Assert.That(source.Position, Is.EqualTo(3));
        }

        [Test]
        public async Task PreCanceledProbeDoesNotConsumeInputOrResetBoundary()
        {
            using var source = new MemoryStream(new byte[] { 10 });
            using var input = Limited(source, 0);
            var bytes = new byte[] { 99 };
            await Assert.ThatAsync(async () => await input.ReadAsync(bytes, 0, 1, new CancellationToken(true)).ConfigureAwait(false),
                Throws.InstanceOf<OperationCanceledException>()).ConfigureAwait(false);
            Assert.That(source.Position, Is.Zero);
            await Assert.ThatAsync(async () => await input.ReadAsync(bytes.AsMemory()).ConfigureAwait(false),
                Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413)).ConfigureAwait(false);
            Assert.That(bytes[0], Is.EqualTo(99));
        }

        [Test]
        public async Task CopyHelpersShareCounterAndCannotBypassWithZeroLengthReads()
        {
            using var source = new MemoryStream(new byte[] { 10, 20, 30 });
            using var input = Limited(source, 2);
            using var output = new MemoryStream();
            Assert.That(input.Read(Array.Empty<byte>(), 0, 0), Is.Zero);
            await Assert.ThatAsync(() => input.CopyToAsync(output), Throws.InstanceOf<HttpException>()).ConfigureAwait(false);
            Assert.That(output.ToArray(), Is.EqualTo(new byte[] { 10, 20 }));
            Assert.That(source.Position, Is.EqualTo(3));
            Assert.That(() => input.Read(Array.Empty<byte>(), 0, 0), Throws.InstanceOf<HttpException>());
        }

        [Test]
        public async Task PrivateProbeStaysOwnedUntilActualBorrowedReadSettles()
        {
            using var source = new PendingSource();
            using var input = Limited(source, 0);
            using var cancel = new CancellationTokenSource();
            var application = new byte[] { 99 };
            var read = input.ReadAsync(application, 0, 1, cancel.Token);
            try
            {
                cancel.Cancel();
                Assert.That(read.IsCompleted, Is.False, "A cancellation request is not actual native settlement.");
                await Assert.ThatAsync(async () => await input.ReadAsync(application, 0, 1).ConfigureAwait(false),
                    Throws.TypeOf<InvalidOperationException>()).ConfigureAwait(false);
                input.Dispose();
                Assert.That(source.CanRead, Is.True);
            }
            finally { source.Complete(); }
            await Assert.ThatAsync(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Throws.InstanceOf<HttpException>().With.Property("StatusCode").EqualTo(413)).ConfigureAwait(false);
            Assert.That(application[0], Is.EqualTo(99));
            Assert.That(source.CanRead, Is.True);
        }
    }
}
