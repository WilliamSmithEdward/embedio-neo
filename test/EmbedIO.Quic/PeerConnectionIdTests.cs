using System.Buffers.Binary;

namespace EmbedIO.Quic
{
    internal static class PeerConnectionIdTests
    {
        private static byte[] Id(ulong sequence, int size = 8)
        {
            var bytes = new byte[Math.Max(8, size)];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, sequence);
            return bytes[..size];
        }

        private static byte[] Token(ulong sequence) =>
        [
            .. Id(sequence),
            .. Id(~sequence)
        ];

        internal static void Run()
        {
            RotationAndCopies();
            DelayedFrames();
            Limits();
            Conflicts();
            LongLifetime();
            PacketRotation();

            foreach (var data in new byte[][]
            {
                [],
                [1, 1, 1],
                [255, 0, 1, 4],
                [7, 2, 3, 9, 0, 255]
            }) Mutate(data);
        }

        private static void RotationAndCopies()
        {
            var retired = new List<ulong>();
            using var ids = new PeerConnectionIds(Id(0), value =>
            {
                retired.Add(value);
                return new Delivery(2, Wire.Frame(0x19, value));
            });
            var id = Id(1);
            var token = Token(1);
            ids.Add(1, 0, id, token);
            id[0] ^= 128;
            token[0] ^= 128;
            ids.Add(1, 0, Id(1), Token(1));
            Check(ids.Current.SequenceEqual(Id(0)) && ids.ActiveCount == 2, "spare and owned input copies");
            ids.Add(2, 1, Id(2, 20), Token(2));
            Check(
                ids.Current.SequenceEqual(Id(1)) && retired.SequenceEqual(new ulong[] { 0 }),
                "switch before retiring original"
            );
            ids.Add(3, 3, Id(3), Token(3));
            Check(
                ids.Current.SequenceEqual(Id(3))
                    && ids.ActiveCount == 1
                    && retired
                        .Order()
                        .SequenceEqual(new ulong[] { 0, 1, 2 }),
                "replace both active IDs"
            );
        }

        private static void DelayedFrames()
        {
            var sent = new List<Delivery>();
            using var ids = new PeerConnectionIds(Id(0), value =>
            {
                var delivery = new Delivery(2, Wire.Frame(0x19, value));
                sent.Add(delivery);
                return delivery;
            });
            ids.Add(10, 10, Id(10), Token(10));
            ids.Add(2, 1, Id(2), Token(2));
            ids.Add(2, 0, Id(2), Token(2));
            Check(
                ids.RetirePrior == 10 && ids.Current.SequenceEqual(Id(10)) && sent.Count == 2,
                "stale IDs retire once while pending"
            );

            foreach (var delivery in sent) delivery.Acknowledged = true;

            ids.Prune();
            Check(ids.RetainedRetirements == 0, "acknowledged retirements release");
            ids.Add(PacketNumberWindow.Maximum, PacketNumberWindow.Maximum, Id(20), Token(20));
            Check(
                ids.ActiveCount == 1 && ids.Current.SequenceEqual(Id(20)) && sent.Count == 3,
                "sequence jumps do not allocate missing history"
            );
        }

        private static void Limits()
        {
            using var ids = new PeerConnectionIds(Id(0), static value => new Delivery(2, Wire.Frame(0x19, value)));
            ids.Add(1, 0, Id(1), Token(1));
            Error(() => ids.Add(2, 0, Id(2), Token(2)), 9);
            Check(ids.ActiveCount == 2, "active limit stays bounded");
            using var pending = new PeerConnectionIds(Id(0), static value => new Delivery(2, Wire.Frame(0x19, value)));

            for (ulong index = 1; index <= PeerConnectionIds.RetirementLimit; index++)
                pending.Add(index, index, Id(index), Token(index));

            Error(() => pending.Add(5, 5, Id(5), Token(5)), 9);
            Check(
                pending.RetainedRetirements == 4 && pending.Current.SequenceEqual(Id(5)),
                "pending limit preserves usable destination for close"
            );
            using var zero = new PeerConnectionIds([], static value => new Delivery(2, Wire.Frame(0x19, value)));
            Error(() => zero.Add(1, 0, Id(1), Token(1)), 10);
        }

        private static void Conflicts()
        {
            using var ids = new PeerConnectionIds(Id(0), static value => new Delivery(2, Wire.Frame(0x19, value)));
            ids.Add(0, 0, Id(0), Token(0));
            ids.Add(0, 0, Id(0), Token(0));
            ids.Add(1, 0, Id(1), Token(1));
            Error(() => ids.Add(1, 0, Id(2), Token(1)), 10);
            Error(() => ids.Add(1, 0, Id(1), Token(2)), 10);
            Error(() => ids.Add(2, 0, Id(1), Token(2)), 10);
            Error(() => ids.Add(2, 0, Id(2), Token(1)), 10);
            Error(() => ids.Add(2, 3, Id(2), Token(2)), 7);
            Error(() => ids.Add(ulong.MaxValue, 0, Id(2), Token(2)), 7);
            Error(() => ids.Add(2, 0, [], Token(2)), 7);
            Error(() => ids.Add(2, 0, new byte[21], Token(2)), 7);
            Error(() => ids.Add(2, 0, Id(2), new byte[15]), 7);
            Error(() => ids.Add(2, 0, Id(2), new byte[17]), 7);
        }

