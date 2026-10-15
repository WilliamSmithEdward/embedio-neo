using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using EmbedIO.Utilities;

namespace EmbedIO
{
    /// <summary>
    /// Provides extension methods for types implementing <see cref="IHttpRequest"/>.
    /// </summary>
    public static partial class HttpRequestExtensions
    {
        /// <summary>
        /// <para>Returns a string representing the remote IP address and port of an <see cref="IHttpRequest"/> interface.</para>
        /// <para>This method can be called even on a <see langword="null"/> interface, or one that has no
        /// remote end point, or no remote address; it will always return a non-<see langword="null"/>,
        /// non-empty string.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <returns>
        /// If <paramref name="this"/> is <see langword="null"/>, or its <see cref="IHttpRequest.RemoteEndPoint">RemoteEndPoint</see>
        /// is <see langword="null"/>, the string <c>"&lt;null&gt;</c>; otherwise, the remote end point's
        /// <see cref="IPEndPoint.Address">Address</see> (or the string <c>"&lt;???&gt;"</c> if it is <see langword="null"/>)
        /// followed by a colon and the <see cref="IPEndPoint.Port">Port</see> number.
        /// </returns>
        public static string SafeGetRemoteEndpointStr(this IHttpRequest @this)
        {
            var endPoint = @this?.RemoteEndPoint;
            return endPoint == null
                ? "<null>"
                : $"{endPoint.Address?.ToString() ?? "<???>"}:{endPoint.Port.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// <para>Attempts to proactively negotiate a compression method for a response,
        /// based on a request's <c>Accept-Encoding</c> header (or lack of it).</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <param name="preferCompression"><see langword="true"/> if sending compressed data is preferred over
        /// sending non-compressed data; otherwise, <see langword="false"/>.</param>
        /// <param name="compressionMethod">When this method returns, the compression method to use for the response,
        /// if content negotiation is successful. This parameter is passed uninitialized.</param>
        /// <param name="prepareResponse">When this method returns, a callback that prepares data in an <see cref="IHttpResponse"/>
        /// according to the result of content negotiation. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if content negotiation is successful;
        /// otherwise, <see langword="false"/>.</returns>
        /// <remarks>
        /// <para>If this method returns <see langword="true"/>, the <paramref name="prepareResponse"/> callback
        /// will set appropriate response headers to reflect the results of content negotiation.</para>
        /// <para>For <see cref="CompressionMethod.None"/>, the callback removes the
        /// <c>Content-Encoding</c> field: the <c>identity</c> token belongs in request negotiation,
        /// not in response coding metadata. The <c>Vary: Accept-Encoding</c> header is retained.</para>
        /// <para>If this method returns <see langword="false"/>, the <paramref name="prepareResponse"/> callback
        /// will throw a <see cref="HttpNotAcceptableException"/> to send a <c>406 Not Acceptable</c> response
        /// with the <c>Vary</c> header set to <c>Accept-Encoding</c>,
        /// so that the client may know the reason why the request has been rejected.</para>
        /// <para>If <paramref name="this"/> has no<c>Accept-Encoding</c> header, this method
        /// always returns <see langword="true"/> and sets <paramref name="compressionMethod"/>
        /// to <see cref="CompressionMethod.None"/>.</para>
        /// </remarks>
        /// <seealso cref="HttpNotAcceptableException(string)"/>
        public static bool TryNegotiateContentEncoding(
            this IHttpRequest @this,
            bool preferCompression,
            out CompressionMethod compressionMethod,
            out Action<IHttpResponse> prepareResponse)
        {
            if (@this is null) throw new System.NullReferenceException();
            var acceptedEncodings = new QValueList(true, @this.Headers.GetValues(HttpHeaderNames.AcceptEncoding));
            if (!acceptedEncodings.TryNegotiateContentEncoding(preferCompression, out compressionMethod, out var compressionMethodName))
            {
                prepareResponse = r => throw HttpException.NotAcceptable(HttpHeaderNames.AcceptEncoding);
                return false;
            }

            prepareResponse = r =>
            {
                r.Headers.Add(HttpHeaderNames.Vary, HttpHeaderNames.AcceptEncoding);
                if (compressionMethodName == CompressionMethodNames.None)
                    r.Headers.Remove(HttpHeaderNames.ContentEncoding);
                else
                    r.Headers.Set(HttpHeaderNames.ContentEncoding, compressionMethodName);
            };
            return true;
        }

        /// <summary>
        /// <para>Checks whether an <c>If-None-Match</c> header exists in a request
        /// and, if so, whether it contains a given entity tag.</para>
        /// <para>See <see href="https://tools.ietf.org/html/rfc7232#section-3.2">RFC7232, Section 3.2</see>
        /// for a normative reference; however, see the Remarks section for more information
        /// about the RFC compliance of this method.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <param name="entityTag">The entity tag.</param>
        /// <param name="headerExists">When this method returns, a value that indicates whether an
        /// <c>If-None-Match</c> header is present in <paramref name="this"/>, regardless of the method's
        /// return value. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if an <c>If-None-Match</c> header is present in
        /// <paramref name="this"/> and one of the entity tags listed in it is equal to <paramref name="entityTag"/>,
        /// or the header is <c>*</c>; <see langword="false"/> otherwise.</returns>
        /// <remarks>
        /// <para>The comparison is the weak one <c>If-None-Match</c> requires
        /// (<see href="https://www.rfc-editor.org/rfc/rfc9110#section-13.1.2">RFC 9110, Section 13.1.2</see>),
        /// so a client's <c>W/</c> tag matches the same opaque value sent as a strong tag. It is the
        /// comparison <see cref="EvaluatePreconditions"/> applies.</para>
        /// <para>A condition this method cannot parse matches nothing, as does an
        /// <paramref name="entityTag"/> that is not a well-formed entity tag.</para>
        /// </remarks>
        public static bool CheckIfNoneMatch(this IHttpRequest @this, string entityTag, out bool headerExists)
        {
            if (@this is null) throw new System.NullReferenceException();
            var values = @this.Headers.GetValues(HttpHeaderNames.IfNoneMatch);
            if (values == null)
            {
                headerExists = false;
                return false;
            }

            headerExists = true;
            var index = 0;
            if (entityTag == null
                || !ReadEntityTag(entityTag, ref index, out var weak, out var start, out var length)
                || index != entityTag.Length)
                return false;

            foreach (var value in values)
            {
                // This older method answers a malformed condition with "no match", where
                // EvaluatePreconditions rejects the request; its callers never expected a status.
                try
                {
                    if (MatchesEntityTags(value, entityTag, weak, start, length, false, true))
                        return true;
                }
                catch (HttpException)
                {
                }
            }

            return false;
        }

        // Check whether the If-Modified-Since request header exists
        // and specifies a date and time more recent than or equal to
        // the date and time of last modification of the requested resource.
        // RFC7232, Section 3.3

        /// <summary>
        /// <para>Checks whether an <c>If-Modified-Since</c> header exists in a request
        /// and, if so, whether its value is a date and time more recent or equal to
        /// a given <see cref="DateTime"/>.</para>
        /// <para>See <see href="https://tools.ietf.org/html/rfc7232#section-3.3">RFC7232, Section 3.3</see>
        /// for a normative reference.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <param name="lastModifiedUtc">A date and time value, in Coordinated Universal Time,
        /// expressing the last time a resource was modified.</param>
        /// <param name="headerExists">When this method returns, a value that indicates whether an
        /// <c>If-Modified-Since</c> header is present in <paramref name="this"/>, regardless of the method's
        /// return value. This parameter is passed uninitialized.</param>
        /// <returns><see langword="true"/> if an <c>If-Modified-Since</c> header is present in
        /// <paramref name="this"/> and its value is a date and time more recent or equal to <paramref name="lastModifiedUtc"/>;
        /// <see langword="false"/> otherwise.</returns>
        public static bool CheckIfModifiedSince(this IHttpRequest @this, DateTime lastModifiedUtc, out bool headerExists)
        {
            if (@this is null) throw new System.NullReferenceException();
            var value = @this.Headers.Get(HttpHeaderNames.IfModifiedSince);
            if (value == null)
            {
                headerExists = false;
                return false;
            }

            headerExists = true;

            // HTTP dates carry whole seconds; a sub-second modification time must not defeat an echo of Last-Modified.
            return HttpDate.TryParse(value, out var dateTime)
                && WholeSeconds(dateTime) >= WholeSeconds(lastModifiedUtc);
        }

        // Checks the Range request header to tell whether to send
        // a "206 Partial Content" response.

        /// <summary>
        /// <para>Checks whether a <c>Range</c> header exists in a request
        /// and, if so, determines whether it is possible to send a <c>206 Partial Content</c> response.</para>
        /// <para>See <see href="https://tools.ietf.org/html/rfc7233">RFC7233</see>
        /// for a normative reference; however, see the Remarks section for more information
        /// about the RFC compliance of this method.</para>
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <param name="contentLength">The total length, in bytes, of the response entity, i.e.
        /// what would be sent in a <c>200 OK</c> response.</param>
        /// <param name="entityTag">An entity tag representing the response entity. This value is checked against
        /// the <c>If-Range</c> header, if it is present.</param>
        /// <param name="lastModifiedUtc">The date and time value, in Coordinated Universal Time,
        /// expressing the last modification time of the resource entity. This value is checked against
        /// the <c>If-Range</c> header, if it is present.</param>
        /// <param name="start">When this method returns <see langword="true"/>, the start of the requested byte range.
        /// This parameter is passed uninitialized.</param>
        /// <param name="upperBound">
        /// <para>When this method returns <see langword="true"/>, the upper bound of the requested byte range.
        /// This parameter is passed uninitialized.</para>
        /// <para>Note that the upper bound of a range is NOT the sum of the range's start and length;
        /// for example, a range expressed as <c>bytes=0-99</c> has a start of 0, an upper bound of 99,
        /// and a length of 100 bytes.</para>
        /// </param>
        /// <returns>
        /// <para>This method returns <see langword="true"/> if the following conditions are satisfied:</para>
        /// <list type="bullet">
        /// <item><description>>the request's HTTP method is <c>GET</c>;</description></item>
        /// <item><description>>a <c>Range</c> header is present in the request;</description></item>
        /// <item><description>>either no <c>If-Range</c> header is present in the request, or it
        /// specifies an entity tag equal to <paramref name="entityTag"/>, or a UTC date and time
        /// equal to <paramref name="lastModifiedUtc"/>;</description></item>
        /// <item><description>>the <c>Range</c> header specifies exactly one range;</description></item>
        /// <item><description>>the specified range starts within the range from 0 to <paramref name="contentLength"/> - 1.
        /// An upper bound at or past the end is reduced to <paramref name="contentLength"/> - 1.</description></item>
        /// </list>
        /// <para>If the last condition is not satisfied, i.e. the specified range starts at or past
        /// <paramref name="contentLength"/>, this method does not return;
        /// it throws a <see cref="HttpRangeNotSatisfiableException"/> instead.</para>
        /// <para>If any of the other conditions are not satisfied, this method returns <see langword="false"/>.</para>
        /// </returns>
        /// <remarks>
        /// <para>A positive suffix range selects the final requested number of bytes, clamped to
        /// the representation length. A zero suffix is unsatisfiable; positive suffix ranges
        /// for empty representations are ignored.</para>
        /// <para>According to <see href="https://tools.ietf.org/html/rfc7233#section-3.1">RFC7233, Section 3.1</see>,
        /// there are several conditions under which a server may ignore or reject a range request; therefore,
        /// clients are (or should be) prepared to receive a <c>200 OK</c> response with the whole response
        /// entity instead of the requested range(s). For this reason, until the generation of
        /// <c>multipart/byteranges</c> responses is implemented in EmbedIO, this method will ignore
        /// range requests specifying more than one range, even if this behavior is not, strictly speaking,
        /// RFC7233-compliant.</para>
        /// <para>To make clients aware that range requests are accepted for a resource, every <c>200 OK</c>
        /// (or <c>304 Not Modified</c>) response for the same resource should include an <c>Accept-Ranges</c>
        /// header with the string <c>bytes</c> as value.</para>
        /// </remarks>
        public static bool IsRangeRequest(this IHttpRequest @this, long contentLength, string entityTag, DateTime lastModifiedUtc, out long start, out long upperBound)
        {
            if (@this is null) throw new System.NullReferenceException();
            start = 0;
            upperBound = contentLength - 1;

            // RFC7233, Section 3.1:
            // "A server MUST ignore a Range header field received with a request method other than GET."
            if (@this.HttpVerb != HttpVerbs.Get)
                return false;

            // No Range header, no partial content.
            var rangeHeader = @this.Headers.Get(HttpHeaderNames.Range);
            if (rangeHeader == null)
                return false;

            // Ignore Range when a supplied If-Range validator does not match.
            // RFC7233, Section 3.2: "If the validator given in the If-Range header field matches the
            //                       current validator for the selected representation of the target
            //                       resource, then the server SHOULD process the Range header field as
            //                       requested.If the validator does not match, the server MUST ignore
            //                       the Range header field.Note that this comparison by exact match,
            //                       including when the validator is an HTTP-date, differs from the
            //                       "earlier than or equal to" comparison used when evaluating an
            //                       If-Unmodified-Since conditional."
            var ifRange = @this.Headers.Get(HttpHeaderNames.IfRange)?.Trim();
            if (ifRange != null && ifRange != entityTag)
            {
                if (!HttpDate.TryParse(ifRange, out var rangeDate))
                    return false;

                if (WholeSeconds(rangeDate) != WholeSeconds(lastModifiedUtc))
                    return false;
            }

            // Ignore the Range request header if it cannot be parsed successfully.
            if (!RangeHeaderValue.TryParse(rangeHeader, out var range))
                return false;

            // EmbedIO does not support multipart/byteranges responses (yet),
            // thus ignore range requests that specify more than one range.
            if (range.Ranges.Count != 1)
                return false;

            var firstRange = range.Ranges.First();
            if (!firstRange.From.HasValue)
            {
                var suffixLength = firstRange.To.GetValueOrDefault();
                if (suffixLength == 0)
                    throw HttpException.RangeNotSatisfiable(contentLength);

                // RFC 9110 permits ignoring Range for an empty representation.
                if (contentLength == 0)
                    return false;

                // To is a suffix length here, not an inclusive offset.
                start = suffixLength >= contentLength ? 0 : contentLength - suffixLength;
                upperBound = contentLength - 1;
            }
            else
            {
                // RFC 9110, Section 14.1.2: a last position at or past the end selects the remainder.
                start = firstRange.From.Value;
                upperBound = Math.Min(firstRange.To ?? contentLength - 1, contentLength - 1);
            }
            if (start >= contentLength || upperBound < start)
                throw HttpException.RangeNotSatisfiable(contentLength);

            return true;
        }

        // Whole seconds of a resource's own UTC time; validators are not converted between time zones here.
        private static long WholeSeconds(DateTime value) => value.Ticks / TimeSpan.TicksPerSecond;
    }
}
