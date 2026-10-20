using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    internal static class StreamLifecycleTests
    {
        private static readonly byte[] Headers = Frame(
            1,
            QpackEncoder.Encode(
                [
                    new HpackField(":method", "CONNECT"),
                    new HpackField(":protocol", "webtransport-h3"),
                    new HpackField(":scheme", "https"),
                    new HpackField(":authority", "localhost"),
                    new HpackField(":path", "/echo"),
                    new HpackField("origin", "https://localhost")
                ],
                8192,
                8192
            )
        );

        private sealed class Peer : IDisposable
        {
            internal List<Delivery> Sent { get; } = [];
            internal ConnectionStreams Streams { get; }

            private readonly bool _acknowledge;

            internal Peer(bool acknowledge, bool authorize = false)
            {
                _acknowledge = acknowledge;
                Streams = new ConnectionStreams(
                    Send,
                    1UL << 32,
                    65536,
                    65536,
                    65536,
                    1UL << 20,
                    16,
                    1200,
                    true,
                    _ => authorize
                );
                var settings = Wire.Frame(8, 1, 0x33, 1, 0x2c7cf000, 1, 0x2b61, 65536, 0x2b64, 1024, 0x2b65, 8);
                Streams.Receive(
                    2,
                    0,
                    [
                        0,
                        .. Frame(4, settings)
                    ],
                    false
                );
            }

            private Delivery Send(byte[] bytes, bool reliable)
            {
                var delivery = new Delivery(2, bytes, reliable) { Acknowledged = _acknowledge };
                Sent.Add(delivery);
                return delivery;
            }

            internal void Acknowledge()
            {
                foreach (var delivery in Sent) delivery.Acknowledged = true;

                Sent.Clear();
                Streams.Prune();
            }

            public void Dispose() => Streams.Dispose();
        }

        internal static void Run()
        {
            Backpressure();
            BufferedRetirement();
            SessionClosure();

            using (var peer = new Peer(false))
            {
                peer.Streams.Receive(0, 0, Headers, true);
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 0 && peer.Streams.IncomingCount == 2,
                    "bidi-retirement-waits-for-fin-ack"
                );
                peer.Acknowledge();
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 1
                        && peer.Streams.IncomingCount == 1
                        && peer.Streams.OutgoingCount == 3,
                    "bidi-retires-after-fin-ack"
                );
                var before = peer.Streams.ReceivedData;
                peer.Streams.Receive(0, 0, Headers, true);
                peer.Streams.Reset(0, 1, (ulong)Headers.Length, 0, false);
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 1 && peer.Streams.ReceivedData == before,
                    "retired-bidi-not-reopened-or-accounted-twice"
                );
                RejectFinal(() => peer.Streams.Receive(0, (ulong)Headers.Length, [1], false));
            }

            using (var peer = new Peer(true))
            {
                for (ulong index = 0; index < 2000; index++)
                {
                    peer.Streams.Receive(index * 4, 0, Headers, true);
                    Bounds(peer.Streams);
                    QuicFuzzChecks.Require(
                        peer.Streams.IncomingCount == 1
                            && peer.Streams.OutgoingCount == 3
                            && peer.Streams.Application.RetainedInputs == 1,
                        "completed-request-buffers-retired"
                    );
                    peer.Sent.Clear();
                }

                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 2000 && peer.Streams.ClosedHistoryCount == 64,
                    "bounded-recent-final-sizes"
                );
                var before = peer.Streams.ReceivedData;
                peer.Streams.Receive(0, 0, Headers, true);
                QuicFuzzChecks.Require(
                    peer.Streams.ReceivedData == before && peer.Streams.IncomingCount == 1,
                    "forgotten-final-size-does-not-revive-stream"
                );
            }

            using (var peer = new Peer(false, true))
            {
                peer.Streams.Receive(0, 0, Headers, false);
                peer.Streams.Receive(
                    6,
                    0,
                    [
                        .. Wire.Frame(0x54, 0),
                        1,
                        2,
                        3
                    ],
                    true
                );
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 0
                        && peer.Streams.IncomingCount == 3
                        && peer.Streams.OutgoingCount == 5
                        && peer.Streams.Application.RetainedOutputs == 1,
                    "echo-retains-receive-capacity-until-send-ack"
                );
                peer.Acknowledge();
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 2
                        && peer.Streams.OutgoingCount == 4
                        && peer.Streams.Application.RetainedOutputs == 0,
                    "uni-send-fin-ack-releases-handle"
                );
                peer.Streams.UpdateStream(15, 100000);
                peer.Streams.Stop(15, 1);
                QuicFuzzChecks.Require(peer.Streams.OutgoingCount == 4, "retired-send-controls-do-not-recreate-stream");
                peer.Dispose();
                peer.Dispose();
                QuicFuzzChecks.Require(
                    peer.Streams.IncomingCount == 0
                        && peer.Streams.OutgoingCount == 0
                        && peer.Streams.Application.RetainedInputs == 0
                        && peer.Streams.Application.RetainedOutputs == 0,
                    "stream-disposal-releases-all-owned-buffers"
                );
            }
        }

        internal static void Mutate(ReadOnlySpan<byte> commands)
        {
            using var peer = new Peer(false, true);
            peer.Streams.Receive(0, 0, Headers, false);
            peer.Streams.Receive(
                4,
                0,
                [
                    .. Wire.Frame(0x41, 0),
                    1,
                    2,
                    3
                ],
                true
            );
            var acknowledged = false;

            foreach (var command in commands[..Math.Min(commands.Length, 16)])
            {
                if ((command & 1) == 0)
                {
                    peer.Acknowledge();
                    acknowledged = true;
                }

                else
                {
                    peer.Streams.Receive(0, (ulong)Headers.Length, Frame(0, Frame(0x2843, [0, 0, 0, 0])), false);
                    QuicFuzzChecks.Require(
                        peer.Streams.OutboundResets == (acknowledged ? 0 : 1),
                        "session-close-ack-order-oracle"
                    );
                    break;
                }

                Bounds(peer.Streams);
            }
        }

        private static void SessionClosure()
        {
            using var peer = new Peer(false, true);
            peer.Streams.Receive(0, 0, Headers, false);
            peer.Streams.Receive(
                4,
                0,
                [
                    .. Wire.Frame(0x41, 0),
                    1,
                    2,
                    3
                ],
                true
            );
            peer.Streams.Receive(0, (ulong)Headers.Length, Frame(0, Frame(0x2843, [0, 0, 0, 0])), false);
            QuicFuzzChecks.Require(
                peer.Streams.OutboundResets == 1,
                "session-close-still-owns-unacknowledged-bidi-response"
            );
        }

        private static void BufferedRetirement()
        {
            using (var peer = new Peer(false, true))
            {
                peer.Streams.Receive(0, 0, Headers, false);
                peer.Streams.Receive(
                    4,
                    0,
                    [
                        .. Wire.Frame(0x41, 0),
                        1
                    ],
                    true
                );
                peer.Streams.Receive(6, 0, Wire.Frame(0x54, 4), true);
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 0 && peer.Streams.IncomingCount == 4,
                    "buffered-stream-waits-for-exact-session-classification"
                );
                peer.Acknowledge();
                QuicFuzzChecks.Require(
                    peer.Streams.RetiredStreams == 2
                        && peer.Streams.IncomingCount == 2
                        && peer.Streams.Application.RetainedInputs == 2,
                    "unavailable-session-reference-releases-buffered-input"
                );
            }

            using (var peer = new Peer(false, true))
            {
                var prefix = Wire.Frame(0x54, 4);
                peer.Streams.Receive(6, 0, prefix, false);
                peer.Streams.Reset(6, 1, (ulong)prefix.Length, (ulong)prefix.Length, true);
                peer.Streams.Receive(4, 0, Headers, false);
                QuicFuzzChecks.Require(
                    peer.Streams.Application.AcceptedSessions == 1
                        && peer.Streams.IncomingCount == 2
                        && peer.Streams.Application.RetainedInputs == 2,
                    "later-session-admission-tolerates-retired-buffered-stream"
                );
            }
        }

        private static void Backpressure()
        {
            using var peer = new Peer(false, true);
            peer.Streams.Receive(0, 0, Headers, false);

            for (ulong id = 6; id < 66; id += 4)
                peer.Streams.Receive(
                    id,
                    0,
                    [
                        .. Wire.Frame(0x54, 0),
                        1,
                        2,
                        3
                    ],
                    true
                );

            QuicFuzzChecks.Require(
                peer.Streams.IncomingCount == 17
                    && peer.Streams.OutgoingCount == 19
                    && peer.Streams.RetiredStreams == 0,
                "missing-output-acks-retain-bounded-input-capacity"
            );
            RejectCode(
                () => peer.Streams.Receive(
                    66,
                    0,
                    [
                        .. Wire.Frame(0x54, 0),
                        1
                    ],
                    true
                ),
                0x04
            );
            peer.Acknowledge();
            QuicFuzzChecks.Require(
                peer.Streams.IncomingCount == 2 && peer.Streams.RetiredStreams == 30,
                "acknowledgments-replenish-blocked-stream-capacity"
            );
            peer.Streams.Receive(
                66,
                0,
                [
                    .. Wire.Frame(0x54, 0),
                    1
                ],
                true
            );
            RejectCode(() => peer.Streams.Stop(6, 0), 0x05);
            RejectCode(() => peer.Streams.UpdateStream(6, 65536), 0x05);
            RejectCode(() => peer.Streams.Stop(1003, 0), 0x05);
            Bounds(peer.Streams);
        }

        private static void RejectCode(Action action, ulong code)
        {
            try
            {
                action();
            }

            catch (TransportException error) when (error.ErrorCode == code)
            {
                return;
            }

            throw new QuicFuzzFailure("stream-control-error-code");
        }

        private static void Bounds(ConnectionStreams streams) =>
            QuicFuzzChecks.Require(
                streams.IncomingCount <= 32
                    && streams.OutgoingCount <= 48
                    && streams.ClosedHistoryCount <= 64
                    && streams.Application.RetainedInputs <= 32
                    && streams.Application.RetainedOutputs <= 16,
                "stream-lifecycle-resources-bounded"
            );

        private static void RejectFinal(Action action)
        {
            try
            {
                action();
            }

            catch (TransportException error) when (error.ErrorCode == 0x06)
            {
                return;
            }

            throw new QuicFuzzFailure("retired-final-size-conflict-not-rejected");
        }

        private static byte[] Frame(ulong type, byte[] payload) =>
        [
            .. Wire.Frame(type, (ulong)payload.Length),
            .. payload
        ];
    }
}
