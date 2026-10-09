using System;
using System.Buffers;
using System.Globalization;
using System.IO;
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
            if (!content.CanRead || !content.CanSeek) throw new ArgumentException("A readable seekable representation is required.", nameof(content));
            if (maximumRanges < 1 || maximumRanges > 128) throw new ArgumentOutOfRangeException(nameof(maximumRanges));
            if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed is null || parsed.MediaType is null
                || parsed.MediaType.IndexOf("*", StringComparison.Ordinal) >= 0) throw new ArgumentException("Invalid representation media type.", nameof(contentType));
            var mediaType = parsed.ToString();
            foreach (var character in mediaType)
                if (character < 32 || character > 126) throw new ArgumentException("Multipart representation metadata must be printable ASCII.", nameof(contentType));
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
                    response.StatusCode = (int)condition.GetValueOrDefault();
                    response.SendChunked = false;
                    response.Headers.Remove(HttpHeaderNames.ContentRange);
                    if (response.StatusCode == 304) response.Headers.Remove(HttpHeaderNames.ContentLength);
                    else response.ContentLength64 = 0;
                    if (response.StatusCode != 304) response.Headers.Remove(HttpHeaderNames.ContentEncoding);
                    return;
                }
                HttpRequestExtensions.SelectedByteRange[]? ranges;
                try { ranges = HttpRequestExtensions.SelectByteRanges(context.Request, total, entityTag, lastModified, lastModifiedIsStrong, maximumRanges); }
                catch (HttpRangeNotSatisfiableException)
                {
                    // The application's error renderer will send different content.
                    response.Headers.Remove(HttpHeaderNames.ContentEncoding);
                    throw;
                }
                byte[][]? headers = null;
                byte[]? closing = null;
                string? boundary = null;
                var transmitted = total;
                if (ranges != null && ranges.Length > 1)
                {
                    boundary = "eio_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                    headers = new byte[ranges.Length][];
                    closing = Encoding.ASCII.GetBytes("--" + boundary + "--\r\n");
                    try
                    {
                        transmitted = closing.LongLength;
                        for (var index = 0; index < ranges.Length; index++)
                        {
                            var range = ranges[index];
                            headers[index] = Encoding.ASCII.GetBytes("--" + boundary + "\r\nContent-Type: " + mediaType
                                + "\r\nContent-Range: " + FormattableString.Invariant($"bytes {range.Start}-{range.End}/{total}") + "\r\n\r\n");
                            transmitted = checked(transmitted + headers[index].LongLength + range.Length + 2);
                        }
                    }
                    catch (OverflowException) { ranges = null; headers = null; closing = null; boundary = null; transmitted = total; }
                }
                else if (ranges != null) transmitted = ranges[0].Length;
                response.StatusCode = ranges is null ? 200 : 206;
                response.ContentType = boundary is null ? mediaType : "multipart/byteranges; boundary=" + boundary;
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
                    else if (ranges.Length == 1) await CopyRange(content, output, ranges[0].Start, ranges[0].Length, buffer, token).ConfigureAwait(false);
                    else
                    {
                        var partHeaders = headers ?? throw new InvalidOperationException("Missing multipart headers.");
                        var trailer = closing ?? throw new InvalidOperationException("Missing multipart closing boundary.");
                        var separator = new byte[] { 13, 10 };
                        for (var index = 0; index < ranges.Length; index++)
                        {
                            await output.WriteAsync(partHeaders[index], 0, partHeaders[index].Length, token).ConfigureAwait(false);
                            await CopyRange(content, output, ranges[index].Start, ranges[index].Length, buffer, token).ConfigureAwait(false);
                            await output.WriteAsync(separator, 0, separator.Length, token).ConfigureAwait(false);
                        }
                        await output.WriteAsync(trailer, 0, trailer.Length, token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, true); }
            }
            finally { if (!leaveOpen) content.Dispose(); }
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
    }
}
