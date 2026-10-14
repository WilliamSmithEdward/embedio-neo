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
                if (i < value.Length && !EndsMember(value, ref i, ref quoted)) continue;
                if (quoted) return false;
                if (IsContinue(value, start, i)) return true;
                start = i + 1;
            }
            return false;
        }

        // Reads one character, skipping the one a backslash escapes inside quotes; true at a
        // comma outside quotes, where the member before it ends.
        private static bool EndsMember(string value, ref int i, ref bool quoted)
        {
            if (quoted && value[i] == '\\') { ++i; return false; }
            if (value[i] == '"') quoted = !quoted;
            return !quoted && value[i] == ',';
        }

        // The member, trimmed of blanks, is exactly "100-continue" in any letter case.
        private static bool IsContinue(string value, int start, int end)
        {
            while (start < end && (value[start] == ' ' || value[start] == '\t')) ++start;
            while (end > start && (value[end - 1] == ' ' || value[end - 1] == '\t')) --end;
            return end - start == 12 && string.Compare(value, start, "100-continue", 0, 12, StringComparison.OrdinalIgnoreCase) == 0;
        }
    }
}
