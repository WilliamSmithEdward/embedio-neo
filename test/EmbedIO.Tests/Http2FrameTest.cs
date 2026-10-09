using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type FrameType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2Frame", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static readonly Type TransportType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2FrameTransport", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static object Frame(byte type, byte flags, int id, byte[] bytes) => (Activator.CreateInstance(FrameType, Flags, null, new object[] { type, flags, id, bytes }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static object Transport(Stream stream) => (Activator.CreateInstance(TransportType, Flags, null, new object[] { stream, 16384 }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static T Property<T>(object value, string name) => (T)((value.GetType().GetProperty(name) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(value) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        private static async Task<object?> Read(object transport, CancellationToken token = default)
        {
            var task = (Task)((TransportType.GetMethod("ReadAsync", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(transport, new object[] { token }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            await task;
            return (task.GetType().GetProperty("Result") ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).GetValue(task);
        }
        private static Task Write(object transport, params object[] frames)
        {
            var array = Array.CreateInstance(FrameType, frames.Length);
            for (var i = 0; i < frames.Length; i++) array.SetValue(frames[i], i);
            return (Task)((TransportType.GetMethod("WriteAsync", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(transport, new object[] { array, 16384, CancellationToken.None }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
        }
        private static void Validate(object frame)
        {
            try { (FrameType.GetMethod("ValidateShape", Flags) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value.")).Invoke(frame, null); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture((error.InnerException ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."))).Throw(); }
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(7)]
        [TestCase(1024)]
        public async Task FragmentedFramesPreserveFlagsReservedBitAndSuccessor(int fragment)
        {
            var wire = Convert.FromHexString("00000300A180000001616263000000F5FF00000000");
            using var source = new AsyncSource(wire, fragment);
            var transport = Transport(source);
            var first = ((await Read(transport)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<int>(first, "StreamId"), Is.EqualTo(1));
            Assert.That(Property<byte>(first, "Flags"), Is.EqualTo(0xa1));
            Assert.That(Property<byte[]>(first, "Payload"), Is.EqualTo(new byte[] { 97, 98, 99 }));
            Validate(first);
            var second = ((await Read(transport)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<byte>(second, "Type"), Is.EqualTo(0xf5));
            Validate(second);
            Assert.That(await Read(transport), Is.Null);
        }

        [TestCase(1)]
        [TestCase(8)]
        [TestCase(9)]
        [TestCase(10)]
        [TestCase(16)]
        public async Task TruncatedFramePoisonsReader(int count)
        {
            var wire = Convert.FromHexString("0000080600000000000102030405060708").Take(count).ToArray();
            using var source = new AsyncSource(wire, 2);
            var transport = Transport(source);
            await Assert.ThatAsync(async () => await Read(transport), Throws.TypeOf<EndOfStreamException>());
            await Assert.ThatAsync(async () => await Read(transport), Throws.TypeOf<IOException>());
        }

        [Test]
        public async Task ExcessiveLengthIsRejectedBeforeReadingAnyPayload()
        {
            using var source = new AsyncSource(Convert.FromHexString("00400100000000000155"), 1024);
            var error = await Assert.CatchAsync<IOException>(async () => await Read(Transport(source)));
            Assert.That(Property<uint>(error ?? throw new AssertionException("Expected oversized-frame error."), "ErrorCode"), Is.EqualTo(6));
            Assert.That(source.Position, Is.EqualTo(9));
            await Task.CompletedTask;
        }

        [Test]
        public async Task PrecancellationDoesNotConsumeInputOrPoisonReader()
        {
            using var source = new AsyncSource(Convert.FromHexString("000000040000000000"), 1);
            var transport = Transport(source);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThatAsync(async () => await Read(transport, canceled.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(source.Position, Is.Zero);
            Assert.That(await Read(transport), Is.Not.Null);
        }

        [Test]
        public async Task ConcurrentReadIsRejectedWithoutCorruptingTheOwner()
        {
            using var source = new AsyncSource(Convert.FromHexString("000000040000000000"), 3) { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var transport = Transport(source);
            var first = Read(transport);
            await Assert.ThatAsync(async () => await Read(transport), Throws.TypeOf<InvalidOperationException>());
            source.Gate.SetResult(true);
            Assert.That(await first, Is.Not.Null);
        }

        [Test]
        public async Task InflightReadCancellationMakesPartialStateTerminal()
        {
            using var source = new AsyncSource(new byte[9], 1) { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var transport = Transport(source);
            using var stop = new CancellationTokenSource();
            var pending = Read(transport, stop.Token);
            stop.Cancel();
            await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
            source.Gate.SetResult(true);
            await Assert.ThatAsync(async () => await Read(transport), Throws.TypeOf<IOException>());
        }

        [TestCase(0, 0, 0, 0, 1, 0)]
        [TestCase(1, 0, 0, 0, 1, 0)]
        [TestCase(9, 0, 0, 0, 1, 0)]
        [TestCase(2, 0, 1, 4, 6, 1)]
        [TestCase(3, 0, 1, 3, 6, 0)]
        [TestCase(4, 1, 0, 6, 6, 0)]
        [TestCase(4, 0, 1, 0, 1, 0)]
        [TestCase(4, 0, 0, 5, 6, 0)]
        [TestCase(6, 0, 0, 7, 6, 0)]
        [TestCase(7, 0, 0, 7, 6, 0)]
        [TestCase(8, 0, 1, 3, 6, 0)]
        [TestCase(0, 8, 1, 0, 6, 0)]
        [TestCase(1, 32, 1, 4, 6, 0)]
        [TestCase(5, 0, 1, 3, 6, 0)]
        public void ShapeErrorsRetainProtocolCodeAndScope(byte type, byte flags, int id, int length, int code, int scope)
        {
            var error = (Assert.Catch<IOException>(() => Validate(Frame(type, flags, id, new byte[length]))) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(error ?? throw new AssertionException("Expected oversized-frame error."), "ErrorCode"), Is.EqualTo(code));
            Assert.That(Property<int>(error, "StreamId"), Is.EqualTo(scope));
        }

        [Test]
        public void PaddingCannotConsumeRequiredFields()
        {
            var error = (Assert.Catch<IOException>(() => Validate(Frame(1, 40, 1, new byte[] { 1, 0, 0, 0, 0, 0 }))) ?? throw new NUnit.Framework.AssertionException("Expected a non-null fixture value."));
            Assert.That(Property<uint>(error ?? throw new AssertionException("Expected oversized-frame error."), "ErrorCode"), Is.EqualTo(1));
            Validate(Frame(0, 8, 1, new byte[] { 1, 0 }));
        }

        [Test]
        public async Task ConcurrentWriteBatchesRemainContiguousAndUseAsyncIo()
        {
            using var sink = new AsyncSink();
            var writer = Transport(sink);
            await Task.WhenAll(Write(writer, Frame(1, 0, 1, new byte[] { 0x82 }), Frame(9, 4, 1, Array.Empty<byte>())),
                Write(writer, Frame(1, 0, 3, new byte[] { 0x82 }), Frame(9, 4, 3, Array.Empty<byte>())));
            using var source = new AsyncSource(sink.Bytes, 1);
            var reader = Transport(source);
            var ids = new List<int>();
            object? frame;
            while ((frame = await Read(reader)) != null) ids.Add(Property<int>(frame, "StreamId"));
            Assert.That(ids, Is.EqualTo(new[] { 1, 1, 3, 3 }).Or.EqualTo(new[] { 3, 3, 1, 1 }));
        }

        [Test]
        public async Task InvalidOutboundFrameDoesNotWriteAndIoFailureIsTerminal()
        {
            using var sink = new AsyncSink();
            var transport = Transport(sink);
            await Assert.ThatAsync(async () => await Write(transport, Frame(0, 0, 1, new byte[16385])), Throws.TypeOf<ArgumentException>());
            Assert.That(sink.Bytes, Is.Empty);
            sink.Fail = true;
            await Assert.ThatAsync(async () => await Write(transport, Frame(4, 0, 0, Array.Empty<byte>())), Throws.TypeOf<IOException>());
            sink.Fail = false;
            await Assert.ThatAsync(async () => await Write(transport, Frame(4, 0, 0, Array.Empty<byte>())), Throws.TypeOf<IOException>());
            Assert.That(sink.Bytes, Is.Empty);
        }

        private sealed class AsyncSource(byte[] bytes, int fragment) : Stream
        {
            private int _position;
            internal TaskCompletionSource<bool>? Gate;
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (Gate != null) await Gate.Task.WaitAsync(token);
                var size = Math.Min(Math.Min(count, fragment), bytes.Length - _position);
                Buffer.BlockCopy(bytes, _position, buffer, offset, size);
                _position += size;
                return size;
            }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => bytes.Length;
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O.");
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private sealed class AsyncSink : Stream
        {
            private readonly MemoryStream _output = new();
            internal bool Fail;
            internal byte[] Bytes => _output.ToArray();
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                await Task.Yield();
                token.ThrowIfCancellationRequested();
                if (Fail) throw new IOException("Injected transport fault.");
                _output.Write(buffer, offset, count);
            }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _output.Length;
            public override long Position { get => _output.Position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous I/O.");
            protected override void Dispose(bool disposing) { if (disposing) _output.Dispose(); base.Dispose(disposing); }
        }
    }
}
