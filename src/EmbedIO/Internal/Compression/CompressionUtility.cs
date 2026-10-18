using System.IO;
using System.IO.Compression;

namespace EmbedIO.Internal
{
    internal static class CompressionUtility
    {
        public static byte[]? ConvertCompression(
            byte[] source,
            CompressionMethod sourceMethod,
            CompressionMethod targetMethod
        )
        {
            if (source == null)
                return null;
#if !NET10_0_OR_GREATER
            if (sourceMethod == CompressionMethod.Brotli || targetMethod == CompressionMethod.Brotli)
                throw new System.NotSupportedException("Brotli conversion requires the .NET 10 asset.");
#endif
            // Preserve identity, including unknown values historically treated as None.
            if (sourceMethod == targetMethod || !IsCompressed(sourceMethod) && !IsCompressed(targetMethod))
                return source;

            using var input = new MemoryStream(source, false);
            using var decoded = sourceMethod switch
            {
#if NET10_0_OR_GREATER
                CompressionMethod.Brotli => (Stream)new BrotliStream(input, CompressionMode.Decompress, true),
#endif
                CompressionMethod.Gzip => (Stream)new GZipStream(input, CompressionMode.Decompress, true),
                CompressionMethod.Deflate => new DeflateStream(input, CompressionMode.Decompress, true),
                _ => input
            };
            using var output = new MemoryStream();

            using (Stream encoded = targetMethod switch
            {
#if NET10_0_OR_GREATER
                CompressionMethod.Brotli => new BrotliStream(output, CompressionMode.Compress, true),
#endif
                CompressionMethod.Gzip => new GZipStream(output, CompressionMode.Compress, true),
                CompressionMethod.Deflate => new DeflateStream(output, CompressionMode.Compress, true),
                _ => output
            }) decoded.CopyTo(encoded);

            return output.ToArray();
        }

        private static bool IsCompressed(CompressionMethod method) =>
            method is CompressionMethod.Deflate or CompressionMethod.Gzip or CompressionMethod.Brotli;
    }
}
