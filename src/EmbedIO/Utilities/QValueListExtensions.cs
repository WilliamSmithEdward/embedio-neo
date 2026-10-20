namespace EmbedIO.Utilities
{
    /// <summary>
    /// Provides extension methods for <see cref="QValueList"/>.
    /// </summary>
    public static class QValueListExtensions
    {
#if NET10_0_OR_GREATER
        // Brotli comes first, so it also wins a tie the client leaves to the server, as with "*".
        private static readonly CompressionMethod[] CompressionFirstMethods = [
            CompressionMethod.Brotli, CompressionMethod.Gzip, CompressionMethod.Deflate, CompressionMethod.None
        ];
        private static readonly string[] CompressionFirstNames = [
            CompressionMethodNames.Brotli, CompressionMethodNames.Gzip, CompressionMethodNames.Deflate,
            CompressionMethodNames.None
        ];
        private static readonly CompressionMethod[] IdentityFirstMethods = [
            CompressionMethod.None, CompressionMethod.Brotli, CompressionMethod.Gzip, CompressionMethod.Deflate
        ];
        private static readonly string[] IdentityFirstNames = [
            CompressionMethodNames.None, CompressionMethodNames.Brotli, CompressionMethodNames.Gzip,
            CompressionMethodNames.Deflate
        ];
#else
        private static readonly CompressionMethod[] CompressionFirstMethods = [
            CompressionMethod.Gzip, CompressionMethod.Deflate, CompressionMethod.None
        ];
        private static readonly string[] CompressionFirstNames = [
            CompressionMethodNames.Gzip, CompressionMethodNames.Deflate, CompressionMethodNames.None
        ];
        private static readonly CompressionMethod[] IdentityFirstMethods = [
            CompressionMethod.None, CompressionMethod.Gzip, CompressionMethod.Deflate
        ];
        private static readonly string[] IdentityFirstNames = [
            CompressionMethodNames.None, CompressionMethodNames.Gzip, CompressionMethodNames.Deflate
        ];
#endif
        /// <summary>
        /// <para>Attempts to proactively negotiate a compression method for a response,
        /// based on the contents of a <see cref="QValueList"/>.</para>
        /// </summary>
        /// <param name="this">The <see cref="QValueList"/> on which this method is called.</param>
        /// <param name="preferCompression"><see langword="true"/> if sending compressed data is preferred over
        /// sending non-compressed data; otherwise, <see langword="false"/>.</param>
        /// <param name="compressionMethod">When this method returns, the compression method to use for the response,
        /// if content negotiation is successful. This parameter is passed uninitialized.</param>
        /// <param name="compressionMethodName">When this method returns, the name of the compression method,
        /// if content negotiation is successful. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if content negotiation is successful;
        /// otherwise, <see langword="false"/>.</returns>
        /// <remarks>
        /// <para>If <paramref name="this"/> is empty, this method always returns <see langword="true"/>,
        /// setting <paramref name="compressionMethod"/> to <see cref="CompressionMethod.None"/>
        /// and <paramref name="compressionMethodName"/> to <see cref="CompressionMethodNames.None"/>.</para>
        /// <para>The coding with the highest weight wins. On the .NET 10 asset, Brotli wins a tie with
        /// gzip or deflate wherever the client lists it; otherwise ties go to the coding the client lists first.</para>
        /// </remarks>
        public static bool TryNegotiateContentEncoding(
            this QValueList @this,
            bool preferCompression,
            out CompressionMethod compressionMethod,
            out string? compressionMethodName
        )
        {
            if (@this is null) throw new System.NullReferenceException();

            if (@this.QValues.Count < 1)
            {
                compressionMethod = CompressionMethod.None;
                compressionMethodName = CompressionMethodNames.None;
                return true;
            }

            // https://tools.ietf.org/html/rfc7231#section-5.3.4
            // RFC7231, Section 5.3.4, rule #2:
            // If the representation has no content-coding, then it is
            // acceptable by default unless specifically excluded by the
            // Accept - Encoding field stating either "identity;q=0" or "*;q=0"
            // without a more specific entry for "identity".
            if (!preferCompression && (!@this.TryGetWeight(CompressionMethodNames.None, out var weight) || weight > 0))
            {
                compressionMethod = CompressionMethod.None;
                compressionMethodName = CompressionMethodNames.None;
                return true;
            }

            var acceptableMethods = preferCompression ? CompressionFirstMethods : IdentityFirstMethods;
            var acceptableMethodNames = preferCompression ? CompressionFirstNames : IdentityFirstNames;

            var acceptableMethodIndex = @this.FindPreferredIndex(acceptableMethodNames);

            if (acceptableMethodIndex < 0)
            {
                // The same rule applies when compression is preferred but no listed coding is acceptable:
                // "Accept-Encoding: br;q=0" refuses Brotli, not the uncompressed representation.
                if (!@this.TryGetWeight(CompressionMethodNames.None, out weight) || weight > 0)
                {
                    compressionMethod = CompressionMethod.None;
                    compressionMethodName = CompressionMethodNames.None;
                    return true;
                }

                compressionMethod = default;
                compressionMethodName = default;
                return false;
            }

            compressionMethod = acceptableMethods[acceptableMethodIndex];
            compressionMethodName = acceptableMethodNames[acceptableMethodIndex];
#if NET10_0_OR_GREATER
            // At equal weight the client's listing order picks above, and browsers list br last
            // ("gzip, deflate, br"). Brotli wins a tie with another coding, as in ASP.NET Core;
            // a tie with identity still goes the way it did.
            if (
                compressionMethod is CompressionMethod.Gzip
                    or CompressionMethod.Deflate
                    && @this.TryGetWeight(CompressionMethodNames.Brotli, out var brotliWeight)
                    && @this.TryGetWeight(compressionMethodName, out var selectedWeight)
                    && brotliWeight == selectedWeight
            )
            {
                compressionMethod = CompressionMethod.Brotli;
                compressionMethodName = CompressionMethodNames.Brotli;
            }
#endif
            return true;
        }
    }
}
