using System;
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
            StringBuilder? output = null;
            var copied = 0;
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
                        Replace(index, 1, "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                    previous = '"';
                    continue;
                }
                if (char.IsWhiteSpace(character)) continue;
                if (commentHandling != JsonCommentHandling.Disallow && character == '/' && index + 1 < json.Length)
                {
                    if (json[index + 1] == '/')
                    {
                        index += 2;
                        while (index < json.Length && json[index] != '\r' && json[index] != '\n') index++;
                        continue;
                    }
                    if (json[index + 1] == '*')
                    {
                        index += 2;
                        while (index + 1 < json.Length && (json[index] != '*' || json[index + 1] != '/')) index++;
                        index++;
                        continue;
                    }
                }

                if ((previous == '\0' || previous == '[' || previous == ':' || previous == ',')
                    && (character == '+' || character == '-' || character == '.' || IsDigit(character)))
                {
                    var end = index + 1;
                    while (end < json.Length && (IsDigit(json[end]) || json[end] == '+' || json[end] == '-'
                        || json[end] == '.' || json[end] == 'e' || json[end] == 'E')) end++;
                    var start = character == '+' ? index + 1 : index;
                    var digits = start < end && json[start] == '-' ? start + 1 : start;
                    var integerEnd = digits;
                    while (integerEnd < end && IsDigit(json[integerEnd])) integerEnd++;
                    var significant = digits;
                    while (significant + 1 < integerEnd && json[significant] == '0') significant++;
                    var leadingPoint = digits < end && json[digits] == '.';
                    var trailingPoint = integerEnd < end && json[integerEnd] == '.'
                        && (integerEnd + 1 == end || json[integerEnd + 1] == 'e' || json[integerEnd + 1] == 'E');
                    if ((integerEnd > digits || (leadingPoint && digits + 1 < end && IsDigit(json[digits + 1])))
                        && (start != index || significant != digits || leadingPoint || trailingPoint))
                    {
                        var number = (digits != start ? "-" : string.Empty)
                            + (leadingPoint ? "0" : string.Empty)
                            + json.Substring(significant, integerEnd - significant);
                        if (integerEnd < end)
                        {
                            number += json[integerEnd];
                            if (trailingPoint) number += "0";
                            number += json.Substring(integerEnd + 1, end - integerEnd - 1);
                        }
                        Replace(index, end - index, number);
                    }
                    index = end - 1;
                    previous = '0';
                    continue;
                }
                previous = character;
            }
            if (output == null) return json;
            output.Append(json, copied, json.Length - copied);
            return output.ToString();

            void Replace(int start, int length, string replacement)
            {
                output ??= new StringBuilder(json.Length);
                output.Append(json, copied, start - copied);
                output.Append(replacement);
                copied = start + length;
            }
        }

        private static bool IsDigit(char value) => value >= '0' && value <= '9';
    }
}
