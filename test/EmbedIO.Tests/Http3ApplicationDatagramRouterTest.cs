using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // RFC 9297 HTTP/3 datagram routing over the EmbedIO.Quic datagram transport,
    // driven directly: packets are delivered by hand and sends are flushed by
    // polling the engine application.
    public sealed class Http3ApplicationDatagramRouterTest
    {
        private const string Http3 = "EmbedIO.Net.Internal.Http3.";

        [TestCase(0L, "00")]
        [TestCase(252L, "3F")]
        [TestCase(256L, "4040")]
        [TestCase(65536L, "80004000")]
        [TestCase((1L << 62) - 4, "CFFFFFFFFFFFFFFF")]
        public async Task ApplicationDatagramsStripOnlyTheirOwnStreamIdAndRetainStorage(long id, string prefix)
        {
            using var engine = EngineDatagrams.Create();
            using var router = engine.Router();
            using var channel = Open(router, id);
            EngineInternals.Call(router, "SetPeerEnabled", true);
            var bytes = Convert.FromHexString(prefix + "010203");
            EngineInternals.Call(router, "Deliver", Packet(bytes));
            var received = await Receive(channel) ?? throw new AssertionException("Missing packet.");
            var payload = Payload(received);
            Assert.That(payload.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(MemoryMarshal.TryGetArray(payload, out var segment), Is.True);
            Assert.That(segment.Array, Is.SameAs(bytes), "Routing must retain the receive buffer rather than copy it.");
            Assert.That(IsReleased(received), Is.False);
            received.Dispose();
            received.Dispose();
            Assert.That(IsReleased(received), Is.True);
        }

        [TestCase("unknown")]
        [TestCase("receive-closed")]
        [TestCase("packet-budget")]
        [TestCase("byte-budget")]
        [TestCase("not-negotiated")]
        public void DiscardedApplicationDatagramsReleaseTheirStorage(string reason)
        {
            using var engine = EngineDatagrams.Create();
            using var router = engine.Router(1, 2);
            using var channel = Open(router, 0L, () => reason != "receive-closed");
            EngineInternals.Call(router, "SetPeerEnabled", reason != "not-negotiated");
            IDisposable? first = null;

            if (reason == "packet-budget")
            {
                first = Packet([0, 1]);
                EngineInternals.Call(router, "Deliver", first);
            }

            byte[] bytes = reason == "unknown" ? [1, 1] : reason == "byte-budget" ? [0, 1, 2, 3] : [0, 1];
            var rejected = Packet(bytes);
            EngineInternals.Call(router, "Deliver", rejected);
            Assert.That(IsReleased(rejected), Is.True);
            channel.Dispose();

            if (first != null) Assert.That(IsReleased(first), Is.True);
        }

        [TestCase("")]
        [TestCase("C0")]
        [TestCase("D000000000000000")]
        public void MalformedApplicationDatagramIsAConnectionErrorAndReleasesStorage(string wire)
        {
            using var engine = EngineDatagrams.Create();
            using var router = engine.Router();
            var packet = Packet(Convert.FromHexString(wire));
            var error = Assert.Catch<Exception>(() => EngineInternals.Call(router, "Deliver", packet))
                ?? throw new AssertionException("Missing failure.");
            Assert.That(error.GetType().Name, Is.EqualTo("Http3ProtocolException"));
            Assert.That(EngineInternals.Property(error, "ErrorCode"), Is.EqualTo(0x33));
            Assert.That(IsReleased(packet), Is.True);
        }

        [Test]
        public async Task CanceledApplicationReceiveDoesNotLoseTheNextDatagram()
        {
            using var engine = EngineDatagrams.Create();
            using var router = engine.Router();
            using var channel = Open(router, 0L);
            EngineInternals.Call(router, "SetPeerEnabled", true);
            using var stop = new CancellationTokenSource();
            var canceled = Receive(channel, stop.Token);
            stop.Cancel();
            await Assert.ThatAsync(async () => await canceled, Throws.InstanceOf<OperationCanceledException>());
            EngineInternals.Call(router, "Deliver", Packet([0, 7]));
            using var packet = await Receive(channel) ?? throw new AssertionException("Missing retained packet.");
            Assert.That(Payload(packet).ToArray(), Is.EqualTo(new byte[] { 7 }));
        }

        [Test]
        public async Task ReceiveFinDoesNotCloseDatagramOutputAndWireLimitIncludesIdentifier()
        {
            using var engine = EngineDatagrams.Create(10);
            using var router = engine.Router();
            var receiving = true;
            using var channel = Open(router, 256L, () => receiving);
            EngineInternals.Call(router, "SetPeerEnabled", true);
            var queued = Packet([0x40, 0x40, 99]);
            EngineInternals.Call(router, "Deliver", queued);
            receiving = false;
            EngineInternals.Call(channel, "CompleteReceive");
            Assert.That(IsReleased(queued), Is.True, "FIN must release data still queued for the application.");
            Assert.That(await Receive(channel), Is.Null);
            Assert.That(TrySend(channel, new byte[9]), Is.EqualTo("TooLarge"));
            Assert.That(TrySend(channel, [1, 2, 3]), Is.EqualTo("Queued"));
            engine.Flush();
            Assert.That(engine.Sent, Has.Count.EqualTo(1));
            Assert.That(engine.Sent[0], Is.EqualTo(new byte[] { 0x40, 0x40, 1, 2, 3 }));
        }

        [Test]
        public void StaleDatagramChannelCannotSendOrCloseAReplacement()
        {
            using var engine = EngineDatagrams.Create(10);
            using var router = engine.Router();
            using var old = Open(router, 0L);
            old.Dispose();
            using var current = Open(router, 0L);
            old.Dispose();
            EngineInternals.Call(router, "SetPeerEnabled", true);
            Assert.That(TrySend(old, []), Is.EqualTo("Closed"));
            Assert.That(TrySend(current, []), Is.EqualTo("Queued"));
        }

        private static IDisposable Packet(byte[] bytes) =>
            (IDisposable)EngineInternals.Create(Http3 + "Http3DatagramPacket", bytes);

        private static ReadOnlyMemory<byte> Payload(object packet) =>
            (ReadOnlyMemory<byte>)(EngineInternals.Property(packet, "Payload")
                ?? throw new AssertionException("Missing payload."));

        // A released packet no longer exposes its storage.
        private static bool IsReleased(object packet)
        {
            try
            {
                _ = Payload(packet);
                return false;
            }

            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private static IDisposable Open(object router, long id, Func<bool>? canReceive = null) =>
            (IDisposable)(EngineInternals.Call(router, "Open", id, canReceive ?? (() => true), (Func<bool>)(() => true))
                ?? throw new AssertionException("Missing datagram channel."));

        private static string TrySend(object channel, byte[] payload) =>
            EngineInternals
                .Call(channel, "TrySend", new ReadOnlyMemory<byte>(payload))
                ?.ToString()
                ?? throw new AssertionException("Missing send status.");

        private static async Task<IDisposable?> Receive(object channel, CancellationToken token = default) =>
            await EngineInternals.AwaitAsync(EngineInternals.Call(channel, "ReceiveAsync", token)) as IDisposable;

        // An engine application whose datagram sends are recorded instead of transmitted.
        private sealed class EngineDatagrams : IDisposable
        {
            private readonly List<byte[]> _sent;
            private readonly IDisposable _application;
            private readonly object _transport;

            private EngineDatagrams(IDisposable application, object transport, List<byte[]> sent)
            {
                _application = application;
                _transport = transport;
                _sent = sent;
            }

            internal List<byte[]> Sent
            {
                get
                {
                    lock (_sent) return [.. _sent];
                }
            }

            // limit is the peer's largest datagram payload, or null when it negotiated none.
            internal static EngineDatagrams Create(int? limit = null)
            {
                if (EngineInternals.Find("EmbedIO.Quic.AsyncStreamApplication") == null)
                    Assert.Ignore("This build of the library has no EmbedIO.Quic engine.");

                var sent = new List<byte[]>();
                var operations = EngineInternals.Create(
                    "EmbedIO.Quic.StreamOperations",
                    (Action<ulong, byte[], bool>)((_, _, _) => { }),
                    (Action<ulong, byte[]>)((_, _) => { }),
                    (Action<byte[]>)(_ => { }),
                    (Action<ulong, long>)((_, _) => { }),
                    (Action<ulong, int>)((_, _) => { }),
                    (Func<ulong, ReadOnlyMemory<byte>, bool, bool>)((_, _, _) => false),
                    (Func<bool, ulong?>)(_ => null),
                    (Action<ulong, ulong>)((_, _) => { }),
                    (Action<ulong, ulong>)((_, _) => { }),
                    (Func<byte[], bool>)(bytes =>
                    {
                        lock (sent) sent.Add(bytes);

                        return true;
                    }),
                    limit,
                    null,
                    16,
                    16
                );
                var application = (IDisposable)EngineInternals.Create(
                    "EmbedIO.Quic.AsyncStreamApplication",
                    operations,
                    null
                );
                return new EngineDatagrams(
                    application,
                    EngineInternals.Create(Http3 + "EmbedIOQuicDatagramTransport", application),
                    sent
                );
            }

            internal IDisposable Router(int packets = 128, int bytes = 262144) =>
                (IDisposable)EngineInternals.Create(Http3 + "Http3DatagramRouter", _transport, packets, bytes);

            // Hands queued sends to the recorded datagram operation.
            internal void Flush() => EngineInternals.Call(_application, "Poll");

            public void Dispose() => _application.Dispose();
        }
    }
}