        private static void LongLifetime()
        {
            Delivery? last = null;
            var count = 0;
            using var ids = new PeerConnectionIds(Id(0), value =>
            {
                count++;
                last = new Delivery(2, Wire.Frame(0x19, value));
                return last;
            });

            for (ulong sequence = 1; sequence <= 10000; sequence++)
            {
                ids.Add(sequence, sequence, Id(sequence), Token(sequence));
                Check(ids.ActiveCount == 1 && ids.RetainedRetirements == 1, "bounded rotation state");

                if (last == null) throw new InvalidOperationException("Retirement was not queued.");

                last.Acknowledged = true;
                ids.Prune();
            }

            Check(count == 10000 && ids.RetainedRetirements == 0, "long-lived retirement accounting");
        }

        private static void PacketRotation()
        {
            byte[] original = [1, 2, 3, 4, 5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12, 13, 14, 15, 16];
            using var keys = PacketKeys.Initial(original, false);
            var first = NativeFuzz.Initial(keys, original, client, 0, [1]);
            using var connection = new QuicConnection(
                first,
                static _ => new ConnectionCoreTests.ModelTls(true),
                static (port, _) => new StreamEchoApplication(port)
            );
            connection.Receive(first, 0);
            connection.Tick(100);
            byte[] replacement = [91, 92, 93];
            using var frame = new MemoryStream();
            frame.Write(Wire.Frame(2, 0, 0, 0, 0));
            frame.Write(Wire.Frame(0x18, 1, 1));
            frame.WriteByte((byte)replacement.Length);
            frame.Write(replacement);
            frame.Write(Token(1));
            var output = connection.Receive(
                ConnectionCoreTests.ApplicationPacket(connection.ServerId, frame.ToArray()),
                101
            );
            Check(
                output.Count != 0
                    && output.All(
                        bytes => bytes[0] < 128
                            && bytes
                                .AsSpan(1, replacement.Length)
                                .SequenceEqual(replacement)
                    ),
                "wire destination changes"
            );
            using var write = new PacketKeys(new byte[32]);
            Check(output.Any(bytes => write
                    .Unprotect(bytes, 1 + replacement.Length, 0, out _)
                    .AsSpan()
                    .StartsWith(Wire.Frame(0x19, 0))), "retirement is sent through recovery");
            var repeated = connection.Tick(2000);
            Check(repeated.Any(bytes => write
                    .Unprotect(bytes, 1 + replacement.Length, 0, out _)
                    .AsSpan()
                    .StartsWith(Wire.Frame(0x19, 0))), "lost retirement retransmits");
        }

        internal static void Mutate(ReadOnlySpan<byte> input)
        {
            var deliveries = new List<Delivery>();
            using var ids = new PeerConnectionIds(Id(0), value =>
            {
                var delivery = new Delivery(2, Wire.Frame(0x19, value));
                deliveries.Add(delivery);
                return delivery;
            });

            for (var index = 0; index + 2 < Math.Min(input.Length, 192); index += 3)
            {
                var sequence = (ulong)input[index];
                var prior = (ulong)input[index + 1];
                var kind = input[index + 2];

                try
                {
                    ids.Add(sequence, prior, Id(kind), Token(kind));
                }

                catch (TransportException error)
                {
                    Check(error.ErrorCode is 7 or 9 or 10, "mutation error classification");
                    break;
                }

                if ((kind & 1) != 0)
                {
                    foreach (var delivery in deliveries) delivery.Acknowledged = true;

                    ids.Prune();
                    deliveries.Clear();
                }

                Check(ids.ActiveCount is >= 1 and <= 2 && ids.RetainedRetirements <= 4, "mutation bounds");
            }

            Check(ids.ActiveCount <= 2 && ids.RetainedRetirements <= 4, "failure bounds");
            ids.Dispose();
            ids.Dispose();
            Check(ids.ActiveCount == 0 && ids.RetainedRetirements == 0, "mutation retirement");
        }

        private static void Error(Action action, ulong expected)
        {
            try
            {
                action();
                throw new InvalidOperationException("Invalid peer ID was accepted.");
            }

            catch (TransportException error)
            {
                Check(error.ErrorCode == expected, "protocol error classification");
            }
        }

        private static void Check(bool passed, string name) =>
            QuicFuzzChecks.Require(passed, "peer-connection-id-" + name);
    }
}
