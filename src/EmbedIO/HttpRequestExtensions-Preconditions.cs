using System;
using System.Net;
using EmbedIO.Utilities;

namespace EmbedIO
{
    public static partial class HttpRequestExtensions
    {
        /// <summary>Evaluates RFC 9110 preconditions for an application's selected representation.</summary>
        /// <param name="request">The request.</param>
        /// <param name="entityTag">The current entity tag, or null when unavailable.</param>
        /// <param name="lastModified">The last modification instant, or null when unavailable.</param>
        /// <param name="representationExists">Whether the selected representation exists.</param>
        /// <returns>304 or 412 when a condition stops processing; otherwise null.</returns>
        /// <remarks>Call after authorization and ordinary request validation, when processing would
        /// otherwise succeed. QUERY validators must include its content, metadata and negotiated result.
        /// This method does not read content, choose a representation, set response headers or cache results.</remarks>
        /// <exception cref="ArgumentNullException">The request is null.</exception>
        /// <exception cref="ArgumentException">The application's entity tag is invalid.</exception>
        /// <exception cref="HttpException">An entity-tag condition is malformed (400).</exception>
        public static HttpStatusCode? EvaluatePreconditions(this IHttpRequest request, string? entityTag,
            DateTimeOffset? lastModified, bool representationExists = true)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            var currentStart = 0;
            var currentLength = 0;
            var currentWeak = false;
            if (entityTag != null)
            {
                var index = 0;
                if (!ReadEntityTag(entityTag, ref index, out currentWeak, out currentStart, out currentLength) || index != entityTag.Length)
                    throw new ArgumentException("Invalid selected-representation entity tag.", nameof(entityTag));
            }
            if (request.HttpMethod is "CONNECT" or "OPTIONS" or "TRACE") return null;
            var modified = lastModified.HasValue ? WholeSeconds(lastModified.Value) : (long?)null;
            var ifMatch = request.Headers[HttpHeaderNames.IfMatch];
            if (ifMatch != null)
            {
                if (!MatchesEntityTags(ifMatch, entityTag, currentWeak, currentStart, currentLength, true, representationExists))
                    return HttpStatusCode.PreconditionFailed;
            }
            else if (representationExists && modified.HasValue && TryConditionalDate(request.Headers[HttpHeaderNames.IfUnmodifiedSince], out var unmodified)
                && modified.GetValueOrDefault() > WholeSeconds(unmodified))
                return HttpStatusCode.PreconditionFailed;

            var retrieval = request.HttpMethod is "GET" or "HEAD" or "QUERY";
            var ifNoneMatch = request.Headers[HttpHeaderNames.IfNoneMatch];
            if (ifNoneMatch != null)
            {
                if (MatchesEntityTags(ifNoneMatch, entityTag, currentWeak, currentStart, currentLength, false, representationExists))
                    return retrieval ? HttpStatusCode.NotModified : HttpStatusCode.PreconditionFailed;
            }
            else if (retrieval && representationExists && modified.HasValue && TryConditionalDate(request.Headers[HttpHeaderNames.IfModifiedSince], out var since)
                && modified.GetValueOrDefault() <= WholeSeconds(since))
                return HttpStatusCode.NotModified;
            return null;
        }

        private static bool TryConditionalDate(string? value, out DateTimeOffset date)
        {
            date = default;
            return value != null && HttpDate.TryParse(value, out date);
        }
        private static long WholeSeconds(DateTimeOffset value) => value.UtcDateTime.Ticks / TimeSpan.TicksPerSecond;

        private static bool MatchesEntityTags(string value, string? current, bool currentWeak,
            int currentStart, int currentLength, bool strong, bool exists)
        {
            var index = 0;
            SkipTagWhitespace(value, ref index);
            if (index < value.Length && value[index] == '*')
            {
                index++;
                SkipTagWhitespace(value, ref index);
                if (index != value.Length) throw HttpException.BadRequest("Malformed entity-tag wildcard condition.");
                return exists;
            }
            var matched = false;
            while (index < value.Length)
            {
                // RFC list recipients ignore empty members; commas inside opaque tags remain data.
                if (value[index] == ',') { index++; SkipTagWhitespace(value, ref index); continue; }
                if (!ReadEntityTag(value, ref index, out var weak, out var start, out var length))
                    throw HttpException.BadRequest("Malformed entity-tag condition.");
                if (exists && current != null && (!strong || (!weak && !currentWeak)) && length == currentLength
                    && string.Compare(value, start, current, currentStart, length, StringComparison.Ordinal) == 0)
                    matched = true;
                SkipTagWhitespace(value, ref index);
                if (index == value.Length) break;
                if (value[index++] != ',') throw HttpException.BadRequest("Malformed entity-tag list separator.");
                SkipTagWhitespace(value, ref index);
            }
            return matched;
        }
        private static void SkipTagWhitespace(string value, ref int index)
        {
            while (index < value.Length && value[index] is ' ' or '\t') index++;
        }
        private static bool ReadEntityTag(string value, ref int index, out bool weak, out int start, out int length)
        {
            weak = false;
            start = length = 0;
            if (index + 1 < value.Length && value[index] == 'W' && value[index + 1] == '/')
            {
                weak = true;
                index += 2;
            }
            if (index == value.Length || value[index++] != '"') return false;
            start = index;
            while (index < value.Length && value[index] != '"')
            {
                var character = value[index++];
                if (character < 33 || character == 127 || character > 255) return false;
            }
            if (index == value.Length) return false;
            length = index - start;
            index++;
            return true;
        }
    }
}
