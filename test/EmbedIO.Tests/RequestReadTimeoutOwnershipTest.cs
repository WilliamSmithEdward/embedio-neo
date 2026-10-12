using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class RequestReadTimeoutOwnershipTest
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task AnExpiredReadCannotRetainOrModifyReturnedCallerStorage(int mode)
        {
            using var source = new HeldRead();
            using var reader = CreateReader(source, TimeSpan.FromMilliseconds(40));
            var caller = new byte[8];
            Task<int> reading = mode switch
            {
                0 => Task.Run(() => reader.Read(caller, 2, 4)),
                1 => reader.ReadAsync(caller, 2, 4),
                2 => reader.ReadAsync(caller.AsMemory(2, 4)).AsTask(),
                _ => Task.Run(() => reader.Read(caller.AsSpan(2, 4)))
            };
            try
            {
                await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var failure = await Assert.ThrowsAsync<HttpException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(failure?.StatusCode, Is.EqualTo(408));
                Assert.That(source.Pending.IsCompleted, Is.False, "The controlled source has not finished its owned write.");
                if (mode == 3)
                    Assert.That(source.Storage.ToArray(), Is.All.EqualTo((byte)0x55),
                        "An outstanding source buffer must not be cleared or returned to a pool before settlement.");
                Array.Fill(caller, (byte)0x22);
                source.Complete();
                await source.Pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(caller, Is.All.EqualTo((byte)0x22),
                    "The application owns its buffer again after the read reports timeout.");
            }
            finally
            {
                source.Complete();
                await source.Pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CallerCancellationCannotLeaveItsBufferOwnedByTheSource(bool memory)
        {
            using var source = new HeldRead();
            using var reader = CreateReader(source, TimeSpan.FromSeconds(5));
            using var canceled = new CancellationTokenSource();
            var caller = new byte[8];
            var reading = memory ? reader.ReadAsync(caller.AsMemory(2, 4), canceled.Token).AsTask()
                : reader.ReadAsync(caller, 2, 4, canceled.Token);
            try
            {
                await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                canceled.Cancel();
                var failure = await Assert.ThrowsAsync<OperationCanceledException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(failure?.CancellationToken, Is.EqualTo(canceled.Token));
                Assert.That(source.Pending.IsCompleted, Is.False);
                Array.Fill(caller, (byte)0x22);
                source.Complete();
                await source.Pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(caller, Is.All.EqualTo((byte)0x22));
            }
            finally
            {
                source.Complete();
                await source.Pending.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task SuccessfulReadsRespectShortReadsAndCallerOffsets(int mode)
        {
            using var source = new MemoryStream(new byte[] { 7, 8 });
            using var reader = CreateReader(source, TimeSpan.FromSeconds(5));
            var caller = new byte[] { 9, 9, 9, 9, 9, 9 };
            var received = mode switch
            {
                0 => reader.Read(caller, 1, 4),
                1 => await reader.ReadAsync(caller, 1, 4),
                2 => await reader.ReadAsync(caller.AsMemory(1, 4)),
                _ => reader.Read(caller.AsSpan(1, 4))
            };
            Assert.That(received, Is.EqualTo(2));
            Assert.That(caller, Is.EqualTo(new byte[] { 9, 7, 8, 9, 9, 9 }));
            Assert.That(await reader.ReadAsync(caller, 0, 0), Is.Zero);
            Assert.That(await reader.ReadAsync(caller, 0, caller.Length), Is.Zero);
        }

        [Test]
        public async Task RetirementWakesPendingReadWithoutReturningItsSourceStorage()
        {
            using var source = new HeldRead();
            using var reader = CreateReader(source, TimeSpan.FromSeconds(30));
            var caller = new byte[8];
            var reading = reader.ReadAsync(caller, 2, 4);
            try
            {
                await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var retire = reader.GetType().GetMethod("Retire", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing request retirement contract.");
                retire.Invoke(reader, null);
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.That(source.Pending.IsCompleted, Is.False);
                Assert.That(source.Storage.ToArray(), Is.All.EqualTo((byte)0x55));
                Array.Fill(caller, (byte)0x22);
                source.Complete();
                await source.Pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(caller, Is.All.EqualTo((byte)0x22));
                Assert.That(reader.CanRead, Is.False);
            }
            finally { source.Complete(); await source.Pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Test]
        public async Task RepeatedTimeoutReportsTheOwnedFailureAndRunsItsCallbackOnce()
        {
            using var source = new HeldRead();
            var callbacks = 0;
            using var reader = CreateReader(source, TimeSpan.FromMilliseconds(40), () => Interlocked.Increment(ref callbacks));
            try
            {
                var first = await Assert.ThrowsAsync<HttpException>(async () => _ = await reader.ReadAsync(new byte[4], 0, 4));
                var second = await Assert.ThrowsAsync<HttpException>(async () => _ = await reader.ReadAsync(new byte[4], 0, 4));
                Assert.That(second, Is.SameAs(first));
                Assert.That(callbacks, Is.EqualTo(1));
                var predicate = reader.GetType().GetMethod("IsTimeoutFailure", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing owned timeout failure predicate.");
                Assert.That(predicate.Invoke(reader, new object?[] { first }), Is.True);
                Assert.That(predicate.Invoke(reader, new object[] { new HttpException(408, "Application timeout") }), Is.False);
            }
            finally { source.Complete(); await source.Pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Test]
        public async Task CancellationDoesNotAllowAnotherReadWhileTheSourceStillOwnsStorage()
        {
            using var source = new HeldRead();
            using var reader = CreateReader(source, TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var reading = reader.ReadAsync(new byte[4], 0, 4, cancellation.Token);
            try
            {
                await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () => await reading.WaitAsync(TimeSpan.FromSeconds(5)));
                await Assert.ThrowsAsync<InvalidOperationException>(async () => _ = await reader.ReadAsync(new byte[4], 0, 4));
                Assert.That(source.ReadCalls, Is.EqualTo(1));
                Assert.That(source.Storage.ToArray(), Is.All.EqualTo((byte)0x55));
            }
            finally { source.Complete(); await source.Pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static Stream CreateReader(Stream source, TimeSpan timeout, Action? callback = null)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.ReadTimeoutStream", true)
                ?? throw new AssertionException("Missing body read deadline implementation.");
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object?[] { source, timeout, callback }, null) as Stream
                ?? throw new AssertionException("Missing body read deadline constructor.");
        }

        private sealed class HeldRead : Stream
        {
            private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task<int> Pending => _pending.Task;
            internal Memory<byte> Storage { get; private set; }
            internal int ReadCalls { get; private set; }

            internal void Complete()
            {
                if (_pending.Task.IsCompleted) return;
                Storage.Span.Fill(0x33);
                _pending.TrySetResult(Storage.Length);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ++ReadCalls;
                Storage = buffer.AsMemory(offset, count);
                Storage.Span.Fill(0x55);
                Started.TrySetResult(true);
                return _pending.Task;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                ++ReadCalls;
                Storage = buffer;
                Storage.Span.Fill(0x55);
                Started.TrySetResult(true);
                return new ValueTask<int>(_pending.Task);
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
