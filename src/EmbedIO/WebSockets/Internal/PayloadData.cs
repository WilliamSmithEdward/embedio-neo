using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EmbedIO.Internal;

namespace EmbedIO.WebSockets.Internal
{
    internal class PayloadData
    {
        public const ulong MaxLength = long.MaxValue;

        private readonly byte[] _data;
        private ushort? _code;

        internal PayloadData(byte[] data)
        {
            _data = data;
        }

        internal PayloadData(ushort code = 1005, string? reason = null)
        {
            _code = code;
            _data = code == 1005 ? Array.Empty<byte>() : Append(code, reason);
        }

        internal MemoryStream ApplicationData => new MemoryStream(_data);

        internal ulong Length => (ulong)_data.Length;

        internal ushort Code
        {
            get
            {
                if (!_code.HasValue)
                {
                    _code = _data.Length > 1
                            ? (ushort)((_data[0] << 8) | _data[1])
                            : (ushort)1005;
                }

                return _code.Value;
            }
        }

        internal bool HasReservedCode => _data.Length > 1 && (Code == (ushort)CloseStatusCode.Undefined ||
                   Code == (ushort)CloseStatusCode.NoStatus ||
                   Code == (ushort)CloseStatusCode.Abnormal ||
                   Code == (ushort)CloseStatusCode.TlsHandshakeFailure);

        public override string ToString() => BitConverter.ToString(_data);

        internal static byte[] Append(ushort code, string? reason)
        {
            var length = string.IsNullOrEmpty(reason) ? 0 : Encoding.UTF8.GetByteCount(reason);
            var result = new byte[2 + length];
            result[0] = (byte)(code >> 8);
            result[1] = (byte)code;
            if (length > 0 && reason != null)
                Encoding.UTF8.GetBytes(reason, 0, reason.Length, result, 2);
            return result;
        }

        internal void Mask(byte[] key) => Mask(_data, key);

        // XORs data in place with the repeating four-byte key, eight bytes at a time.
        internal static void Mask(byte[] data, byte[] key)
        {
            var bytes = data.AsSpan();
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            if (words.Length > 0)
            {
                Span<byte> pattern = stackalloc byte[8];
                for (var i = 0; i < 8; i++) pattern[i] = key[i & 3];
                var mask = MemoryMarshal.Read<ulong>(pattern);
                for (var i = 0; i < words.Length; i++) words[i] ^= mask;
            }
            // Whole words cover a multiple of four bytes, so the key phase restarts at zero.
            for (var i = words.Length * 8; i < bytes.Length; i++)
                bytes[i] = (byte)(bytes[i] ^ key[i & 3]);
        }

        internal byte[] ToArray() => _data;
    }
}
