using System;
using System.Collections.Generic;

namespace EmbedIO
{
    public static partial class HttpRequestExtensions
    {
        internal readonly struct SelectedByteRange
        {
            internal SelectedByteRange(long start, long length)
            {
                Start = start;
                Length = length;
            }

            internal long Start { get; }
            internal long Length { get; }
            internal long End => Start + Length - 1;
        }

        internal static SelectedByteRange[]? SelectByteRanges(
            IHttpRequest request,
            long total,
            string? tag,
            DateTimeOffset? modified,
            bool strongDate,
            int maximum
        )
        {
            if (request.HttpMethod is not ("GET" or "QUERY")) return null;

            var value = request.Headers[HttpHeaderNames.Range];

            if (value is null) return null;

            var weak = IsWeakEntityTag(tag, "Invalid representation entity tag.", nameof(tag));
            var validator = request.Headers[HttpHeaderNames.IfRange];

            if (validator != null && !RangeValidatorMatches(validator, tag, weak, modified, strongDate)) return null;

            var index = 0;

            if (!ReadBytesUnit(value, ref index)) return null;

            var ranges = new List<SelectedByteRange>();
            var specifications = 0;

            while (index < value.Length)
            {
                SkipTagWhitespace(value, ref index);

                if (index == value.Length) break;

                if (value[index] == ',')
                {
                    index++;
                    continue;
                }

                if (++specifications > maximum) return null;

                if (!ReadRangeSpec(value, ref index, total - 1, out var suffix, out var first, out var end))
                    return null;

                SkipTagWhitespace(value, ref index);

                if (index < value.Length && value[index++] != ',') return null;

                if (IsSatisfiable(suffix, first, total)) ranges.Add(Select(suffix, first, end, total));
            }

            if (specifications == 0 || total == 0) return null;

            if (ranges.Count == 0) throw HttpException.RangeNotSatisfiable(total);

            MergeOverlapping(ranges);
            return [.. ranges];
        }

        // Combines overlapping and adjacent ranges in place, retaining request order.
        private static void MergeOverlapping(List<SelectedByteRange> ranges)
        {
            for (var first = 0; first < ranges.Count; first++)
            {
                for (var second = first + 1; second < ranges.Count; second++)
                {
                    if (ranges[first].Start > ranges[second].End + 1 || ranges[second].Start > ranges[first].End + 1)
                        continue;

                    var start = Math.Min(ranges[first].Start, ranges[second].Start);
                    var end = Math.Max(ranges[first].End, ranges[second].End);
                    ranges[first] = new SelectedByteRange(start, end - start + 1);
                    ranges.RemoveAt(second);
                    second = first;
                }
            }
        }
    }
}
