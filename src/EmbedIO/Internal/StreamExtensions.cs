using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    internal static class StreamExtensions
    {
        internal static string ToText(this byte[] bytes) => Encoding.UTF8.GetString(bytes);

        internal static async Task<byte[]> ReadBytesAsync(this Stream stream, int count, int bufferSize = 4096)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            var buffer = new byte[Math.Min(count, bufferSize)];
            if (count <= bufferSize)
            {
                var offset = 0;
                while (offset < count)
                {
                    var read = await stream.ReadAsync(buffer, offset, count - offset).ConfigureAwait(false);
                    if (read == 0) break;
                    offset += read;
                }

                if (offset != count)
                    Array.Resize(ref buffer, offset);
                return buffer;
            }

            using var output = new MemoryStream();
            while (output.Length < count)
            {
                var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, count - (int)output.Length)).ConfigureAwait(false);
                if (read == 0) break;
                var required = checked((int)output.Length + read);
                if (required > output.Capacity)
                {
                    // Grow only after bytes arrive, but never beyond the known
                    // result length. Complete reads then need no final trim/copy.
                    output.Capacity = (int)Math.Min(count,
                        Math.Max(256L, Math.Max(required, (long)output.Capacity * 2)));
                }
                output.Write(buffer, 0, read);
            }
            // Return the owned buffer only when every byte is part of the result.
            return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
        }
    }
}
