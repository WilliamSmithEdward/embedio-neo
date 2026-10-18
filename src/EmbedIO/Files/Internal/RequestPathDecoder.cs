using System;
using EmbedIO.Internal;

namespace EmbedIO.Files.Internal
{
    // Decodes a mapped request path one segment at a time.
    //
    // Modules are selected on the undecoded path: WebServer builds the route from
    // Url.AbsolutePath, and .NET's Uri keeps %2F encoded because a slash is reserved.
    // Decoding a whole path here would therefore turn %2F into a separator that routing
    // never saw, so a request could reach a resource under a prefix whose modules, an
    // authentication module included, never ran. RFC 3986 Section 2.2 is explicit that a
    // reserved character in its encoded form is not equivalent to the character itself.
    //
    // Each segment decodes on its own and the separators stay the ones the request actually
    // sent, so the provider resolves exactly the segments routing matched. A segment that
    // decodes to something which is not a single usable segment is refused rather than
    // repaired, and the caller maps that to "no resource". Nothing legitimate is lost: no
    // file name or archive entry name can contain a separator in the first place.
    internal static class RequestPathDecoder
    {
        // True when every segment decodes to a usable single segment, and decoded then holds
        // the path with its original separators. False leaves decoded empty, meaning the
        // request names no resource.
        internal static bool TryDecodeSegments(string requestPath, out string decoded)
        {
            decoded = string.Empty;

            if (requestPath == null) return false;

            if (requestPath.Length == 0) return true;

            // Most paths carry no escapes, so they need no splitting or rejoining.
            if (StringOperations.IndexOfOrdinal(requestPath, '%') < 0)
            {
                if (ContainsRefusedCharacter(requestPath)) return false;

                decoded = requestPath;
                return true;
            }

            var segments = requestPath.Split('/');

            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];

                if (StringOperations.IndexOfOrdinal(segment, '%') < 0)
                {
                    if (ContainsRefusedCharacter(segment)) return false;

                    continue;
                }

                // A malformed escape throws rather than decoding, and an unusable path
                // names no resource.
                try
                {
                    segment = Uri.UnescapeDataString(segment);
                }

                catch (UriFormatException)
                {
                    return false;
                }

                if (!IsSingleSegment(segment)) return false;

                segments[index] = segment;
            }

            decoded = string.Join("/", segments);
            return true;
        }

        // A decoded segment must still be one segment: never a separator of either flavour,
        // never a dot segment that would climb the tree, and never a control character.
        private static bool IsSingleSegment(string segment) =>
            StringOperations.IndexOfOrdinal(segment, '/') < 0
                && StringOperations.IndexOfOrdinal(segment, '\\') < 0
                && !string.Equals(segment, ".", StringComparison.Ordinal)
                && !string.Equals(segment, "..", StringComparison.Ordinal)
                && !ContainsRefusedCharacter(segment);

        private static bool ContainsRefusedCharacter(string value)
        {
            foreach (var character in value)
                if (character < ' ' || character == '\u007f') return true;

            return false;
        }
    }
}
