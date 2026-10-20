using System;
using System.Net.Http.Headers;

namespace EmbedIO
{
    partial class HttpContextExtensions
    {
        private static readonly object MultipartReaderKey = new();

        /// <summary>Opens a streaming multipart/form-data reader, disposed automatically when the context closes.</summary>
        /// <param name="this">The request context.</param>
        /// <param name="options">Multipart limits, or null for defaults.</param>
        /// <returns>A reader owning the decoded request stream. It may also be disposed explicitly.</returns>
        /// <remarks>Open only once, before any other body reader. The total limit applies after content decompression.
        /// Read until null to validate all data before committing uploaded files. Unsupported media types return 415,
        /// malformed data returns 400, and exceeded limits return 413.</remarks>
        public static MultipartReader OpenMultipartReader(this IHttpContext @this, MultipartOptions? options = null)
        {
            if (@this == null) throw new NullReferenceException();

            if (@this.Items.ContainsKey(MultipartReaderKey))
                throw new InvalidOperationException("A multipart reader has already been opened for this request.");

            if (!MediaTypeHeaderValue.TryParse(@this.Request.ContentType, out var mediaType) || mediaType == null)
                throw HttpException.BadRequest("Invalid multipart content type.");

            if (!string.Equals(mediaType.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
                throw new HttpException(415, "Expected multipart/form-data.");

            string? boundary = null;

            foreach (var parameter in mediaType.Parameters)
            {
                if (!string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase)) continue;

                if (boundary != null) throw HttpException.BadRequest("Duplicate multipart boundary.");

                boundary = MultipartReader.Unquote(parameter.Value)
                    ?? throw HttpException.BadRequest("Missing multipart boundary value.");
            }

            if (boundary == null) throw HttpException.BadRequest("Missing multipart boundary.");

            try
            {
                MultipartReader.ValidateBoundary(boundary);
            }

            catch (ArgumentException)
            {
                throw HttpException.BadRequest("Invalid multipart boundary.");
            }

            var limits = (options ?? new MultipartOptions()).Snapshot();
            var stream = @this.OpenRequestStream();
            MultipartReader? reader = null;

            try
            {
                reader = new MultipartReader(stream, boundary, limits, @this.CancellationToken, true);
                @this.OnClose(_ => reader.Dispose());
                @this.Items.Add(MultipartReaderKey, reader);
                return reader;
            }

            catch
            {
                if (reader == null) stream.Dispose();

                else reader.Dispose();

                throw;
            }
        }
    }
}
