using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // Test-only facade. Referencing the prototype never creates a socket or loads OpenSSL.
    public static class QuicFuzzChecks
    {
        public const int TargetCount = 7;
        public const int MaximumInput = 4096;

        public static void Check(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;

            CheckTarget(data[0] % TargetCount, data[1..]);
        }

        public static void CheckTarget(int target, ReadOnlySpan<byte> data)
        {
            data = data[..Math.Min(data.Length, MaximumInput)];

            switch (target)
            {
                case 0:
                    Integers(data);
                    break;

                case 1:
                    Protection(data);
                    break;

                case 2:
                    StreamModel.Check(data);
                    break;

                case 3:
                    QuicFuzzState.Run(data, false, null);
                    break;

                case 4:
                    QuicFuzzState.Run(data, true, null);
                    break;

                case 5:
                    BridgeFuzz.Check(data);
                    break;

                case 6:
                    HeadersAndAcks(data);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(target));
            }
        }

        public static void StateMachine(ReadOnlySpan<byte> steps, Action<string>? cover = null) =>
            QuicFuzzState.Run(steps[..Math.Min(steps.Length, MaximumInput)], false, cover);

        public static void Lifecycle(ReadOnlySpan<byte> steps, Action<string>? cover = null) =>
            QuicFuzzState.Run(steps[..Math.Min(steps.Length, MaximumInput)], true, cover);

        internal static void Require(bool condition, string invariant)
        {
            if (!condition) throw new QuicFuzzFailure(invariant);
        }

        public static void SelfTest()
        {
            // Verify that the same authentication oracle used by mutations rejects
            // an intact packet when it is falsely presented as an invalid packet.
            using var keys = new PacketKeys(new byte[32]);
            var header = new byte[] { 0x43, 0, 0, 0, 0 };
            var packet = keys.Protect(header, 1, 0, new byte[4]);

            try
            {
                RejectPacket(keys, packet, 1, 0, false, "negative-authentication-oracle");
            }

            catch (QuicFuzzFailure failure) when (failure.Invariant == "negative-authentication-oracle")
            {
                StreamModel.SelfTest();
                return;
            }

            throw new InvalidOperationException("Authentication oracle accepted injected corruption.");
        }

        private static void Integers(ReadOnlySpan<byte> data)
        {
            var reader = new WireReader(data);
            var cursor = 0;

            while (cursor < data.Length)
            {
                var size = 1 << (data[cursor] >> 6);

                if (size > data.Length - cursor)
                {
                    try
                    {
                        reader.Integer();
                    }

                    catch (InvalidDataException)
                    {
                        return;
                    }

                    throw new QuicFuzzFailure("truncated-integer");
                }

                var expected = (ulong)(data[cursor] & 63);

                for (var index = 1; index < size; index++) expected = expected * 256 + data[cursor + index];

                Require(reader.Integer() == expected, "integer-value");
                var encoded = Wire.Frame(expected);
                var canonical = expected < 64 ? 1 : expected < 16384 ? 2 : expected < 1073741824 ? 4 : 8;
                Require(encoded.Length == canonical, "integer-canonical-size");
                var replay = new WireReader(encoded);
                Require(replay.Integer() == expected && replay.Remaining == 0, "integer-roundtrip");
                cursor += size;
                Require(reader.Remaining == data.Length - cursor, "integer-cursor");
            }
        }

        private static void Protection(ReadOnlySpan<byte> data)
        {
            var secret = SHA256.HashData(data);
            using var keys = new PacketKeys(secret);
            using var next = keys.Next();
            var number = BinaryPrimitives.ReadUInt32BigEndian(secret);
            var header = new byte[13];
            header[0] = 0x43;
            secret
                .AsSpan(4, 8)
                .CopyTo(header.AsSpan(1));
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), number);
            var packet = keys.Protect(header, 9, number, data);
            Require(
                keys
                    .Unprotect(packet, 9, number, out var actual)
                    .AsSpan()
                    .SequenceEqual(data)
                    && actual == number,
                "packet-roundtrip"
            );
            packet[^1] ^= 1;
            RejectPacket(keys, packet, 9, number, false, "packet-authentication");
            header[0] |= 4;
            var updated = next.Protect(header, 9, number, data);
            Require(keys.Phase(updated, 9), "key-update-header-key");
            Require(
                next
                    .Unprotect(updated, 9, number, out actual, true)
                    .AsSpan()
                    .SequenceEqual(data)
                    && actual == number,
                "key-update-roundtrip"
            );
            RejectPacket(keys, updated, 9, number, true, "key-update-old-key");

            try
            {
                keys.Unprotect(data, data.Length == 0 ? 0 : data[0] % 21, number, out _);
            }

            catch (InvalidDataException) { }

            catch (CryptographicException) { }
        }

        private static void RejectPacket(
            PacketKeys keys,
            byte[] packet,
            int offset,
            ulong number,
            bool phase,
            string invariant
        )
        {
            try
            {
                keys.Unprotect(packet, offset, number, out _, phase);
            }

            catch (CryptographicException)
            {
                return;
            }

            catch (InvalidDataException)
            {
                return;
            }

            throw new QuicFuzzFailure(invariant);
        }

        private static void HeadersAndAcks(ReadOnlySpan<byte> data)
        {
            PacketNumberWindowTests.Mutate(data);

            if (PacketHeader.TryDestination(data, out var destination, out var initial))
            {
                Require(destination.Length <= 20 && destination.Length <= data.Length, "destination-bounds");
                Require(initial == ((data[0] & 0xf0) == 0xc0), "destination-packet-type");
            }

            byte[] original = [1, 2, 3, 4];
            byte[] server = [5, 6, 7, 8];
            byte[] client = [9, 10, 11, 12];

            if (PacketHeader.TryRead(data, original, server, client, out var level, out var length, out var offset))
                Require(
                    level is >= 0 and <= 2 && offset >= 1 && offset + 20 <= length && length <= data.Length,
                    "packet-header-bounds"
                );

            using var wire = new MemoryStream();
            wire.Write([0xc3, 0, 0, 0, 1, 4, 1, 2, 3, 4, 4, 9, 10, 11, 12]);
            Wire.Integer(wire, (ulong)data.Length);
            wire.Write(data);
            Wire.Integer(wire, 24);
            wire.Write(new byte[24]);
            Require(
                PacketHeader.TryRead(wire.ToArray(), original, server, client, out level, out length, out offset)
                    && level == 0
                    && length == wire.Length
                    && offset + 24 == length,
                "initial-header-token"
            );
            Require(
                PacketHeader.TryDestination(wire.ToArray(), out destination, out initial)
                    && initial
                    && destination.SequenceEqual(original),
                "initial-destination"
            );
            byte[] shortPacket = [0x40, 1, 2, 3, 4, 5, 6, 7, 8];
            Require(
                PacketHeader.TryDestination(shortPacket, out destination, out initial)
                    && !initial
                    && destination.SequenceEqual(shortPacket.AsSpan(1)),
                "short-destination"
            );
            var reader = new WireReader(data);

            try
            {
                var ranges = AckRanges.Read(ref reader, 4096, 3, out var delay);
                Require(ranges.Count is > 0 and <= 129 && double.IsFinite(delay) && delay >= 0, "ack-bounds");
                ulong previous = 4096;

                foreach (var (low, high) in ranges)
                {
                    Require(low <= high && high < previous, "ack-range-order");
                    previous = low;
                }
            }

            catch (InvalidDataException) { }
        }
    }

    public sealed class QuicFuzzFailure(string invariant) : Exception("QUIC invariant: " + invariant)
    {
        public string Invariant { get; } = invariant;
    }
}
