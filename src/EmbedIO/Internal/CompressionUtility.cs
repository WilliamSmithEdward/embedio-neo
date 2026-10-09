using System.IO;
using System.IO.Compression;

namespace EmbedIO.Internal
{
    internal static class CompressionUtility
    {
        public static byte[]? ConvertCompression(byte[] source, CompressionMethod sourceMethod, CompressionMethod targetMethod)
        {
            if (source == null)
                return null;

            if (sourceMethod == CompressionMethod.Brotli || targetMethod == CompressionMethod.Brotli)
            {
#if NET10_0_OR_GREATER
                return sourceMethod == targetMethod ? source : ConvertBrotli(source, sourceMethod, targetMethod);
#else
                throw new System.NotSupportedException("Brotli conversion requires the .NET 10 asset.");
#endif
            }

            if (sourceMethod == targetMethod)
                return source;

            switch (sourceMethod)
            {
                case CompressionMethod.Deflate:
                    using (var sourceStream = new MemoryStream(source, false))
                    {
                        using var decompressionStream = new DeflateStream(sourceStream, CompressionMode.Decompress, true);
                        using var targetStream = new MemoryStream();
                        if (targetMethod == CompressionMethod.Gzip)
                        {
                            using var compressionStream = new GZipStream(targetStream, CompressionMode.Compress, true);
                            decompressionStream.CopyTo(compressionStream);
                        }
                        else
                        {
                            decompressionStream.CopyTo(targetStream);
                        }

                        return targetStream.ToArray();
                    }

                case CompressionMethod.Gzip:
                    using (var sourceStream = new MemoryStream(source, false))
                    {
                        using var decompressionStream = new GZipStream(sourceStream, CompressionMode.Decompress, true);
                        using var targetStream = new MemoryStream();
                        if (targetMethod == CompressionMethod.Deflate)
                        {
                            using var compressionStream = new DeflateStream(targetStream, CompressionMode.Compress, true);
                            decompressionStream.CopyTo(compressionStream);
                        }
                        else
                        {
                            decompressionStream.CopyTo(targetStream);
                        }

                        return targetStream.ToArray();
                    }

                default:
                    using (var sourceStream = new MemoryStream(source, false))
                    {
                        using var targetStream = new MemoryStream();
                        switch (targetMethod)
                        {
                            case CompressionMethod.Deflate:
                                using (var compressionStream = new DeflateStream(targetStream, CompressionMode.Compress, true))
                                    sourceStream.CopyTo(compressionStream);

                                break;

                            case CompressionMethod.Gzip:
                                using (var compressionStream = new GZipStream(targetStream, CompressionMode.Compress, true))
                                    sourceStream.CopyTo(compressionStream);

                                break;

                            default:
                                // Just in case. Consider all other values as None.
                                return source;
                        }

                        return targetStream.ToArray();
                    }
            }
        }
#if NET10_0_OR_GREATER
        private static byte[] ConvertBrotli(byte[] source, CompressionMethod sourceMethod, CompressionMethod targetMethod)
        {
            using var input = new MemoryStream(source, false);
            using var decoded = sourceMethod switch
            {
                CompressionMethod.Brotli => (Stream)new BrotliStream(input, CompressionMode.Decompress, true),
                CompressionMethod.Gzip => new GZipStream(input, CompressionMode.Decompress, true),
                CompressionMethod.Deflate => new DeflateStream(input, CompressionMode.Decompress, true),
                _ => input
            };
            using var output = new MemoryStream();
            using (Stream encoded = targetMethod switch
            {
                CompressionMethod.Brotli => new BrotliStream(output, CompressionMode.Compress, true),
                CompressionMethod.Gzip => new GZipStream(output, CompressionMode.Compress, true),
                CompressionMethod.Deflate => new DeflateStream(output, CompressionMode.Compress, true),
                _ => output
            }) decoded.CopyTo(encoded);
            return output.ToArray();
        }
#endif
    }
}
