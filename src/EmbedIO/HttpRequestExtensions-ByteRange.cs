using System;
using EmbedIO.Utilities;

namespace EmbedIO
{
    public static partial class HttpRequestExtensions
    {
        /// <summary>Selects one RFC 9110 byte range for GET or exact QUERY.</summary>
        /// <param name="request">The request.</param>
        /// <param name="contentLength">The length of the selected encoded representation.</param>
        /// <param name="entityTag">Its entity tag, or null when unavailable.</param>
        /// <param name="lastModified">Its modification instant, or null when unavailable.</param>
        /// <param name="start">The selected starting byte, or zero when ignored.</param>
        /// <param name="length">The selected byte count, or the full length when ignored.</param>
        /// <param name="lastModifiedIsStrong">Whether the application can guarantee that the date is a strong validator.</param>
        /// <returns>True for a single applicable range; false for an ignored range.</returns>
        /// <remarks>Evaluate preconditions first. Select QUERY results before calling this helper.
        /// Unknown units, invalid syntax, multiple ranges and empty representations are ignored.
        /// This method does not send 206, set headers, choose content coding or generate multipart output.</remarks>
        /// <exception cref="ArgumentNullException">The request is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The content length is negative.</exception>
        /// <exception cref="ArgumentException">The application's entity tag is invalid.</exception>
        /// <exception cref="HttpRangeNotSatisfiableException">A supported range cannot select bytes.</exception>
        public static bool TryGetByteRange(this IHttpRequest request, long contentLength, string? entityTag,
            DateTimeOffset? lastModified, out long start, out long length, bool lastModifiedIsStrong = false)
        {
            if (request is null) throw new ArgumentNullException(nameof(request));
            if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
            start = 0;
            length = contentLength;
            var currentWeak = false;
            if (entityTag != null)
            {
                var offset = 0;
                if (!ReadEntityTag(entityTag, ref offset, out currentWeak, out _, out _) || offset != entityTag.Length)
                    throw new ArgumentException("Invalid selected-representation entity tag.", nameof(entityTag));
            }
            if (request.HttpMethod is not ("GET" or "QUERY")) return false;
            var range = request.Headers[HttpHeaderNames.Range];
            if (range is null) return false;
            var ifRange = request.Headers[HttpHeaderNames.IfRange];
            if (ifRange != null && !RangeValidatorMatches(ifRange, entityTag, currentWeak, lastModified, lastModifiedIsStrong)) return false;
            var index = 0;
            SkipTagWhitespace(range, ref index);
            if (range.Length - index < 6 || string.Compare(range, index, "bytes=", 0, 6, StringComparison.OrdinalIgnoreCase) != 0) return false;
            index += 6;
            SkipTagWhitespace(range, ref index);
            var suffix = index < range.Length && range[index] == '-';
            if (suffix) index++;
            if (!ReadRangeNumber(range, ref index, out var first, out var firstStart, out var firstDigits)) return false;
            long end = contentLength - 1;
            if (!suffix)
            {
                if (index == range.Length || range[index++] != '-') return false;
                if (index < range.Length && range[index] is >= '0' and <= '9')
                {
                    if (!ReadRangeNumber(range, ref index, out end, out var endStart, out var endDigits)) return false;
                    if (firstDigits > endDigits || firstDigits == endDigits
                        && string.Compare(range, firstStart, range, endStart, firstDigits, StringComparison.Ordinal) > 0) return false;
                }
            }
            SkipTagWhitespace(range, ref index);
            if (index != range.Length) return false;
            if (suffix && first == 0) throw HttpException.RangeNotSatisfiable(contentLength);
            if (contentLength == 0) return false;
            if (suffix)
            {
                start = first >= contentLength ? 0 : contentLength - first;
                length = contentLength - start;
            }
            else
            {
                if (first >= contentLength) throw HttpException.RangeNotSatisfiable(contentLength);
                start = first;
                end = Math.Min(end, contentLength - 1);
                length = end - start + 1;
            }
            return true;
        }
        private static bool RangeValidatorMatches(string value, string? current, bool currentWeak,
            DateTimeOffset? modified, bool strongDate)
        {
            var index = 0;
            SkipTagWhitespace(value, ref index);
            if (index < value.Length && (value[index] == '"' || index + 1 < value.Length && value[index] == 'W' && value[index + 1] == '/'))
            {
                if (!ReadEntityTag(value, ref index, out var weak, out var start, out var length)) return false;
                SkipTagWhitespace(value, ref index);
                if (index != value.Length || weak || currentWeak || current is null) return false;
                return length == current.Length - 2 && string.Compare(value, start, current, 1, length, StringComparison.Ordinal) == 0;
            }
            return strongDate && modified.HasValue && HttpDate.TryParse(value, out var date)
                && WholeSeconds(date) == WholeSeconds(modified.GetValueOrDefault());
        }
        private static bool ReadRangeNumber(string value, ref int index, out long number, out int significantStart, out int significantDigits)
        {
            number = 0;
            var beginning = index;
            significantStart = index;
            while (index < value.Length && value[index] is >= '0' and <= '9')
            {
                var digit = value[index] - '0';
                if (index == significantStart && digit == 0) significantStart++;
                number = number > (long.MaxValue - digit) / 10 ? long.MaxValue : number * 10 + digit;
                index++;
            }
            significantDigits = index - significantStart;
            return index != beginning;
        }
    }
}
