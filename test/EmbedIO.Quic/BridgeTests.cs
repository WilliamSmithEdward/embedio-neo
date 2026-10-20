using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Quic
{
    internal static class BridgeTests
    {
        internal static void Run()
        {
            var sent = new List<Delivery>();
            ConnectionStreams Create(ulong credit = 262144) =>
                new(
                    (bytes, reliable) =>
                    {
                        var delivery = new Delivery(2, bytes, reliable);
                        sent.Add(delivery);
                        return delivery;
                    },
                    credit,
                    65536,
                    65536,
                    65536,
                    16,
                    16,
                    1200,
                    true,
                    _ => true
                );

            using (var connection = Create())
            {
                var settings = Wire.Frame(8, 1, 0x33, 1, 0x2c7cf000, 1, 0x2b61, 65536, 0x2b64, 8, 0x2b65, 8);
                connection.Receive(
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
                        new HpackField("origin", "https://localhost")
                    ],
                    8192,
                    8192
                );
                connection.Receive(0, 0, Frame(1, headers), false);
                Check(connection.Application.AcceptedSessions == 1, "linked session admission");
                var prefix = Wire.Frame(0x54, 0);
                connection.Reset(6, 1234, 100, (ulong)prefix.Length, true);
                connection.Receive(6, 1, prefix[1..], false);
                Check(connection.Application.ResetsDelivered == 0, "reset waits for reliable gap");
                connection.Receive(6, 0, prefix[..1], false);
                Check(
                    connection.Application.ResetsDelivered == 1 && connection.Application.StreamsDelivered == 1,
                    "prefix routed before reset delivery"
                );
                Reject(() => connection.Reset(6, 1234, 99, 0, true), "reset final-size conflict");
                Reject(() => connection.Receive(6, 100, [1], false), "data beyond final size");
                Reject(() => connection.Reset(10, 1234, 3, 4, true), "reliable size above final size");
                Reject(() => connection.Receive(3, 0, [1], false), "peer cannot write server unidirectional stream");
                Reject(() => connection.Stop(3, 0), "critical stream stop");
                Reject(
                    () => connection.Reset(2, 1, (ulong)(1 + Frame(4, settings).Length), 0, true),
                    "critical stream reset"
                );
            }

            using (var connection = Create())
            {
                for (ulong id = 2; id < 18; id += 4)
                {
                    connection.Reset(id, 1, 65536, 0, true);
                    connection.Reset(id, 1, 65536, 0, true);
                }

                Check(
                    connection.ReceivedData == 262144 && connection.ReceiveCreditUpdates == 2,
                    "reset final sizes consume and release connection credit once"
                );
                connection.Reset(18, 1, 1, 0, true);
            }

            using (var connection = Create())
            {
                for (ulong id = 2; id < 18; id += 4) connection.Reset(id, 1, 65536, 65536, true);

                Reject(() => connection.Reset(18, 1, 1, 0, true), "reliable gaps retain connection credit");
            }

            using (var connection = Create())
            {
                var chunk = new byte[32768];
                chunk[0] = 0x21; // Unknown unidirectional stream: discard incrementally.

                for (ulong offset = 0; offset < 655360; offset += (ulong)chunk.Length)
                    connection.Receive(2, offset, chunk, false);

                Check(
                    connection.ReceivedData == 655360 && connection.ReceiveCreditUpdates >= 20,
                    "consumption replenishes stream and connection windows"
                );
                Reject(
                    () => connection.Receive(6, 65536, [1], false),
                    "fresh stream cannot borrow another stream credit"
                );
            }

            sent.Clear();

            using (var connection = Create(0))
            {
                Check(sent.Count == 0, "zero connection send credit blocks control streams");
                connection.UpdateData(65536);
                Check(sent.Count == 3, "credit resumes queued control streams");
            }
        }

        private static byte[] Frame(ulong type, byte[] bytes) =>
        [
            .. Wire.Frame(type, (ulong)bytes.Length),
            .. bytes
        ];

        private static void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException("Bridge regression: " + name);
        }

        private static void Reject(Action operation, string name)
        {
            try
            {
                operation();
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException("Expected bridge rejection: " + name);
        }
    }
}
