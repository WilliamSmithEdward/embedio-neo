using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EmbedIO.Serialization
{
    // Normalize only lossless legacy syntax; System.Text.Json still validates the document.
    internal static class CompatibleJsonInput
    {
        public static string Normalize(string json, JsonCommentHandling commentHandling)
        {
            var output = new Rewriter(json);
            var inString = false;
            var escaped = false;
            var previous = '\0';

            for (var index = 0; index < json.Length; index++)
            {
                var character = json[index];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (character == '\\') escaped = true;

                    else if (character == '"') inString = false;

                    else if (character < ' ')
                        output.Replace(index, 1, "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));

                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    previous = '"';
                    continue;
                }

                if (char.IsWhiteSpace(character)) continue;

                if (commentHandling != JsonCommentHandling.Disallow && SkipComment(json, ref index)) continue;

                if (StartsNumber(previous, character))
                {
                    var end = NumberEnd(json, index);
                    var number = CanonicalNumber(json, index, end);

                    if (number != null) output.Replace(index, end - index, number);

                    index = end - 1;
                    previous = '0';
                    continue;
                }

                previous = character;
            }

            return output.Finish();
        }

        // Advances past a "//" or "/*" comment that starts at index. False when
        // no comment starts there.
        private static bool SkipComment(string json, ref int index)
        {
            if (json[index] != '/' || index + 1 >= json.Length) return false;

            if (json[index + 1] == '/')
            {
                index += 2;

                while (index < json.Length && json[index] != '\r' && json[index] != '\n') index++;

                return true;
            }

            if (json[index + 1] != '*') return false;

            index += 2;

            while (index + 1 < json.Length && (json[index] != '*' || json[index + 1] != '/')) index++;

            index++;
            return true;
        }

        // A number may start the document or follow an opening bracket, a name or a comma.
        private static bool StartsNumber(char previous, char character) =>
            (previous == '\0' || previous == '[' || previous == ':' || previous == ',')
                && (character == '+' || character == '-' || character == '.' || IsDigit(character));

        private static int NumberEnd(string json, int index)
        {
            var end = index + 1;

            while (
                end < json.Length
                && (IsDigit(json[end])
                    || json[end] == '+'
                    || json[end] == '-'
                    || json[end] == '.'
                    || json[end] == 'e'
                    || json[end] == 'E')
            ) end++;

            return end;
        }

        // The strict spelling of a legacy number token (leading "+", leading zeros,
        // leading or trailing point), or null when the token is left as it is.
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1845:Use span-based string.Concat",
            Justification = "The shared implementation targets netstandard2.0, which has no span-based string.Concat overloads."
        )]
        private static string? CanonicalNumber(string json, int index, int end)
        {
            var start = json[index] == '+' ? index + 1 : index;
            var digits = start < end && json[start] == '-' ? start + 1 : start;
            var integerEnd = digits;

            while (integerEnd < end && IsDigit(json[integerEnd])) integerEnd++;

            var significant = digits;

            while (significant + 1 < integerEnd && json[significant] == '0') significant++;

            var leadingPoint = digits < end && json[digits] == '.';
            var trailingPoint = integerEnd < end
                && json[integerEnd] == '.'
                && (integerEnd + 1 == end || json[integerEnd + 1] == 'e' || json[integerEnd + 1] == 'E');
            var hasDigits = integerEnd > digits || (leadingPoint && digits + 1 < end && IsDigit(json[digits + 1]));
            var legacy = start != index || significant != digits || leadingPoint || trailingPoint;

            if (!hasDigits || !legacy) return null;

            var number = (digits != start ? "-" : string.Empty)
                + (leadingPoint ? "0" : string.Empty)
                + json.Substring(significant, integerEnd - significant);

            if (integerEnd < end)
            {
                number += json[integerEnd];

                if (trailingPoint) number += "0";

                number += json.Substring(integerEnd + 1, end - integerEnd - 1);
            }

            return number;
        }

        // Copies the input lazily: nothing is allocated until the first replacement.
        private struct Rewriter
        {
            private readonly string _source;
            private StringBuilder? _output;
            private int _copied;

            internal Rewriter(string source)
            {
                _source = source;
                _output = null;
                _copied = 0;
            }

            internal void Replace(int start, int length, string replacement)
            {
                _output ??= new StringBuilder(_source.Length);
                _output.Append(_source, _copied, start - _copied);
                _output.Append(replacement);
                _copied = start + length;
            }

            internal readonly string Finish()
            {
                if (_output == null) return _source;

                _output.Append(_source, _copied, _source.Length - _copied);
                return _output.ToString();
            }
        }

        private static bool IsDigit(char value) => value >= '0' && value <= '9';
    }
}
