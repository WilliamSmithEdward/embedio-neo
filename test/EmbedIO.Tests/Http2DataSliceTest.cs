using System;
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
        private static object BorrowedData(byte flags, int id, byte[] bytes, int offset, int count)
            => FrameType.GetMethod("BorrowData", Flags | BindingFlags.Static)?.Invoke(null, new object[] { flags, id, bytes, offset, count })
                ?? throw new AssertionException("Missing borrowed DATA factory.");

        [TestCase(0, 0)]
        [TestCase(0, 1)]
        [TestCase(32767, 3)]
        [TestCase(60000, 0)]
        public async Task DataWritesOnlyTheBorrowedSliceAndLeavesItsOwnerUnchanged(int offset, int count)
        {
            var owner = Enumerable.Range(0, 60000).Select(i => (byte)(i * 17)).ToArray();
            var before = owner.ToArray();
            var frame = BorrowedData(1, 1, owner, offset, count);
            Assert.That(Property<byte[]>(frame, "Payload"), Is.SameAs(owner));
            using var wire = new MemoryStream();
            using (var writer = (IDisposable)Transport(wire)) await Write(writer, frame);
            Assert.That(wire.Length, Is.EqualTo(9 + count), "The parent buffer does not determine the wire length.");
            wire.Position = 0;
            using var reader = (IDisposable)Transport(wire);
            var received = await Read(reader) ?? throw new AssertionException("Missing DATA frame.");
            Assert.That(Property<byte[]>(received, "Payload"), Is.EqualTo(before.Skip(offset).Take(count)));
            Assert.That(Property<byte>(received, "Flags"), Is.EqualTo(1));
            Assert.That(owner, Is.EqualTo(before), "Transport scratch storage must not modify the borrowed owner.");
        }

        [Test]
        public async Task QueuedSliceCancellationReturnsOnlyItsReservedBytes()
        {
            using var wire = new GatedOutput();
            var failures = 0;
            using var connection = (IDisposable)Connection(wire, _ => Interlocked.Increment(ref failures));
            var first = SendRequest(connection, CancellationToken.None, Frame(6, 1, 0, new byte[8]));
            await wire.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var flow = ConnectionType.GetProperty("SendFlow", Flags)?.GetValue(connection)
                ?? throw new AssertionException("Missing flow control.");
            object? Flow(string name, params object[] args) => flow.GetType().GetMethod(name, Flags)?.Invoke(flow, args);
            Flow("Open", 1);
            Assert.That(await (Task<int>)(Flow("ReserveAsync", 1, 49151, CancellationToken.None)
                ?? throw new AssertionException("Missing first reservation.")), Is.EqualTo(49151));
            using var cancel = new CancellationTokenSource();
            var queued = SendRequest(connection, cancel.Token, BorrowedData(1, 3, new byte[1048576], 512, 16384));
            Flow("Open", 5);
            var sibling = (Task<int>)(Flow("ReserveAsync", 5, 1, CancellationToken.None)
                ?? throw new AssertionException("Missing sibling reservation."));
            try
            {
                Assert.That(sibling.IsCompleted, Is.False);
                Flow("Close", 3);
                cancel.Cancel();
                await Assert.ThatAsync(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2)),
                    Throws.InstanceOf<OperationCanceledException>());
                Assert.That(await sibling.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(1));
                Assert.That(failures, Is.Zero);
            }
            finally
            {
                wire.Release.TrySetResult(true);
                await first.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.That(wire.Length, Is.EqualTo(17), "Canceled borrowed bytes must never reach the wire.");
        }
    }
}
