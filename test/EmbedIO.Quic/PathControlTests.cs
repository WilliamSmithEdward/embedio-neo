namespace EmbedIO.Quic
{
    internal static class PathControlTests
    {
        private static readonly byte[] Original = [1, 2, 3, 4, 5, 6, 7, 8];
        private static readonly byte[] Client = [9, 10, 11, 12, 13, 14, 15, 16];

        internal static void Run()
        {
            var failures = new List<string>();
            var count = 0;

            void Case(string name, Action test)
            {
                count++;

                try
                {
                    test();
                }

                catch (QuicFuzzFailure error)
                {
                    failures.Add(name + ": " + error.Invariant);
                }
            }

            Case("padded-echo", Echo);
            Case("one-response-through-loss", Loss);
            Case("amplification-padding-bound", Amplification);
            Case("unmatched-response-policy", () => Invalid(Frame(0x1b, new byte[8]), 10));

            foreach (var type in new byte[] { 0x1a, 0x1b })
                for (var length = 0; length < 8; length++)
                {
                    var frame = Frame(type, new byte[length]);
                    Case("truncated-" + type + "-" + length, () => Invalid(frame, 7));
                }

            if (failures.Count != 0)
                throw new QuicFuzzFailure(
                    "path-controls " + failures.Count + "/" + count + ": " + string.Join("; ", failures)
                );

            Console.WriteLine("Path-control cases passed: " + count);
        }

        private static QuicConnection Create()
        {
            using var keys = PacketKeys.Initial(Original, false);
            var initial = NativeFuzz.Initial(keys, Original, Client, 0, [1]);
            var connection = new QuicConnection(
                initial,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port)
            );
            var warm = connection.Receive(initial, 0);
            warm.AddRange(connection.Tick(100));
            warm.AddRange(connection.Tick(200));
            // Acknowledge both handshake-completion deliveries so path response
            // loss is the only outstanding retransmission candidate.
            ulong largest = 0;

            foreach (var packet in warm.Where(static packet => packet[0] < 128))
            {
                using var peer = new PacketKeys(new byte[32]);
                peer.Unprotect(packet, 1 + Client.Length, 0, out var number);
                largest = Math.Max(largest, number);
            }

            connection.Receive(Packet(connection, Wire.Frame(2, largest, 0, 0, largest), 0), 201);
            Check(connection.Phase == ConnectionPhase.Active, "fixture warm ACK");
            return connection;
        }

        private static byte[] Packet(QuicConnection connection, ReadOnlySpan<byte> frame, uint number = 1) =>
            ConnectionCoreTests.ApplicationPacket(connection.ServerId, frame, number);

        private static byte[] Frame(byte type, ReadOnlySpan<byte> value)
        {
            var frame = new byte[value.Length + 1];
            frame[0] = type;
            value.CopyTo(frame.AsSpan(1));
            return frame;
        }

        private static byte[] Plain(byte[] packet)
        {
            using var keys = new PacketKeys(new byte[32]);
            return keys.Unprotect(packet, 1 + Client.Length, 0, out _);
        }

        private static IEnumerable<byte[]> Responses(IEnumerable<byte[]> packets) =>
            packets
                .Where(static packet => packet[0] < 128)
                .Where(static packet => Plain(packet)[0] == 0x1b);

        private static void Echo()
        {
            using var connection = Create();
            byte[] challenge = [0, 1, 2, 3, 4, 5, 6, 255];
            var output = connection.Receive(Packet(connection, Frame(0x1a, challenge)), 300);
            var responses = Responses(output).ToArray();
            Check(connection.Phase == ConnectionPhase.Active && responses.Length == 1, "one echo");
            var plain = Plain(responses[0]);
            Check(
                plain
                    .AsSpan(1, 8)
                    .SequenceEqual(challenge)
                    && plain
                        .AsSpan(9)
                        .IndexOfAnyExcept((byte)0)
                        < 0,
                "exact echo and padding"
            );
            Check(responses[0].Length == 1200, "minimum response datagram");
        }

        private static void Loss()
        {
            using var connection = Create();
            var output = connection.Receive(Packet(connection, Frame(0x1a, new byte[8])), 300);
            output.AddRange(connection.Tick(2000));
            output.AddRange(connection.Tick(4000));
            Check(Responses(output).Count() == 1, "response is not retransmitted by PTO");
        }

        private static void Amplification()
        {
            using var connection = Create();
            var sizes = new List<int>();

            for (uint number = 1; number <= 10; number++)
            {
                var output = connection.Receive(
                    Packet(connection, Frame(0x1a, new byte[8]), number),
                    300 + number * 100
                );
                sizes.AddRange(Responses(output).Select(static packet => packet.Length));
                Check(
                    connection.SentBytes <= connection.ReceivedBytes * 3,
                    "padding respects unvalidated amplification budget"
                );
            }

            Check(
                sizes.Count == 10 && sizes.Contains(1200) && sizes.Any(static size => size < 1200),
                "padding shrinks only at amplification limit"
            );
        }

        private static void Invalid(byte[] frame, ulong expected)
        {
            using var connection = Create();
            var output = connection.Receive(Packet(connection, frame), 300);
            Check(connection.Phase == ConnectionPhase.Closing && output.Count == 1, "invalid path-control closure");
            var reader = new WireReader(Plain(output[0]));
            Check(reader.Integer() == 0x1c && reader.Integer() == expected, "path-control error code");
        }

        internal static void Mutate(ReadOnlySpan<byte> data)
        {
            var length = data.IsEmpty ? 8 : data[0] % 9;
            var type = !data.IsEmpty && (data[0] & 16) != 0 ? (byte)0x1b : (byte)0x1a;
            Span<byte> value = stackalloc byte[8];
            value.Clear();
            data[..Math.Min(data.Length, 8)].CopyTo(value);
            var frame = Frame(type, value[..length]);

            if (length < 8 || type == 0x1b) Invalid(frame, length < 8 ? 7UL : 10UL);

            else
            {
                using var connection = Create();
                var output = connection.Receive(Packet(connection, frame), 300);
                var responses = Responses(output).ToArray();
                Check(
                    connection.Phase == ConnectionPhase.Active && responses.Length == 1 && responses[0].Length == 1200,
                    "mutation padded echo"
                );
                Check(
                    Plain(responses[0])
                        .AsSpan(1, 8)
                        .SequenceEqual(value),
                    "mutation echo bytes"
                );
            }
        }

        private static void Check(bool condition, string name) =>
            QuicFuzzChecks.Require(condition, "path-control-" + name);
    }
}
