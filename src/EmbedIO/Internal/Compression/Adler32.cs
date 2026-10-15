using System;
#if NET10_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace EmbedIO.Internal
{
    internal static class Adler32
    {
#if NET10_0_OR_GREATER
        private static readonly Vector128<ushort> LowerWeights = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
        private static readonly Vector128<ushort> UpperWeights = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
#endif
        internal static uint Update(uint checksum, byte[] bytes, int offset, int count)
        {
            if (bytes is null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
#if NET10_0_OR_GREATER
            return Update(checksum, bytes.AsSpan(offset, count));
#else
            var low = checksum & 65535;
            var high = checksum >> 16;
            var limit = offset + count;
            for (var index = offset; index < limit;)
            {
                var end = index + Math.Min(limit - index, 5552);
                while (index < end) { low += bytes[index++]; high += low; }
                low %= 65521;
                high %= 65521;
            }
            return (high << 16) | low;
#endif
        }
#if NET10_0_OR_GREATER
        internal static uint Update(uint checksum, ReadOnlySpan<byte> bytes)
        {
            var low = checksum & 65535;
            var high = checksum >> 16;
            for (var index = 0; index < bytes.Length;)
            {
                // 5552 bounds both accumulators for the worst-case all-255 input.
                var end = index + Math.Min(bytes.Length - index, 5552);
                if (Vector128.IsHardwareAccelerated)
                {
                    while (index <= end - 16)
                    {
                        var values = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(bytes), (nuint)index);
                        var lower = Vector128.WidenLower(values);
                        var upper = Vector128.WidenUpper(values);
                        // Each half's ushort weighted sum is at most 25500/9180.
                        var weighted = (uint)Vector128.Sum(Vector128.Multiply(lower, LowerWeights))
                            + Vector128.Sum(Vector128.Multiply(upper, UpperWeights));
                        high += 16 * low + weighted;
                        low += (uint)Vector128.Sum(lower) + Vector128.Sum(upper);
                        index += 16;
                    }
                }
                while (index < end) { low += bytes[index++]; high += low; }
                low %= 65521;
                high %= 65521;
            }
            return (high << 16) | low;
        }
#endif
    }
}
