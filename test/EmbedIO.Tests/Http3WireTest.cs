using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3WireTest
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type InternalType(string name) => typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3." + name, true)
            ?? throw new AssertionException("Missing HTTP/3 type.");
        private delegate long ReadInteger(byte[] bytes, ref int offset, int end);
        private static readonly ReadInteger Decode = (InternalType("QuicInteger").GetMethod("Read", Hidden)
            ?? throw new AssertionException("Missing decoder.")).CreateDelegate<ReadInteger>();
        private static readonly Func<byte[], int, long, int> Encode = (InternalType("QuicInteger").GetMethod("Write", Hidden)
            ?? throw new AssertionException("Missing encoder.")).CreateDelegate<Func<byte[], int, long, int>>();

        [TestCase("00", 0L)]
        [TestCase("19", 25L)]
        [TestCase("3f", 63L)]
        [TestCase("4040", 64L)]
        [TestCase("7bbd", 15293L)]
        [TestCase("7fff", 16383L)]
        [TestCase("80004000", 16384L)]
        [TestCase("9d7f3e7d", 494878333L)]
        [TestCase("bfffffff", 1073741823L)]
        [TestCase("c000000040000000", 1073741824L)]
        [TestCase("c2197c5eff14e88c", 151288809941952652L)]
        [TestCase("ffffffffffffffff", 4611686018427387903L)]
        public void IntegerBoundariesAndPublishedExamples(string hex, long expected)
        {
            var input = Convert.FromHexString(hex);
            var offset = 0;
            Assert.That(Decode(input, ref offset, input.Length), Is.EqualTo(expected));
            Assert.That(offset, Is.EqualTo(input.Length));
            var output = new byte[10];
            var written = Encode(output, 1, expected);
            Assert.That(Convert.ToHexStringLower(output, 1, written), Is.EqualTo(hex));
            Assert.That(output[0], Is.Zero);
            Assert.That(output[written + 1], Is.Zero);
        }

        [TestCase("4019")]
        [TestCase("80000019")]
        [TestCase("c000000000000019")]
        public void LongerIntegerEncodingsAreValid(string hex)
        {
            var input = Convert.FromHexString(hex);
            var offset = 0;
            Assert.That(Decode(input, ref offset, input.Length), Is.EqualTo(25));
            Assert.That(offset, Is.EqualTo(input.Length));
        }

        [TestCase("")]
        [TestCase("40")]
        [TestCase("80ffff")]
        [TestCase("c0ffffffffffff")]
        public void TruncatedIntegerDoesNotAdvanceOffset(string hex)
        {
            var input = Convert.FromHexString(hex);
            var offset = 0;
            Assert.Throws<EndOfStreamException>(() => Decode(input, ref offset, input.Length));
            Assert.That(offset, Is.Zero);
        }

        [TestCase(-1L)]
        [TestCase(4611686018427387904L)]
        [TestCase(long.MaxValue)]
        public void OutOfRangeIntegerCannotBeEncoded(long value)
            => Assert.Throws<ArgumentOutOfRangeException>(() => Encode(new byte[8], 0, value));

        private sealed class Reader
        {
            private readonly object _instance;
            internal Reader(Stream stream) => _instance = Activator.CreateInstance(InternalType("Http3FrameReader"), Hidden, null, new object[] { stream }, null)
                ?? throw new AssertionException("Missing frame reader.");
            private object Call(string method, params object[] args)
            {
                try
                {
                    return (_instance.GetType().GetMethod(method, Hidden) ?? throw new AssertionException("Missing reader method.")).Invoke(_instance, args)
                    ?? throw new AssertionException("Missing async result.");
                }
                catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
            }
            internal async Task<object?> Header(CancellationToken token = default)
            {
                var task = AsTask(Call("ReadHeaderAsync", token));
                await task;
                return (task.GetType().GetProperty("Result") ?? throw new AssertionException("Missing header result.")).GetValue(task);
            }
            internal Task<byte[]> Buffer(int maximum, CancellationToken token = default) => (Task<byte[]>)AsTask(Call("ReadBufferedPayloadAsync", maximum, token));
            internal Task Skip(CancellationToken token = default) => AsTask(Call("SkipPayloadAsync", token));
            internal Task<int> Read(byte[] bytes, int offset, int count, CancellationToken token = default) => (Task<int>)AsTask(Call("ReadPayloadAsync", bytes, offset, count, token));
        }
        // Reader operations return Task or ValueTask; tests observe them as tasks.
        private static Task AsTask(object? pending) => pending as Task
            ?? (Task)((pending ?? throw new AssertionException("Missing async result.")).GetType().GetMethod("AsTask")?.Invoke(pending, null)
                ?? throw new AssertionException("Missing async result."));
        // The frame reader reads through the memory overload on modern targets;
        // these fixtures keep their array-based fragmentation and pending behavior.
        private static ValueTask<int> ArrayRead(Memory<byte> buffer, CancellationToken token, Func<byte[], int, int, CancellationToken, Task<int>> read)
            => MemoryMarshal.TryGetArray<byte>(buffer, out var segment) && segment.Array != null
                ? new ValueTask<int>(read(segment.Array, segment.Offset, segment.Count, token))
                : throw new NotSupportedException("Fixture reads require array-backed memory.");
        private static long Property(object? value, string name)
        {
            Assert.That(value, Is.Not.Null);
            var instance = value ?? throw new AssertionException("Missing value.");
            return (long)((instance.GetType().GetProperty(name) ?? throw new AssertionException("Missing property.")).GetValue(instance)
                ?? throw new AssertionException("Missing property value."));
        }
        private sealed class FragmentedStream(byte[] bytes, int fragment) : MemoryStream(bytes)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ArrayRead(buffer, cancellationToken, ReadAsync);
            public int MaximumRead { get; private set; }
            public int Reads { get; private set; }
            public override int Read(byte[] buffer, int offset, int count) => throw new AssertionException("Synchronous read.");
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MaximumRead = Math.Max(MaximumRead, count);
                Reads++;
                return Task.FromResult(base.Read(buffer, offset, Math.Min(fragment, count)));
            }
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(4096)]
        public async Task StreamingPayloadPreservesNextFrameAndAcceptsNonminimalHeaders(int fragment)
        {
            using var source = new FragmentedStream(Convert.FromHexString("400080000003aabbcc0100"), fragment);
            var reader = new Reader(source);
            var header = await reader.Header();
            Assert.That(Property(header, "Type"), Is.Zero);
            Assert.That(Property(header, "Length"), Is.EqualTo(3));
            await Assert.ThatAsync(async () => await reader.Header(), Throws.TypeOf<InvalidOperationException>());
            var bytes = new byte[3];
            var offset = 0;
            while (offset < bytes.Length) offset += await reader.Read(bytes, offset, bytes.Length - offset);
            Assert.That(Convert.ToHexStringLower(bytes), Is.EqualTo("aabbcc"));
            Assert.That(await reader.Read(bytes, 0, 3), Is.Zero);
            Assert.That(Property(await reader.Header(), "Type"), Is.EqualTo(1));
            Assert.That(await reader.Buffer(0), Is.Empty);
            Assert.That(await reader.Header(), Is.Null);
            Assert.That(await reader.Header(), Is.Null);
        }

        // A frame header is read with as few transport reads as its encoding
        // allows, and no read ever asks for a byte beyond the header.
        [TestCase("0003aabbcc", 0L, 3L, 2, 1)]
        [TestCase("01404000", 1L, 64L, 3, 2)]
        [TestCase("402105aabbccddee", 33L, 5L, 3, 2)]
        [TestCase("2180000005aabbccddee", 33L, 5L, 5, 2)]
        public async Task FrameHeaderReadsStopAtTheHeader(string wire, long type, long length, int headerBytes, int reads)
        {
            using var source = new FragmentedStream(Convert.FromHexString(wire), 4096);
            var reader = new Reader(source);
            var header = await reader.Header();
            Assert.That(Property(header, "Type"), Is.EqualTo(type));
            Assert.That(Property(header, "Length"), Is.EqualTo(length));
            Assert.That(source.Position, Is.EqualTo(headerBytes), "Payload bytes stay in the transport.");
            Assert.That(source.Reads, Is.EqualTo(reads));
        }

        [TestCase("40")]
        [TestCase("00")]
        [TestCase("0040")]
        [TestCase("0002ff")]
        public async Task CleanFinInsideFrameIsAConnectionFrameError(string wire)
        {
            using var source = new FragmentedStream(Convert.FromHexString(wire), 1);
            var reader = new Reader(source);
            var error = await Assert.CatchAsync<IOException>(async () => { await reader.Header(); await reader.Skip(); });
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(0x106));
            await Assert.ThatAsync(async () => await reader.Header(), Throws.TypeOf<IOException>());
        }

        [Test]
        public async Task HugeMetadataRejectedBeforePayloadReadOrAllocation()
        {
            using var source = new FragmentedStream(Convert.FromHexString("01ffffffffffffffff"), 8);
            var reader = new Reader(source);
            Assert.That(Property(await reader.Header(), "Length"), Is.EqualTo(4611686018427387903L));
            var error = await Assert.CatchAsync<IOException>(async () => await reader.Buffer(65536));
            Assert.That(Property(error, "ErrorCode"), Is.EqualTo(0x107));
            Assert.That(source.Position, Is.EqualTo(9));
        }

        [Test]
        public async Task UnknownFrameCanBeSkippedWithConstantStorage()
        {
            var wire = new byte[20008];
            var offset = Encode(wire, 0, 0x21);
            offset += Encode(wire, offset, 20000);
            offset += 20000;
            wire[offset++] = 1; wire[offset++] = 0;
            using var source = new FragmentedStream(wire, 7000);
            source.SetLength(offset);
            var reader = new Reader(source);
            Assert.That(Property(await reader.Header(), "Type"), Is.EqualTo(0x21));
            await reader.Skip();
            Assert.That(source.MaximumRead, Is.LessThanOrEqualTo(4096));
            Assert.That(Property(await reader.Header(), "Type"), Is.EqualTo(1));
        }

        private sealed class PendingStream : MemoryStream
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ArrayRead(buffer, cancellationToken, ReadAsync);
            internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }

        [Test]
        public async Task ConcurrentReaderRejectedAndCanceledReadPoisonsInput()
        {
            using var source = new PendingStream();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var reader = new Reader(source);
            var pending = reader.Header(stop.Token);
            await source.Entered.Task.WaitAsync(stop.Token);
            await Assert.ThatAsync(async () => await reader.Header(), Throws.TypeOf<InvalidOperationException>());
            stop.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            await Assert.ThatAsync(async () => await reader.Header(), Throws.TypeOf<IOException>());
            Assert.That(source.CanRead, Is.True, "Frame reader must not dispose the QUIC stream.");
        }

        [Test]
        public async Task PrecanceledReadDoesNotConsumeOrPoisonInput()
        {
            using var source = new FragmentedStream(new byte[] { 1, 0 }, 1);
            var reader = new Reader(source);
            await Assert.ThatAsync(async () => await reader.Header(new CancellationToken(true)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(source.Position, Is.Zero);
            Assert.That(Property(await reader.Header(), "Type"), Is.EqualTo(1));
        }
    }
}
