using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.Http3;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Quic
{
    internal static class BridgeFuzz
    {
        private sealed class Peer : IDisposable
        {
            internal readonly List<Delivery> Sent = [];
            internal readonly ConnectionStreams Streams;

            internal Peer(int owner)
            {
                var token = "actor-" + owner;
                Streams = new(
                    (bytes, reliable) =>
                    {
                        var delivery = new Delivery(2, bytes, reliable) { Acknowledged = true };
                        Sent.Add(delivery);
                        return delivery;
                    },
                    262144,
                    65536,
                    65536,
                    65536,
                    16,
                    16,
                    1200,
                    true,
                    headers => headers.GetValueOrDefault("authorization") == token
                );
                var settings = Wire.Frame(8, 1, 0x33, 1, 0x2c7cf000, 1, 0x2b61, 65536, 0x2b64, 8, 0x2b65, 8);
                Streams.Receive(
                    2,
                    0,
                    [
                        0,
                        .. Frame(4, settings)
                    ],
                    false
                );
                var headers = QpackEncoder.Encode(
                    [
                        new HpackField(":method", "CONNECT"),
                        new HpackField(":protocol", "webtransport-h3"),
                        new HpackField(":scheme", "https"),
                        new HpackField(":authority", "localhost"),
                        new HpackField(":path", "/echo"),
                        new HpackField("origin", "https://localhost"),
                        new HpackField("authorization", token)
                    ],
                    8192,
                    8192
                );
                Streams.Receive(0, 0, Frame(1, headers), false);
                QuicFuzzChecks.Require(Streams.Application.AcceptedSessions == 1, "bridge-admission");
            }

            internal string Snapshot() =>
                string.Join(
                    '|',
                    Sent.Count,
                    Streams.ReceivedData,
                    Streams.Application.ActiveSessions,
                    Streams.Application.StreamsDelivered,
                    Streams.Application.DatagramsDelivered,
                    Streams.Application.ResetsDelivered
                );

            public void Dispose() => Streams.Dispose();
        }

        internal static void Check(ReadOnlySpan<byte> data)
        {
            using var healthy = new Peer(1);
            using var mutated = new Peer(2);
            var expected = healthy.Snapshot();
            var payload = data[..Math.Min(data.Length, 1024)].ToArray();

            try
            {
                switch (data.Length == 0 ? 0 : data[0] % 5)
                {
                    case 0:
                        mutated.Streams.Receive(4, 0, payload, true);
                        break;

                    case 1:
                        mutated.Streams.Receive(6, 0, payload, true);
                        break;

                    case 2:
                        mutated.Streams.Datagram(payload);
                        break;

                    case 3:
                        var prefix = Wire.Frame(0x54, 0);
                        mutated.Streams.Reset(
                            6,
                            1,
                            (ulong)payload.Length + (ulong)prefix.Length,
                            (ulong)prefix.Length,
                            true
                        );
                        mutated.Streams.Receive(6, 1, prefix[1..], false);
                        QuicFuzzChecks.Require(
                            mutated.Streams.Application.ResetsDelivered == 0,
                            "reset-waits-for-prefix"
                        );
                        mutated.Streams.Receive(6, 0, prefix[..1], false);
                        QuicFuzzChecks.Require(
                            mutated.Streams.Application.ResetsDelivered == 1,
                            "reset-delivered-once"
                        );
                        mutated.Streams.Receive(6, 0, prefix, false);
                        QuicFuzzChecks.Require(
                            mutated.Streams.Application.ResetsDelivered == 1,
                            "duplicate-reset-prefix"
                        );
                        break;

                    case 4:
                        mutated.Streams.UpdateData(0);
                        mutated.Streams.UpdateStream(3, 0);
                        mutated.Streams.Stop(15, data[0]);
                        break;
                }
            }

            catch (InvalidDataException) { }

            catch (WebTransportException) { }

            catch (Http3ProtocolException) { }

            QuicFuzzChecks.Require(healthy.Snapshot() == expected, "bridge-connection-isolation");
            healthy.Sent.Clear();
            var datagram = new byte[] { 0, 1, 2, 3, 4 };
            healthy.Streams.Datagram(datagram);
            var echoed = healthy.Sent.Single();
            QuicFuzzChecks.Require(
                !echoed.Reliable
                    && echoed.Bytes
                        .AsSpan()
                        .SequenceEqual(Frame(0x31, datagram)),
                "bridge-healthy-datagram"
            );
        }

        private static byte[] Frame(ulong type, byte[] bytes) =>
        [
            .. Wire.Frame(type, (ulong)bytes.Length),
            .. bytes
        ];
    }
}
