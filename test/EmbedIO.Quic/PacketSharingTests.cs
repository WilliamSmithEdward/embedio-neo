namespace EmbedIO.Quic
{
    // QuicConnectionLimits.SharePackets: frames share packets, 1-RTT acknowledgments wait for
    // a second ack-eliciting packet, a packet to share or the acknowledgment delay, and stream
    // credit goes out with other frames.
    internal static class PacketSharingTests
    {
        private static readonly byte[] Original = [1, 2, 3, 4, 5, 6, 7, 8];
        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        internal static void Run()
        {
            Action[] cases =
            [
                DelayedAcknowledgment,
                SecondPacketAcknowledged,
                OutOfOrderAcknowledged,
                () => SharedEcho(true),
                () => SharedEcho(false),
                StreamCreditShared,
                SharedPacketRecovered
            ];

            foreach (var test in cases) test();

            Console.WriteLine("Packet-sharing cases passed: " + cases.Length);
        }

        private static (QuicConnection Connection, double Now) Create(bool share)
        {
            using var keys = PacketKeys.Initial(Original, false);
            var initial = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var connection = new QuicConnection(
                initial,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port),
                limits: new QuicConnectionLimits { SharePackets = share }
            );
            var warm = connection.Receive(initial, 0);
            warm.AddRange(connection.Tick(100));
            warm.AddRange(connection.Tick(200));
            ulong largest = 0;

            foreach (var packet in warm.Where(static packet => packet[0] < 128))
            {
                using var peer = new PacketKeys(new byte[32]);
                peer.Unprotect(packet, 1 + Client.Length, 0, out var number);
                largest = Math.Max(largest, number);
            }

            // The client's packet 0 acknowledges the whole first flight; ACK-only packets
            // need no acknowledgment of their own.
            connection.Receive(Packet(connection, Wire.Frame(2, largest, 0, 0, largest), 0), 201);
            Check(connection.Phase == ConnectionPhase.Active, "fixture");
            return (connection, 201);
        }

        private static byte[] Packet(QuicConnection connection, ReadOnlySpan<byte> frame, uint number) =>
            ConnectionCoreTests.ApplicationPacket(connection.ServerId, frame, number);

        // STREAM frame with an offset, a length and FIN.
        private static byte[] Stream(ulong id, ReadOnlySpan<byte> data)
        {
            var frame = new byte[1 + Wire.IntegerLength(id) + 1 + Wire.IntegerLength((ulong)data.Length) + data.Length];
            frame[0] = 0x0f;
            var offset = 1 + Wire.Integer(frame.AsSpan(1), id);
            frame[offset++] = 0;
            offset += Wire.Integer(frame.AsSpan(offset), (ulong)data.Length);
            data.CopyTo(frame.AsSpan(offset));
            return frame;
        }

        private readonly record struct Frame(ulong Type, ulong First, ulong Second);

        // The frames of each 1-RTT packet, in order: ACK as (largest, delay), STREAM as (id,
        // length), MAX_STREAMS as (count, 0).
        private static List<List<Frame>> Read(IEnumerable<byte[]> output)
        {
            var packets = new List<List<Frame>>();

            foreach (var packet in output.Where(static packet => packet[0] < 128))
            {
                using var keys = new PacketKeys(new byte[32]);
                var plain = keys.Unprotect(packet, 1 + Client.Length, 0, out _);
                packets.Add(Frames(plain));
            }

            return packets;
        }

        private static List<Frame> Frames(ReadOnlySpan<byte> plain)
        {
            var frames = new List<Frame>();
            var reader = new WireReader(plain);

            while (reader.Remaining != 0)
            {
                var type = reader.Integer();

                if (type == 0) continue;

                frames.Add(
                    type switch
                    {
                        1 or 0x1e => new Frame(type, 0, 0),
                        2 => ReadAcknowledgment(ref reader),
                        >= 8 and <= 15 => ReadStream(ref reader, type),
                        0x10 or 0x12 or 0x13 => new Frame(type, reader.Integer(), 0),
                        0x11 => new Frame(type, reader.Integer(), reader.Integer()),
                        0x18 => ReadConnectionId(ref reader),
                        _ => throw new QuicFuzzFailure("packet-sharing-unexpected-frame-" + type)
                    }
                );
            }

            return frames;
        }

        private static Frame ReadAcknowledgment(ref WireReader reader)
        {
            var largest = reader.Integer();
            var delay = reader.Integer();
            var ranges = reader.Integer();
            reader.Integer();

            for (ulong index = 0; index < ranges; index++)
            {
                reader.Integer();
                reader.Integer();
            }

            return new Frame(2, largest, delay);
        }

        private static Frame ReadStream(ref WireReader reader, ulong type)
        {
            var id = reader.Integer();

            if ((type & 4) != 0) reader.Integer();

            var length = (type & 2) != 0 ? (int)reader.Integer() : reader.Remaining;
            reader.Take(length);
            return new Frame(type, id, (ulong)length);
        }

        private static Frame ReadConnectionId(ref WireReader reader)
        {
            var sequence = reader.Integer();
            reader.Integer();
            reader.Take(reader.Byte());
            reader.Take(16);
            return new Frame(0x18, sequence, 0);
        }

        private static IEnumerable<Frame> All(List<List<Frame>> packets, ulong type) =>
            packets.SelectMany(static frames => frames).Where(frame => frame.Type == type);

        private static void DelayedAcknowledgment()
        {
            var (connection, now) = Create(true);

            using (connection)
            {
                var output = connection.Receive(Packet(connection, Wire.Frame(1), 1), now + 1);
                Check(output.Count == 0, "one ack-eliciting packet waits");
                Check(connection.Tick(now + 1.5).Count == 0, "acknowledgment waits for its delay");
                var acknowledgments = All(Read(connection.Tick(now + 2)), 2).ToArray();
                Check(acknowledgments.Length == 1 && acknowledgments[0].First == 1, "delay expiry acknowledges");
                // One millisecond in units of 2^3 microseconds.
                Check(acknowledgments[0].Second == 125, "acknowledgment reports its delay");
                Check(connection.Tick(now + 10).Count == 0, "one acknowledgment per wait");
            }
        }

        private static void SecondPacketAcknowledged()
        {
            var (connection, now) = Create(true);

            using (connection)
            {
                Check(connection.Receive(Packet(connection, Wire.Frame(1), 1), now + 1).Count == 0, "first waits");
                var acknowledgments = All(Read(connection.Receive(Packet(connection, Wire.Frame(1), 2), now + 1.2)), 2)
                    .ToArray();
                Check(
                    acknowledgments.Length == 1 && acknowledgments[0].First == 2 && acknowledgments[0].Second == 0,
                    "second ack-eliciting packet is acknowledged at once"
                );
            }
        }

        private static void OutOfOrderAcknowledged()
        {
            var (connection, now) = Create(true);

            using (connection)
            {
                var acknowledgments = All(Read(connection.Receive(Packet(connection, Wire.Frame(1), 3), now + 1)), 2)
                    .ToArray();
                Check(acknowledgments.Length == 1 && acknowledgments[0].First == 3, "gap is acknowledged at once");
            }
        }

        private static void SharedEcho(bool share)
        {
            var (connection, now) = Create(share);

            using (connection)
            {
                var packets = Read(connection.Receive(Packet(connection, Stream(0, "hello"u8), 1), now + 1));

                if (share)
                {
                    Check(packets.Count == 1, "acknowledgment and echo share one packet");
                    Check(
                        packets[0].Any(static frame => frame.Type == 2 && frame.First == 1)
                            && packets[0].Any(static frame => frame.Type == 0x0f && frame.First == 0 && frame.Second == 5),
                        "shared packet carries both frames"
                    );
                }

                else
                    Check(
                        packets.Count == 2 && packets.All(static frames => frames.Count == 1),
                        "without sharing every frame has its own packet"
                    );
            }
        }

        private static void StreamCreditShared()
        {
            var (connection, now) = Create(true);

            using (connection)
            {
                var echo = connection.Receive(Packet(connection, Stream(0, "a"u8), 1), now + 1);
                ulong largest = 0;

                foreach (var packet in echo)
                {
                    using var keys = new PacketKeys(new byte[32]);
                    keys.Unprotect(packet, 1 + Client.Length, 0, out var number);
                    largest = Math.Max(largest, number);
                }

                // The acknowledged echo retires stream 0. Fifteen of sixteen streams remain to
                // the client, so the new limit waits for a packet to share.
                var retired = Read(connection.Receive(Packet(connection, Wire.Frame(2, largest, 0, 0, 0), 2), now + 2));
                Check(!All(retired, 0x12).Any(), "credit waits for a packet to share");
                var shared = Read(connection.Receive(Packet(connection, Stream(4, "b"u8), 3), now + 3));
                Check(shared.Count == 1, "credit shares the echo's packet");
                Check(
                    shared[0].Any(static frame => frame.Type == 0x12 && frame.First == 17)
                        && shared[0].Any(static frame => frame.Type == 0x0f && frame.First == 4),
                    "MAX_STREAMS rides with the echo"
                );
            }
        }

        private static void SharedPacketRecovered()
        {
            var (connection, now) = Create(true);

            using (connection)
            {
                var first = Read(connection.Receive(Packet(connection, Stream(0, "hello"u8), 1), now + 1));
                Check(first.Count == 1 && first[0].Count >= 2, "shared packet sent");

                // Never acknowledged: the probe timeout repeats the echo, not the acknowledgment.
                var repeated = Read(connection.Tick(now + 2000));
                repeated.AddRange(Read(connection.Tick(now + 6000)));
                Check(
                    All(repeated, 0x0f).Any(static frame => frame.First == 0 && frame.Second == 5),
                    "lost shared packet repeats its stream data"
                );
            }
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "packet-sharing-" + name);
    }
}
