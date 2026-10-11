using System;

namespace EmbedIO.Net.Internal
{
    internal static class HttpExpectations
    {
        // Recognize only a bare expectation member. Commas and escaped quotes
        // inside extension values must not create a synthetic 100-continue member.
        internal static bool ContainsContinue(string? value)
        {
            if (value == null) return false;
            var start = 0;
            var quoted = false;
            for (var i = 0; i <= value.Length; ++i)
            {
                if (i < value.Length)
                {
                    if (quoted && value[i] == '\\') { ++i; continue; }
                    if (value[i] == '"') quoted = !quoted;
                    if (quoted || value[i] != ',') continue;
                }
                if (quoted) return false;
                var end = i;
                while (start < end && (value[start] == ' ' || value[start] == '\t')) ++start;
                while (end > start && (value[end - 1] == ' ' || value[end - 1] == '\t')) --end;
                if (end - start == 12 && string.Compare(value, start, "100-continue", 0, 12, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
                start = i + 1;
            }
            return false;
        }
    }
}
