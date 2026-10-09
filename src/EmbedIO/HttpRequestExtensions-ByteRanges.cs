using System;
using System.Collections.Generic;

namespace EmbedIO
{
    public static partial class HttpRequestExtensions
    {
        internal readonly struct SelectedByteRange
        {
            internal SelectedByteRange(long start, long length) { Start = start; Length = length; }
            internal long Start { get; }
            internal long Length { get; }
            internal long End => Start + Length - 1;
        }
        internal static SelectedByteRange[]? SelectByteRanges(IHttpRequest request, long total, string? tag,
            DateTimeOffset? modified, bool strongDate, int maximum)
        {
            if (request.HttpMethod is not ("GET" or "QUERY")) return null;
            var value = request.Headers[HttpHeaderNames.Range];
            if (value is null) return null;
            var weak = false;
            if (tag != null)
            {
                var cursor = 0;
                if (!ReadEntityTag(tag, ref cursor, out weak, out _, out _) || cursor != tag.Length)
                    throw new ArgumentException("Invalid representation entity tag.", nameof(tag));
            }
            var validator = request.Headers[HttpHeaderNames.IfRange];
            if (validator != null && !RangeValidatorMatches(validator, tag, weak, modified, strongDate)) return null;
            var index = 0;
            SkipTagWhitespace(value, ref index);
            if (value.Length - index < 6 || string.Compare(value, index, "bytes=", 0, 6, StringComparison.OrdinalIgnoreCase) != 0) return null;
            index += 6;
            var ranges = new List<SelectedByteRange>();
            var specifications = 0;
            while (index < value.Length)
            {
                SkipTagWhitespace(value, ref index);
                if (index == value.Length) break;
                if (value[index] == ',') { index++; continue; }
                if (++specifications > maximum) return null;
                var suffix = value[index] == '-';
                if (suffix) index++;
                if (!ReadRangeNumber(value, ref index, out var first, out var firstStart, out var firstDigits)) return null;
                var end = total - 1;
                if (!suffix)
                {
                    if (index == value.Length || value[index++] != '-') return null;
                    if (index < value.Length && value[index] is >= '0' and <= '9')
                    {
                        if (!ReadRangeNumber(value, ref index, out end, out var endStart, out var endDigits)) return null;
                        if (firstDigits > endDigits || firstDigits == endDigits
                            && string.Compare(value, firstStart, value, endStart, firstDigits, StringComparison.Ordinal) > 0) return null;
                    }
                }
                SkipTagWhitespace(value, ref index);
                if (index < value.Length && value[index++] != ',') return null;
                if (total == 0 || suffix && first == 0 || !suffix && first >= total) continue;
                var start = suffix ? first >= total ? 0 : total - first : first;
                end = suffix ? total - 1 : Math.Min(end, total - 1);
                ranges.Add(new SelectedByteRange(start, end - start + 1));
            }
            if (specifications == 0 || total == 0) return null;
            if (ranges.Count == 0) throw HttpException.RangeNotSatisfiable(total);
            // Retain request order when combining overlapping/adjacent ranges.
            for (var first = 0; first < ranges.Count; first++)
            {
                for (var second = first + 1; second < ranges.Count; second++)
                {
                    if (ranges[first].Start > ranges[second].End + 1 || ranges[second].Start > ranges[first].End + 1) continue;
                    var start = Math.Min(ranges[first].Start, ranges[second].Start);
                    var end = Math.Max(ranges[first].End, ranges[second].End);
                    ranges[first] = new SelectedByteRange(start, end - start + 1);
                    ranges.RemoveAt(second);
                    second = first;
                }
            }
            return ranges.ToArray();
        }
    }
}
