using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    // Original single-block header mask from RFC 9001 section 5.4.4 and
    // RFC 8439 sections 2.1-2.3. Payload encryption uses the BCL AEAD.
    internal static class ChaChaHeaderMask
    {
        internal static void Write(ReadOnlySpan<byte> key, ReadOnlySpan<byte> sample, Span<byte> mask)
        {
            if (key.Length != 32 || sample.Length != 16 || mask.Length < 5)
                throw new ArgumentException("Invalid ChaCha20 header-mask input.");

            Span<uint> original = stackalloc uint[16];
            Span<uint> state = stackalloc uint[16];
            original[0] = 0x61707865;
            original[1] = 0x3320646e;
            original[2] = 0x79622d32;
            original[3] = 0x6b206574;

            for (var index = 0; index < 8; index++)
                original[4 + index] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(4 * index, 4));

            for (var index = 0; index < 4; index++)
                original[12 + index] = BinaryPrimitives.ReadUInt32LittleEndian(sample.Slice(4 * index, 4));

            original.CopyTo(state);

            try
            {
                for (var pair = 0; pair < 10; pair++)
                {
                    for (var column = 0; column < 4; column++)
                        Mix(ref state[column], ref state[column + 4], ref state[column + 8], ref state[column + 12]);

                    for (var diagonal = 0; diagonal < 4; diagonal++)
                        Mix(
                            ref state[diagonal],
                            ref state[4 + ((diagonal + 1) & 3)],
                            ref state[8 + ((diagonal + 2) & 3)],
                            ref state[12 + ((diagonal + 3) & 3)]
                        );
                }

                BinaryPrimitives.WriteUInt32LittleEndian(mask, unchecked(state[0] + original[0]));
                mask[4] = unchecked((byte)(state[1] + original[1]));
            }

            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(original));
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(state));
            }
        }

        private static void Mix(ref uint first, ref uint second, ref uint third, ref uint fourth)
        {
            first = unchecked(first + second);
            fourth = BitOperations.RotateLeft(fourth ^ first, 16);
            third = unchecked(third + fourth);
            second = BitOperations.RotateLeft(second ^ third, 12);
            first = unchecked(first + second);
            fourth = BitOperations.RotateLeft(fourth ^ first, 8);
            third = unchecked(third + fourth);
            second = BitOperations.RotateLeft(second ^ third, 7);
        }
    }
}
