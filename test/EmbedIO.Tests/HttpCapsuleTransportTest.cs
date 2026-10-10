using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class HttpCapsuleTransportTest
    {
        private static readonly Type CodecType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpCapsuleTransport", true)
            ?? throw new AssertionException("Missing capsule codec.");
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static object Codec(Stream stream) => Activator.CreateInstance(CodecType, Flags, null, new object[] { stream }, null)
            ?? throw new AssertionException("Missing capsule constructor.");
        private static Task Invoke(object codec, string method, params object[] arguments)
            => (Task)(CodecType.GetMethod(method, Flags)?.Invoke(codec, arguments) ?? throw new AssertionException("Missing capsule operation."));
        private static async Task<object?> Header(object codec)
        {
            var task = Invoke(codec, "ReadHeaderAsync", CancellationToken.None);
            await task;
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        private static long Value(object header, string property)
            => (long)(header.GetType().GetProperty(property)?.GetValue(header) ?? throw new AssertionException("Missing capsule header value."));
        private static async Task<int> Read(object codec, byte[] bytes)
        {
            var task = Invoke(codec, "ReadPayloadAsync", bytes, 0, bytes.Length, CancellationToken.None);
            await task;
            return (int)(task.GetType().GetProperty("Result")?.GetValue(task) ?? throw new AssertionException("Missing read count."));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(16)]
        public async Task FragmentedAndNonminimalHeadersPreserveCapsuleBoundaries(int fragment)
        {
            // Type 23 and length 3 use the valid nonminimal eight-byte widths.
            using var stream = new FragmentedInput(new byte[] { 0xc0, 0, 0, 0, 0, 0, 0, 23, 0xc0, 0, 0, 0, 0, 0, 0, 3, 97, 98, 99, 0, 0 }, fragment);
            var codec = Codec(stream);
            var first = await Header(codec) ?? throw new AssertionException("Missing header.");
            Assert.That((Value(first, "Type"), Value(first, "Length")), Is.EqualTo((23L, 3L)));
            using var payload = new MemoryStream();
            var bytes = new byte[16];
            int count;
            while ((count = await Read(codec, bytes)) != 0) payload.Write(bytes, 0, count);
            Assert.That(payload.ToArray(), Is.EqualTo(new byte[] { 97, 98, 99 }));
            var second = await Header(codec) ?? throw new AssertionException("Missing empty capsule.");
            Assert.That((Value(second, "Type"), Value(second, "Length")), Is.EqualTo((0L, 0L)));
            Assert.That(await Header(codec), Is.Null);
            Assert.That(stream.CanRead, Is.True, "The carrier remains caller-owned.");
        }

        [Test]
        public async Task MaximumDeclaredLengthIsStreamedWithoutLengthBasedAllocation()
        {
            using var stream = new MemoryStream(new byte[] { 0, 255, 255, 255, 255, 255, 255, 255, 255, 1, 2, 3 });
            var codec = Codec(stream);
            var header = await Header(codec) ?? throw new AssertionException("Missing header.");
            Assert.That(Value(header, "Length"), Is.EqualTo((1L << 62) - 1));
            var bytes = new byte[4];
            Assert.That(await Read(codec, bytes), Is.EqualTo(3));
            await Assert.ThrowsAsync<EndOfStreamException>(async () => await Read(codec, bytes));
            await Assert.ThrowsAsync<IOException>(async () => await Header(codec));
        }

        [TestCase("c0")]
        [TestCase("c000000000000000")]
        [TestCase("0040")]
        [TestCase("00036162")]
        public async Task TruncationPoisonsTheCarrierInsteadOfReportingACompleteCapsule(string hex)
        {
            using var stream = new FragmentedInput(Convert.FromHexString(hex), 1);
            var codec = Codec(stream);
            await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            {
                _ = await Header(codec);
                await Invoke(codec, "SkipPayloadAsync", CancellationToken.None);
            });
            await Assert.ThrowsAsync<IOException>(async () => await Header(codec));
        }

        [Test]
        public async Task UnknownCapsuleCanBeSkippedInBoundedChunksBeforeTheNextHeader()
        {
            var wire = new byte[4103];
            wire[0] = 23; wire[1] = 0x50; wire[2] = 1; // Length 4097.
            Array.Fill(wire, (byte)7, 3, 4097);
            wire[4100] = 0; wire[4101] = 1; wire[4102] = 42;
            using var stream = new FragmentedInput(wire, 257);
            var codec = Codec(stream);
            Assert.That(Value(await Header(codec) ?? throw new AssertionException("Missing unknown capsule."), "Type"), Is.EqualTo(23));
            await Invoke(codec, "SkipPayloadAsync", CancellationToken.None);
            var next = await Header(codec) ?? throw new AssertionException("Missing following capsule.");
            Assert.That((Value(next, "Type"), Value(next, "Length")), Is.EqualTo((0L, 1L)));
            var payload = new byte[2];
            Assert.That(await Read(codec, payload), Is.EqualTo(1));
            Assert.That(payload[0], Is.EqualTo(42));
        }

        [Test]
        public async Task UnconsumedPayloadAndOversizedWritesDoNotCorruptCarrierState()
        {
            using var stream = new MemoryStream();
            var writer = Codec(stream);
            await Invoke(writer, "WriteHeaderAsync", 0L, 3L, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Invoke(writer, "WriteHeaderAsync", 1L, 0L, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(async () => await Invoke(writer, "WritePayloadAsync", new byte[4], 0, 4, CancellationToken.None));
            await Invoke(writer, "WritePayloadAsync", new byte[] { 97, 98 }, 0, 2, CancellationToken.None);
            await Invoke(writer, "WritePayloadAsync", new byte[] { 99 }, 0, 1, CancellationToken.None);
            await Invoke(writer, "WriteHeaderAsync", 23L, 0L, CancellationToken.None);
            Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0, 3, 97, 98, 99, 23, 0 }));
            stream.Position = 0;
            var reader = Codec(stream);
            _ = await Header(reader);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Header(reader));
            await Invoke(reader, "SkipPayloadAsync", CancellationToken.None);
            Assert.That(Value(await Header(reader) ?? throw new AssertionException("Missing successor."), "Type"), Is.EqualTo(23));
        }

        [Test]
        public async Task PartialOutputFailureIsTerminal()
        {
            using var stream = new PartialFailure();
            var codec = Codec(stream);
            await Assert.ThrowsAsync<IOException>(async () => await Invoke(codec, "WriteHeaderAsync", 0L, 3L, CancellationToken.None));
            await Assert.ThrowsAsync<IOException>(async () => await Invoke(codec, "WriteHeaderAsync", 23L, 0L, CancellationToken.None));
            Assert.That(stream.Length, Is.EqualTo(1));
            Assert.That(stream.CanWrite, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CancellationAfterPartialIoIsTerminalAndConcurrentCallsCannotEnter(bool writing)
        {
            using var stream = writing ? (InterruptibleCarrier)new InterruptedWrite() : new InterruptedRead();
            var codec = Codec(stream);
            using var stop = new CancellationTokenSource();
            var pending = writing ? Invoke(codec, "WriteHeaderAsync", 0L, 3L, stop.Token)
                : Invoke(codec, "ReadHeaderAsync", stop.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (writing) Assert.Throws<InvalidOperationException>(() => Complete(codec));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                if (writing) await Invoke(codec, "WriteHeaderAsync", 0L, 0L, CancellationToken.None);
                else await Header(codec);
            });
            stop.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            await Assert.ThrowsAsync<IOException>(async () =>
            {
                if (writing) await Invoke(codec, "WriteHeaderAsync", 0L, 0L, CancellationToken.None);
                else await Header(codec);
            });
        }

        [Test]
        public async Task CancellationBeforeIoDoesNotConsumeOrPoisonTheCarrier()
        {
            using var stream = new MemoryStream(new byte[] { 0, 0 });
            var codec = Codec(stream);
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await Invoke(codec, "ReadHeaderAsync", stop.Token));
            Assert.That(stream.Position, Is.Zero);
            var header = await Header(codec) ?? throw new AssertionException("Canceled call consumed the header.");
            Assert.That((Value(header, "Type"), Value(header, "Length")), Is.EqualTo((0L, 0L)));
        }

        private static void Complete(object codec)
        {
            try { (CodecType.GetMethod("CompleteOutput", Flags) ?? throw new AssertionException("Missing output completion.")).Invoke(codec, Array.Empty<object>()); }
            catch (TargetInvocationException error) when (error.InnerException is Exception cause)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw(); }
        }

        [TestCase(0)]
        [TestCase(3)]
        public async Task CompleteOutputIsIdempotentAndPreventsFurtherFrames(int length)
        {
            using var stream = new MemoryStream();
            var codec = Codec(stream);
            await Invoke(codec, "WriteHeaderAsync", 0L, (long)length, CancellationToken.None);
            await Invoke(codec, "WritePayloadAsync", new byte[length], 0, length, CancellationToken.None);
            var completedBytes = stream.ToArray();
            Complete(codec); Complete(codec);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Invoke(codec, "WriteHeaderAsync", 0L, 0L, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await Invoke(codec, "WritePayloadAsync", Array.Empty<byte>(), 0, 0, CancellationToken.None));
            Assert.That(stream.ToArray(), Is.EqualTo(completedBytes));
            Assert.That(stream.CanWrite, Is.True, "The caller still owns stream closure.");
        }

        [Test]
        public async Task IncompleteOutputCannotBeDeclaredCompleteOrResumedAfterCompletionFailure()
        {
            using var stream = new MemoryStream();
            var codec = Codec(stream);
            await Invoke(codec, "WriteHeaderAsync", 0L, 3L, CancellationToken.None);
            await Invoke(codec, "WritePayloadAsync", new byte[] { 1 }, 0, 1, CancellationToken.None);
            Assert.Throws<InvalidDataException>(() => Complete(codec));
            Assert.Throws<IOException>(() => Complete(codec));
            await Assert.ThrowsAsync<IOException>(async () => await Invoke(codec, "WritePayloadAsync", new byte[] { 2 }, 0, 1, CancellationToken.None));
            Assert.That(stream.ToArray(), Is.EqualTo(new byte[] { 0, 3, 1 }));
        }
        private abstract class InterruptibleCarrier : MemoryStream
        {
            protected InterruptibleCarrier() { }
            protected InterruptibleCarrier(byte[] bytes) : base(bytes) { }
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private sealed class InterruptedRead() : InterruptibleCarrier(new byte[] { 0xc0 })
        {
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Position == 0) return await base.ReadAsync(buffer, offset, count, token);
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertionException("Interrupted read unexpectedly resumed.");
            }
        }
        private sealed class InterruptedWrite : InterruptibleCarrier
        {
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                base.Write(buffer, offset, 1);
                Entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertionException("Interrupted write unexpectedly resumed.");
            }
        }
        private sealed class FragmentedInput(byte[] bytes, int fragment) : MemoryStream(bytes)
        {
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => base.ReadAsync(buffer, offset, Math.Min(count, fragment), token);
        }
        private sealed class PartialFailure : MemoryStream
        {
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                base.Write(buffer, offset, 1);
                return Task.FromException(new IOException("Injected partial capsule write."));
            }
        }
    }
}
