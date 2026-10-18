using System;
using System.Text;

namespace EmbedIO.Files.Internal
{
    internal static class EntityTag
    {
        // A precompressed file's tag: both files' identities, separated so it cannot equal any Compute result.
        public static string ComputeVariant(
            DateTime sourceModifiedUtc,
            long sourceLength,
            DateTime variantModifiedUtc,
            long variantLength,
            CompressionMethod compressionMethod
        )
        {
            var name = compressionMethod == CompressionMethod.Brotli
                ? CompressionMethodNames.Brotli
                : CompressionMethodNames.Gzip;
            return new StringBuilder()
                .Append('"')
                .Append(Base64Utility.LongToBase64(sourceModifiedUtc.Ticks))
                .Append(Base64Utility.LongToBase64(sourceLength))
                .Append('.')
                .Append(Base64Utility.LongToBase64(variantModifiedUtc.Ticks))
                .Append(Base64Utility.LongToBase64(variantLength))
                .Append('-')
                .Append(name)
                .Append("-file")
                .Append('"')
                .ToString();
        }

        public static string Compute(DateTime lastModifiedUtc, long length, CompressionMethod compressionMethod)
        {
            var sb = new StringBuilder()
                .Append('"')
                .Append(Base64Utility.LongToBase64(lastModifiedUtc.Ticks))
                .Append(Base64Utility.LongToBase64(length));

            switch (compressionMethod)
            {
                case CompressionMethod.Deflate:
                    sb
                        .Append('-')
                        .Append(CompressionMethodNames.Deflate);
                    break;

                case CompressionMethod.Brotli:
                    sb
                        .Append('-')
                        .Append(CompressionMethodNames.Brotli);
                    break;

                case CompressionMethod.Gzip:
                    sb
                        .Append('-')
                        .Append(CompressionMethodNames.Gzip);
                    break;
            }

            return sb
                .Append('"')
                .ToString();
        }
    }
}
