using System;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    /// <summary>
    /// Represents some System.NET custom extensions.
    /// </summary>
    internal static class NetExtensions
    {
        internal static byte[] ToByteArray(this ushort value, Endianness order)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!order.IsHostOrder())
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        internal static byte[] ToByteArray(this ulong value, Endianness order)
        {
            var bytes = BitConverter.GetBytes(value);
            if (!order.IsHostOrder())
            {
                Array.Reverse(bytes);
            }

            return bytes;
        }

        // true: !(true ^ true) or !(false ^ false)
        // false: !(true ^ false) or !(false ^ true)
        private static bool IsHostOrder(this Endianness order)
            => !(BitConverter.IsLittleEndian ^ (order == Endianness.Little));
    }
}
