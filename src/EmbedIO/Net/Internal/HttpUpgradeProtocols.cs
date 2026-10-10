using System;
using System.Collections.Generic;

namespace EmbedIO.Net.Internal
{
    // Original parser for RFC 9110 section 7.8. Protocol names are matched
    // without case; an optional protocol version retains its exact spelling.
    internal static class HttpUpgradeProtocols
    {
        internal static IReadOnlyList<string> Parse(string? field)
        {
            if (field == null) return Array.Empty<string>();
            var protocols = new List<string>();
            foreach (var element in field.Split(','))
            {
                var value = element.Trim(' ', '\t');
                // List syntax permits recipients to ignore empty list members.
                if (value.Length == 0) continue;
                if (!Valid(value)) return Array.Empty<string>();
                protocols.Add(value);
            }
            return protocols.AsReadOnly();
        }

        internal static bool Matches(string requested, string selected)
        {
            if (!Valid(requested) || !Valid(selected)) return false;
            var left = requested.IndexOf('/');
            var right = selected.IndexOf('/');
            if ((left < 0) != (right < 0)) return false;
            if (left < 0) return string.Equals(requested, selected, StringComparison.OrdinalIgnoreCase);
            return string.Equals(requested.Substring(0, left), selected.Substring(0, right), StringComparison.OrdinalIgnoreCase)
                && string.Equals(requested.Substring(left + 1), selected.Substring(right + 1), StringComparison.Ordinal);
        }

        private static bool Valid(string value)
        {
            if (value.Length == 0) return false;
            var slash = false;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '/')
                {
                    if (slash || i == 0 || i == value.Length - 1) return false;
                    slash = true;
                }
                else if (!HttpRequestFraming.IsTokenCharacter(value[i])) return false;
            }
            return true;
        }
    }
}