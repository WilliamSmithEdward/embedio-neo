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
            using var output = new MemoryStream();
            var buffer = new byte[Math.Min(count, bufferSize)];
            while (output.Length < count)
            {
                var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, count - (int)output.Length)).ConfigureAwait(false);
                if (read == 0) break;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
    }
}
