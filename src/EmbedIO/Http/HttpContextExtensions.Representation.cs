using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    public static partial class HttpContextExtensions
    {
        /// <summary>Streams a selected seekable representation with preconditions and bounded byte ranges.</summary>
        /// <param name="context">The HTTP context.</param>
        /// <param name="content">The entire selected representation, starting at byte zero.</param>
        /// <param name="contentType">Its media type.</param>
        /// <param name="entityTag">Its entity tag, or null.</param>
        /// <param name="lastModified">Its modification instant, or null.</param>
        /// <param name="leaveOpen">Whether the caller retains ownership of the source.</param>
        /// <param name="lastModifiedIsStrong">Whether the date is known to be a strong validator.</param>
        /// <param name="maximumRanges">Maximum received range specifications; excess causes Range to be ignored.</param>
        /// <returns>A task that completes when response writing finishes.</returns>
        /// <remarks>Call after authorization and representation selection, including QUERY content and negotiation.
        /// The source must be readable/seekable and must not change or be shared while sending. Its position changes.
        /// Set content-coding metadata yourself for already encoded content. No automatic compression is applied.
        /// Source ownership is accepted after argument validation; owned sources close on success, cancellation or failure.</remarks>
        public static async Task SendRepresentationAsync(this IHttpContext context, Stream content, string contentType,
            string? entityTag = null, DateTimeOffset? lastModified = null, bool leaveOpen = false,
            bool lastModifiedIsStrong = false, int maximumRanges = 16)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));
            if (content is null) throw new ArgumentNullException(nameof(content));
            var mediaType = ValidateRepresentation(content, contentType, maximumRanges);
            try
            {
                var token = context.CancellationToken;
                token.ThrowIfCancellationRequested();
                var total = content.Length;
                if (total < 0) throw new IOException("Negative representation length.");
                var response = context.Response;
                var condition = context.Request.EvaluatePreconditions(entityTag, lastModified);
                response.Headers[HttpHeaderNames.AcceptRanges] = "bytes";
                if (entityTag != null) response.Headers[HttpHeaderNames.ETag] = entityTag;
                if (lastModified.HasValue) response.Headers[HttpHeaderNames.LastModified] = lastModified.GetValueOrDefault().ToUniversalTime().ToString("r", CultureInfo.InvariantCulture);
                if (condition.HasValue)
                {
                    SendPreconditionResult(response, condition.GetValueOrDefault());
                    return;
                }

                var ranges = SelectRanges(context.Request, response, total, entityTag, lastModified, lastModifiedIsStrong, maximumRanges);
                MultipartFraming? multipart = null;
                if (ranges != null && ranges.Length > 1)
                {
                    multipart = MultipartFraming.TryCreate(ranges, mediaType, total);
                    // Framing whose length overflows sends the entire representation instead.
                    if (!multipart.HasValue) ranges = null;
                }

                var transmitted = total;
                if (multipart.HasValue) transmitted = multipart.GetValueOrDefault().Length;
                else if (ranges != null) transmitted = ranges[0].Length;
                response.StatusCode = ranges is null ? 200 : 206;
                response.ContentType = multipart.HasValue ? "multipart/byteranges; boundary=" + multipart.GetValueOrDefault().Boundary : mediaType;
                response.SendChunked = false;
                response.Headers.Remove(HttpHeaderNames.ContentRange);
                response.ContentLength64 = transmitted;
                if (ranges != null && ranges.Length == 1)
                    response.Headers[HttpHeaderNames.ContentRange] = FormattableString.Invariant($"bytes {ranges[0].Start}-{ranges[0].End}/{total}");
                if (context.Request.HttpMethod == "HEAD" || transmitted == 0) return;
                var output = response.OutputStream;
                var buffer = ArrayPool<byte>.Shared.Rent(65536);
                try
                {
                    if (ranges is null) await CopyRange(content, output, 0, total, buffer, token).ConfigureAwait(false);
                    else if (!multipart.HasValue) await CopyRange(content, output, ranges[0].Start, ranges[0].Length, buffer, token).ConfigureAwait(false);
                    else await CopyParts(content, output, ranges, multipart.GetValueOrDefault(), buffer, token).ConfigureAwait(false);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, true); }
            }
            finally { if (!leaveOpen) content.Dispose(); }
        }

        // Validates the representation arguments before ownership of the source is accepted
        // and returns the media type as sent in the response and in multipart part headers.
        private static string ValidateRepresentation(Stream content, string contentType, int maximumRanges)
        {
            if (!content.CanRead || !content.CanSeek) throw new ArgumentException("A readable seekable representation is required.", nameof(content));
            if (maximumRanges < 1 || maximumRanges > 128) throw new ArgumentOutOfRangeException(nameof(maximumRanges));
            if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed is null || parsed.MediaType is null
                || EmbedIO.Internal.StringOperations.IndexOfOrdinal(parsed.MediaType, '*') >= 0) throw new ArgumentException("Invalid representation media type.", nameof(contentType));
            var mediaType = parsed.ToString();
            foreach (var character in mediaType)
                if (character < 32 || character > 126) throw new ArgumentException("Multipart representation metadata must be printable ASCII.", nameof(contentType));
            return mediaType;
        }

        // A precondition result ends the response without a body and without range framing.
        private static void SendPreconditionResult(IHttpResponse response, HttpStatusCode status)
        {
            response.StatusCode = (int)status;
            response.SendChunked = false;
            response.Headers.Remove(HttpHeaderNames.ContentRange);
            if (response.StatusCode == 304)
            {
                response.Headers.Remove(HttpHeaderNames.ContentLength);
            }
            else
            {
                response.ContentLength64 = 0;
                response.Headers.Remove(HttpHeaderNames.ContentEncoding);
            }
        }

        // The application's error renderer sends different content for 416, so the
        // representation's content coding must not stay on the response.
        private static HttpRequestExtensions.SelectedByteRange[]? SelectRanges(IHttpRequest request, IHttpResponse response, long total,
            string? entityTag, DateTimeOffset? lastModified, bool lastModifiedIsStrong, int maximumRanges)
        {
            try
            {
                return HttpRequestExtensions.SelectByteRanges(request, total, entityTag, lastModified, lastModifiedIsStrong, maximumRanges);
            }
            catch (HttpRangeNotSatisfiableException)
            {
                response.Headers.Remove(HttpHeaderNames.ContentEncoding);
                throw;
            }
        }

        private static async Task CopyParts(Stream content, Stream output, HttpRequestExtensions.SelectedByteRange[] ranges, MultipartFraming framing, byte[] buffer, CancellationToken token)
        {
            var separator = new byte[] { 13, 10 };
            for (var index = 0; index < ranges.Length; index++)
            {
                var header = framing.PartHeaders[index];
                await output.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                await CopyRange(content, output, ranges[index].Start, ranges[index].Length, buffer, token).ConfigureAwait(false);
                await output.WriteAsync(separator, 0, separator.Length, token).ConfigureAwait(false);
            }

            await output.WriteAsync(framing.Closing, 0, framing.Closing.Length, token).ConfigureAwait(false);
        }

        private static async Task CopyRange(Stream source, Stream destination, long start, long count, byte[] buffer, CancellationToken token)
        {
            source.Seek(start, SeekOrigin.Begin);
            while (count != 0)
            {
                var read = await source.ReadAsync(buffer, 0, (int)Math.Min(count, buffer.Length), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Representation changed or ended before its declared length.");
                await destination.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                count -= read;
            }
        }

        // The multipart/byteranges framing of a multi-range response: its boundary, the
        // part headers, the closing delimiter and the declared length of the whole body.
        private readonly struct MultipartFraming
        {
            private MultipartFraming(string boundary, byte[][] partHeaders, byte[] closing, long length)
            {
                Boundary = boundary;
                PartHeaders = partHeaders;
                Closing = closing;
                Length = length;
            }

            internal string Boundary { get; }
            internal byte[][] PartHeaders { get; }
            internal byte[] Closing { get; }
            internal long Length { get; }

            // Returns null when the framed length does not fit in a long.
            internal static MultipartFraming? TryCreate(HttpRequestExtensions.SelectedByteRange[] ranges, string mediaType, long total)
            {
                var boundary = "eio_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                var partHeaders = new byte[ranges.Length][];
                var closing = Encoding.ASCII.GetBytes("--" + boundary + "--\r\n");
                try
                {
                    var length = closing.LongLength;
                    for (var index = 0; index < ranges.Length; index++)
                    {
                        var range = ranges[index];
                        partHeaders[index] = Encoding.ASCII.GetBytes("--" + boundary + "\r\nContent-Type: " + mediaType
                            + "\r\nContent-Range: " + FormattableString.Invariant($"bytes {range.Start}-{range.End}/{total}") + "\r\n\r\n");
                        length = checked(length + partHeaders[index].LongLength + range.Length + 2);
                    }

                    return new MultipartFraming(boundary, partHeaders, closing, length);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }
        }
    }
}
