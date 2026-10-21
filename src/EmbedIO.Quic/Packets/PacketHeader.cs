using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace EmbedIO.Quic
{
    internal static class PacketHeader
    {
        internal static bool TryDestination(
            ReadOnlySpan<byte> packet,
            out ReadOnlySpan<byte> destination,
            out bool initial
        )
        {
            destination = default;
            initial = false;

            if (packet.Length < 9 || (packet[0] & 64) == 0) return false;

            if ((packet[0] & 128) == 0)
            {
                destination = packet.Slice(1, 8);
                return true;
            }

            if (BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(1, 4)) != 1) return false;

            var length = packet[5];

            if (length > 20 || packet.Length < 6 + length) return false;

            destination = packet.Slice(6, length);
            initial = (packet[0] & 48) == 0;
            return true;
        }

        internal static bool TryRead(
            ReadOnlySpan<byte> packet,
            ReadOnlySpan<byte> originalId,
            ReadOnlySpan<byte> serverId,
            ReadOnlySpan<byte> clientId,
            out int level,
            out int length,
            out int numberOffset
        )
        {
            level = 0;
            length = 0;
            numberOffset = 0;

            try
            {
                var reader = new WireReader(packet);
                var first = reader.Byte();

                if ((first & 64) == 0) return false;

                if ((first & 128) != 0)
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(reader.Take(4)) != 1) return false;

                    var type = (first >> 4) & 3;

                    if (type is not (0 or 2)) return false;

                    level = type == 0 ? 0 : 1;
                    var destination = reader.Take(reader.Byte());
                    var source = reader.Take(reader.Byte());

                    if (
                        (!destination.SequenceEqual(originalId) && !destination.SequenceEqual(serverId))
                        || !source.SequenceEqual(clientId)
                    ) return false;

                    if (level == 0) reader.Take(reader.Length(4096));

                    var payloadLength = reader.Length(packet.Length);
                    numberOffset = packet.Length - reader.Remaining;

                    if (payloadLength > reader.Remaining || payloadLength < 20) return false;

                    length = numberOffset + payloadLength;
                }

                else
                {
                    level = 2;

                    if (
                        !reader
                            .Take(serverId.Length)
                            .SequenceEqual(serverId)
                    ) return false;

                    numberOffset = packet.Length - reader.Remaining;
                    length = packet.Length;
                }

                return numberOffset + 20 <= length;
            }

            catch (InvalidDataException)
            {
                return false;
            }
        }
    }
}
