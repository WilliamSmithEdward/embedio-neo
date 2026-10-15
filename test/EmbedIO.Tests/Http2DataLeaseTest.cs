using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2FrameTest
    {
        private static object PooledTransport(Stream stream, ArrayPool<byte> pool)
            => Activator.CreateInstance(TransportType, Flags, null, new object[] { stream, 16384, pool }, null)
                ?? throw new AssertionException("Missing pooled transport constructor.");
        private static byte[] DataWire(byte flags, int id, params byte[] bytes)
            => new byte[] { 0, 0, (byte)bytes.Length, 0, flags, 0, 0, 0, (byte)id }.Concat(bytes).ToArray();

        [TestCase(false)]
        [TestCase(true)]
        public async Task DataLeaseReturnsItsClearedOwnerExactlyOnce(bool concurrent)
        {
            var pool = new ObservedDataPool();
            using var source = new MemoryStream(DataWire(1, 1, 10, 20, 30));
            using var transport = (IDisposable)PooledTransport(source, pool);
            var frame = await Read(transport) ?? throw new AssertionException("Missing DATA lease.");
            Assert.That(Property<int>(frame, "PayloadLength"), Is.EqualTo(3));
            Assert.That(Property<byte[]>(frame, "Payload").Length, Is.GreaterThan(3));
            Assert.That(pool.Active, Is.EqualTo(1));
            Validate(frame);
            if (concurrent) Parallel.Invoke(((IDisposable)frame).Dispose, ((IDisposable)frame).Dispose);
            else { ((IDisposable)frame).Dispose(); ((IDisposable)frame).Dispose(); }
            Assert.That(pool.Returned, Is.EqualTo(1));
            Assert.That(pool.Active, Is.Zero);
            var error = Assert.Throws<TargetInvocationException>(() => Property<byte[]>(frame, "Payload"));
            Assert.That(error?.InnerException, Is.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public async Task QueuedRequestBodyRetainsBytesAfterTheFrameLeaseReturns()
        {
            var pool = new ObservedDataPool();
            using var source = new MemoryStream(DataWire(1, 1, 10, 20, 30));
            using var transport = (IDisposable)PooledTransport(source, pool);
            var frame = await Read(transport) ?? throw new AssertionException("Missing DATA lease.");
            var bodyType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.Http2RequestBody", true)
                ?? throw new AssertionException("Missing request body.");
            var consumed = 0;
            using var body = (Stream)(Activator.CreateInstance(bodyType, Flags, null,
                new object?[] { 1, null, new Action<int>(count => consumed += count) }, null)
                ?? throw new AssertionException("Missing body constructor."));
            (bodyType.GetMethod("Append", Flags) ?? throw new AssertionException("Missing body append method.")).Invoke(body,
                new object[] { Property<byte[]>(frame, "Payload"), 0, Property<int>(frame, "PayloadLength"), true });
            ((IDisposable)frame).Dispose();
            Assert.That(pool.Active, Is.Zero);
            var result = new byte[3];
            await body.ReadExactlyAsync(result);
            Assert.That(result, Is.EqualTo(new byte[] { 10, 20, 30 }));
            Assert.That(consumed, Is.EqualTo(3));
        }

        [Test]
        public async Task TruncatedDataReadReturnsItsRental()
        {
            var pool = new ObservedDataPool();
            var truncated = DataWire(0, 1, 10, 20, 30); truncated[2] = 4;
            using var source = new MemoryStream(truncated);
            using var transport = (IDisposable)PooledTransport(source, pool);
            await Assert.ThatAsync(async () => await Read(transport), Throws.TypeOf<EndOfStreamException>());
            Assert.That(pool.Rented, Is.EqualTo(1));
            Assert.That(pool.Returned, Is.EqualTo(1));
            Assert.That(pool.Active, Is.Zero);
        }

        [Test]
        public async Task CanceledDataReadReturnsItsRental()
        {
            var pool = new ObservedDataPool();
            using var source = new WaitingDataSource(DataWire(0, 1, 10, 20, 30));
            using var transport = (IDisposable)PooledTransport(source, pool);
            using var cancel = new CancellationTokenSource();
            var reading = Read(transport, cancel.Token);
            await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(pool.Active, Is.EqualTo(1));
            cancel.Cancel();
            await Assert.ThatAsync(async () => await reading.WaitAsync(TimeSpan.FromSeconds(2)), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(pool.Returned, Is.EqualTo(1));
            Assert.That(pool.Active, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task InvalidDataShapeReturnsTheConnectionReadersLease(bool padded)
        {
            var pool = new ObservedDataPool();
            using var source = new MemoryStream(DataWire(padded ? (byte)8 : (byte)0, padded ? 1 : 0, 3, 20, 30));
            using var connection = (IDisposable)(Activator.CreateInstance(ConnectionType, Flags, null,
                new object[] { source, pool }, null) ?? throw new AssertionException("Missing pooled connection."));
            var reading = (Task)(ConnectionType.GetMethod("ReadFrameAsync", Flags)?.Invoke(connection,
                new object[] { CancellationToken.None }) ?? throw new AssertionException("Missing frame reader."));
            await Assert.ThatAsync(async () => await reading, Throws.InstanceOf<IOException>());
            Assert.That(pool.Rented, Is.EqualTo(1));
            Assert.That(pool.Returned, Is.EqualTo(1));
            Assert.That(pool.Active, Is.Zero);
        }

        [Test]
        public async Task ControlFramesKeepIndependentPayloads()
        {
            var pool = new ObservedDataPool();
            using var source = new MemoryStream(new byte[] { 0, 0, 8, 6, 0, 0, 0, 0, 0 }.Concat(new byte[8]).ToArray());
            using var transport = (IDisposable)PooledTransport(source, pool);
            var frame = await Read(transport) ?? throw new AssertionException("Missing PING.");
            ((IDisposable)frame).Dispose();
            Assert.That(Property<byte[]>(frame, "Payload"), Has.Length.EqualTo(8));
            Assert.That(pool.Rented, Is.Zero);
        }

        private sealed class ObservedDataPool : ArrayPool<byte>
        {
            private readonly object _sync = new();
            private readonly HashSet<byte[]> _active = new();
            internal int Rented { get; private set; }
            internal int Returned { get; private set; }
            internal int Active { get { lock (_sync) return _active.Count; } }
            public override byte[] Rent(int minimumLength)
            {
                lock (_sync)
                {
                    var result = new byte[Math.Max(256, minimumLength)];
                    Array.Fill(result, (byte)0xdd);
                    _active.Add(result); Rented++;
                    return result;
                }
            }
            public override void Return(byte[] array, bool clearArray = false)
            {
                lock (_sync)
                {
                    Assert.That(_active.Remove(array), Is.True, "Each rental must return exactly once.");
                    Assert.That(clearArray, Is.True, "Request bytes must not remain in recycled storage.");
                    Array.Clear(array); Returned++;
                }
            }
        }
        private sealed class WaitingDataSource(byte[] wire) : MemoryStream(wire)
        {
            internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            {
                if (Position >= 9)
                {
                    Started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return await base.ReadAsync(buffer, offset, count, token);
            }
        }
    }
}
