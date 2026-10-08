using System;

namespace EmbedIO.Internal
{
    internal static class UriUtility
    {
        public static Uri? StringToUri(string? str)
        {
            _ = Uri.TryCreate(str, CanBeAbsoluteUrl(str) ? UriKind.Absolute : UriKind.Relative, out var result);
            return result;
        }

        public static Uri? StringToAbsoluteUri(string str)
        {
            if (!CanBeAbsoluteUrl(str))
            {
                return null;
            }

            _ = Uri.TryCreate(str, UriKind.Absolute, out var result);
            return result;
        }

        // URI schemes are case-insensitive; the path and query must retain their case.
        private static bool CanBeAbsoluteUrl(string? str)
            => str != null && str.Length > 0
            && (str.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
                || str.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
                || str.StartsWith("ws:", StringComparison.OrdinalIgnoreCase)
                || str.StartsWith("wss:", StringComparison.OrdinalIgnoreCase));
    }
}
