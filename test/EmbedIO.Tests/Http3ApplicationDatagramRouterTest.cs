using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicNativeDatagramTest
    {
        [TestCase(0L, "00")]
        [TestCase(252L, "3F")]
        [TestCase(256L, "4040")]
        [TestCase(65536L, "80004000")]
        [TestCase((1L << 62) - 4, "CFFFFFFFFFFFFFFF")]
        public async Task ApplicationDatagramsStripOnlyTheirOwnStreamIdAndRetainStorage(long id, string prefix)
        {
            using var native = Create(new FakeNative());
            using var router = ApplicationRouter(native);
            using var channel = (IDisposable)Call(router, "Open", id, (Func<bool>)(() => true), (Func<bool>)(() => true));
            Call(router, "SetPeerEnabled", true);
            var bytes = Convert.FromHexString(prefix + "010203");
            var owner = new PacketOwner();
            Call(router, "Deliver", ApplicationPacket(bytes, owner));
            using var received = await ApplicationReceive(channel);
            Assert.That(received, Is.Not.Null);
            var payload = Get<ReadOnlyMemory<byte>>(received ?? throw new AssertionException("Missing packet."), "Payload");
            Assert.That(payload.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(MemoryMarshal.TryGetArray(payload, out var segment), Is.True);
            Assert.That(segment.Array, Is.SameAs(bytes), "Routing must retain the receive buffer rather than copy it.");
            Assert.That(owner.Disposals, Is.Zero);
            received.Dispose();
            received.Dispose();
            Assert.That(owner.Disposals, Is.EqualTo(1));
        }
        [TestCase("unknown")]
        [TestCase("receive-closed")]
        [TestCase("packet-budget")]
        [TestCase("byte-budget")]
        [TestCase("not-negotiated")]
        public void DiscardedApplicationDatagramsReleaseTheirStorage(string reason)
        {
            using var native = Create(new FakeNative());
            using var router = ApplicationRouter(native, 1, 2);
            using var channel = (IDisposable)Call(router, "Open", 0L, (Func<bool>)(() => reason != "receive-closed"), (Func<bool>)(() => true));
            Call(router, "SetPeerEnabled", reason != "not-negotiated");
            var first = new PacketOwner();
            if (reason == "packet-budget") Call(router, "Deliver", ApplicationPacket(new byte[] { 0, 1 }, first));
            var rejected = new PacketOwner();
            var bytes = reason == "unknown" ? new byte[] { 1, 1 }
                : reason == "byte-budget" ? new byte[] { 0, 1, 2, 3 } : new byte[] { 0, 1 };
            Call(router, "Deliver", ApplicationPacket(bytes, rejected));
            Assert.That(rejected.Disposals, Is.EqualTo(1));
            channel.Dispose();
            if (reason == "packet-budget") Assert.That(first.Disposals, Is.EqualTo(1));
        }
        [TestCase("")]
        [TestCase("C0")]
        [TestCase("D000000000000000")]
        public void MalformedApplicationDatagramIsAConnectionErrorAndReleasesStorage(string wire)
        {
            using var native = Create(new FakeNative());
            using var router = ApplicationRouter(native);
            var owner = new PacketOwner();
            var error = Assert.Catch<Exception>(() => Call(router, "Deliver", ApplicationPacket(Convert.FromHexString(wire), owner)));
            Assert.That(error?.GetType().Name, Is.EqualTo("Http3ProtocolException"));
            Assert.That(error?.GetType().GetProperty("ErrorCode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(error), Is.EqualTo(0x33));
            Assert.That(owner.Disposals, Is.EqualTo(1));
        }
        [Test]
        public async Task CanceledApplicationReceiveDoesNotLoseTheNextDatagram()
        {
            using var native = Create(new FakeNative());
            using var router = ApplicationRouter(native);
            using var channel = (IDisposable)Call(router, "Open", 0L, (Func<bool>)(() => true), (Func<bool>)(() => true));
            Call(router, "SetPeerEnabled", true);
            using var stop = new CancellationTokenSource();
            var canceled = ApplicationReceive(channel, stop.Token);
            stop.Cancel();
            await Assert.ThatAsync(async () => await canceled, Throws.InstanceOf<OperationCanceledException>());
            var owner = new PacketOwner();
            Call(router, "Deliver", ApplicationPacket(new byte[] { 0, 7 }, owner));
            using var packet = await ApplicationReceive(channel);
            Assert.That(Get<ReadOnlyMemory<byte>>(packet ?? throw new AssertionException("Missing retained packet."), "Payload").ToArray(), Is.EqualTo(new byte[] { 7 }));
        }
        [Test]
        public async Task ReceiveFinDoesNotCloseDatagramOutputAndWireLimitIncludesIdentifier()
        {
            var fake = new FakeNative();
            using var native = Negotiated(fake, 10);
            using var router = ApplicationRouter(native);
            var receiving = true;
            using var channel = (IDisposable)Call(router, "Open", 256L, (Func<bool>)(() => receiving), (Func<bool>)(() => true));
            Call(router, "SetPeerEnabled", true);
            var owner = new PacketOwner();
            Call(router, "Deliver", ApplicationPacket(new byte[] { 0x40, 0x40, 99 }, owner));
            receiving = false;
            Call(channel, "CompleteReceive");
            Assert.That(owner.Disposals, Is.EqualTo(1), "FIN must release data still queued for the application.");
            Assert.That(await ApplicationReceive(channel), Is.Null);
            Assert.That(Call(channel, "TrySend", new ReadOnlyMemory<byte>(new byte[9])).ToString(), Is.EqualTo("TooLarge"));
            Assert.That(Call(channel, "TrySend", new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 })).ToString(), Is.EqualTo("Queued"));
            Assert.That(fake.Calls[0].Bytes, Is.EqualTo(new byte[] { 0x40, 0x40, 1, 2, 3 }));
            Complete(native, fake.Calls[0].Context, 6);
        }
        [Test]
        public void StaleDatagramChannelCannotSendOrCloseAReplacement()
        {
            var fake = new FakeNative();
            using var native = Negotiated(fake, 10);
            using var router = ApplicationRouter(native);
            using var old = (IDisposable)Call(router, "Open", 0L, (Func<bool>)(() => true), (Func<bool>)(() => true));
            old.Dispose();
            using var current = (IDisposable)Call(router, "Open", 0L, (Func<bool>)(() => true), (Func<bool>)(() => true));
            old.Dispose();
            Call(router, "SetPeerEnabled", true);
            Assert.That(Call(old, "TrySend", ReadOnlyMemory<byte>.Empty).ToString(), Is.EqualTo("Closed"));
            Assert.That(Call(current, "TrySend", ReadOnlyMemory<byte>.Empty).ToString(), Is.EqualTo("Queued"));
            Complete(native, fake.Calls[0].Context, 6);
        }
        private sealed class PacketOwner : IDisposable
        {
            internal int Disposals;
            public void Dispose() => Interlocked.Increment(ref Disposals);
        }
        private static IDisposable ApplicationRouter(object native, int packets = 128, int bytes = 262144)
        {
            var transport = ConstructApplicationType("MsQuicHttp3DatagramTransport", native);
            return (IDisposable)ConstructApplicationType("Http3DatagramRouter", transport, packets, bytes);
        }
        private static object ApplicationPacket(byte[] bytes, IDisposable owner)
            => ConstructApplicationType("Http3DatagramPacket", new ReadOnlyMemory<byte>(bytes), owner);
        private static object ConstructApplicationType(string name, params object[] arguments)
        {
            var type = Core.GetType("EmbedIO.Net.Internal.Http3." + name)
                ?? throw new IgnoreException("Application datagrams are unavailable in the selected asset.");
            return Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, arguments, null)
                ?? throw new AssertionException("Missing application datagram object.");
        }
        private static async Task<IDisposable?> ApplicationReceive(object channel, CancellationToken token = default)
        {
            var operation = Call(channel, "ReceiveAsync", token);
            var pending = (Task)(operation.GetType().GetMethod("AsTask")?.Invoke(operation, null)
                ?? throw new AssertionException("Missing receive task."));
            await pending;
            return TaskResult(pending) as IDisposable;
        }
    }
}
