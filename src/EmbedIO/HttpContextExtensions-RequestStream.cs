using System.IO;
using System.IO.Compression;
using System.Text;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO
{
    partial class HttpContextExtensions
    {
        /// <summary>
        /// <para>Wraps the request input stream and returns a <see cref="Stream"/> that can be used directly.</para>
        /// <para>Decompression of compressed request bodies is implemented if specified in the web server's options.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpContext"/> on which this method is called.</param>
        /// <returns>
        /// <para>A <see cref="Stream"/> that can be used to read request data.</para>
        /// <para>This stream MUST be disposed when finished writing.</para>
        /// </returns>
        /// <seealso cref="OpenRequestText"/>
        /// <seealso cref="WebServerOptionsBase.SupportCompressedRequests"/>
        public static Stream OpenRequestStream(this IHttpContext @this)
        {
            if (@this is null) throw new System.NullReferenceException();
            var stream = @this.Request.InputStream ?? Stream.Null;

            var encoding = @this.Request.Headers[HttpHeaderNames.ContentEncoding];
            if (encoding != null) encoding = RequestCodingChain.TrimValue(encoding);
            if (encoding == null || encoding.Length == 0 || encoding.Equals(CompressionMethodNames.None, System.StringComparison.OrdinalIgnoreCase))
                return stream;
            if (encoding.IndexOf(",", System.StringComparison.Ordinal) >= 0 && RequestCodingChain.TryOpen(stream, encoding, @this.SupportCompressedRequests, out var decoded))
                return decoded == stream ? stream : RequestDecompressionPolicy.Apply(@this, decoded ?? throw new System.InvalidOperationException("Missing decoded request stream."));
            if (@this.SupportCompressedRequests)
            {
                if (encoding.Equals(CompressionMethodNames.Gzip, System.StringComparison.OrdinalIgnoreCase))
                    return RequestDecompressionPolicy.Apply(@this, new GZipStream(stream, CompressionMode.Decompress));
                if (encoding.Equals(CompressionMethodNames.Deflate, System.StringComparison.OrdinalIgnoreCase))
                    return RequestDecompressionPolicy.Apply(@this, new DeflateStream(stream, CompressionMode.Decompress));
#if NET10_0_OR_GREATER
                if (encoding.Equals(CompressionMethodNames.Brotli, System.StringComparison.OrdinalIgnoreCase))
                    return RequestDecompressionPolicy.Apply(@this, new BrotliRequestStream(stream));
#endif
            }

            $"[{@this.Id}] Unsupported request content encoding \"{encoding}\", sending 400 Bad Request..."
                .Warn(nameof(OpenRequestStream));

            throw HttpException.BadRequest($"Unsupported content encoding \"{encoding}\"");
        }

        /// <summary>
        /// <para>Wraps the request input stream and returns a <see cref="TextReader" /> that can be used directly.</para>
        /// <para>Decompression of compressed request bodies is implemented if specified in the web server's options.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpContext" /> on which this method is called.</param>
        /// <returns>
        /// <para>A <see cref="TextReader" /> that can be used to read the request body as text.</para>
        /// <para>This reader MUST be disposed when finished reading.</para>
        /// </returns>
        /// <seealso cref="OpenRequestStream"/>
        /// <seealso cref="WebServerOptionsBase.SupportCompressedRequests"/>
        public static TextReader OpenRequestText(this IHttpContext @this)
        {
            if (@this is null) throw new System.NullReferenceException();
            return new StreamReader(OpenRequestStream(@this), @this.Request.ContentEncoding);
        }
    }
}
