using System;
using System.Runtime.CompilerServices;
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
        public static bool TryGetByteRange(
            this IHttpRequest request,
            long contentLength,
            string? entityTag,
            DateTimeOffset? lastModified,
            out long start,
            out long length,
            bool lastModifiedIsStrong = false
        )
        {
            if (request is null) throw new ArgumentNullException(nameof(request));

            if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));

            start = 0;
            length = contentLength;
            var currentWeak = IsWeakEntityTag(
                entityTag,
                "Invalid selected-representation entity tag.",
                nameof(entityTag)
            );

            if (request.HttpMethod is not ("GET" or "QUERY")) return false;

            var range = request.Headers[HttpHeaderNames.Range];

            if (range is null) return false;

            var ifRange = request.Headers[HttpHeaderNames.IfRange];

            if (
                ifRange != null
                && !RangeValidatorMatches(ifRange, entityTag, currentWeak, lastModified, lastModifiedIsStrong)
            ) return false;

            var index = 0;

            if (!ReadBytesUnit(range, ref index)) return false;

            SkipTagWhitespace(range, ref index);

            if (!ReadRangeSpec(range, ref index, contentLength - 1, out var suffix, out var first, out var end))
                return false;

            SkipTagWhitespace(range, ref index);

            if (index != range.Length) return false;

            if (suffix && first == 0) throw HttpException.RangeNotSatisfiable(contentLength);

            if (contentLength == 0) return false;

            if (!suffix && first >= contentLength) throw HttpException.RangeNotSatisfiable(contentLength);

            var selected = Select(suffix, first, end, contentLength);
            start = selected.Start;
            length = selected.Length;
            return true;
        }

        // The application's entity tag must be a complete, valid tag; its
        // weakness decides whether If-Range can match it.
        private static bool IsWeakEntityTag(string? tag, string message, string parameterName)
        {
            if (tag == null) return false;

            var cursor = 0;

            if (!ReadEntityTag(tag, ref cursor, out var weak, out _, out _) || cursor != tag.Length)
                throw new ArgumentException(message, parameterName);

            return weak;
        }

        // Only the bytes unit is supported, case-insensitively, after optional whitespace.
        private static bool ReadBytesUnit(string value, ref int index)
        {
            SkipTagWhitespace(value, ref index);

            if (
                value.Length - index < 6
                || string.Compare(value, index, "bytes=", 0, 6, StringComparison.OrdinalIgnoreCase) != 0
            ) return false;

            index += 6;
            return true;
        }

        // One "first-last", "first-" or "-suffix" specification. An omitted last byte
        // means lastByte; a last byte numerically below the first byte is rejected
        // by digit count and then by digit order, so overlong numbers never overflow.
        private static bool ReadRangeSpec(
            string value,
            ref int index,
            long lastByte,
            out bool suffix,
            out long first,
            out long end
        )
        {
            end = lastByte;
            suffix = index < value.Length && value[index] == '-';

            if (suffix) index++;

            if (!ReadRangeNumber(value, ref index, out first, out var firstStart, out var firstDigits)) return false;

            if (suffix) return true;

            if (index == value.Length || value[index++] != '-') return false;

            if (index == value.Length || value[index] is not (>= '0' and <= '9')) return true;

            if (!ReadRangeNumber(value, ref index, out end, out var endStart, out var endDigits)) return false;

            return firstDigits < endDigits
                || (firstDigits == endDigits
                    && string.Compare(value, firstStart, value, endStart, firstDigits, StringComparison.Ordinal) <= 0);
        }

        // A suffix of zero bytes or a first byte past the end selects nothing.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsSatisfiable(bool suffix, long first, long total) =>
            total != 0 && (suffix ? first != 0 : first < total);

        // The bytes a satisfiable specification selects from a representation of the given length.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static SelectedByteRange Select(bool suffix, long first, long end, long total)
        {
            var start = suffix ? (first >= total ? 0 : total - first) : first;
            var last = suffix ? total - 1 : Math.Min(end, total - 1);
            return new SelectedByteRange(start, last - start + 1);
        }

        private static bool RangeValidatorMatches(
            string value,
            string? current,
            bool currentWeak,
            DateTimeOffset? modified,
            bool strongDate
        )
        {
            var index = 0;
            SkipTagWhitespace(value, ref index);

            if (
                index < value.Length
                && (value[index] == '"' || index + 1 < value.Length && value[index] == 'W' && value[index + 1] == '/')
            )
            {
                if (!ReadEntityTag(value, ref index, out var weak, out var start, out var length)) return false;

                SkipTagWhitespace(value, ref index);

                if (index != value.Length || weak || currentWeak || current is null) return false;

                return length == current.Length - 2
                    && string.Compare(value, start, current, 1, length, StringComparison.Ordinal) == 0;
            }

            return strongDate
                && modified.HasValue
                && HttpDate.TryParse(value, out var date)
                && WholeSeconds(date) == WholeSeconds(modified.GetValueOrDefault());
        }

        private static bool ReadRangeNumber(
            string value,
            ref int index,
            out long number,
            out int significantStart,
            out int significantDigits
        )
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
