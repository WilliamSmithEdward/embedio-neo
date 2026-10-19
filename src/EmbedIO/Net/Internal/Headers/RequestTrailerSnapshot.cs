using System;
using System.Collections.Generic;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    internal interface IRequestTrailerSource
    {
        bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers);
    }

    // Separate owned metadata. Never merge trailer lines into routing or auth headers.
    internal static class RequestTrailerSnapshot
    {
        internal static readonly IReadOnlyList<KeyValuePair<string, string>> Empty = Array.AsReadOnly(
            Array.Empty<KeyValuePair<string, string>>()
        );

        internal static IReadOnlyList<KeyValuePair<string, string>> Copy(HpackField[] fields)
        {
            if (fields.Length == 0) return Empty;

            var lines = new KeyValuePair<string, string>[fields.Length];

            for (var i = 0; i < fields.Length; i++)
                lines[i] = new KeyValuePair<string, string>(fields[i].Name, fields[i].Value);

            return Array.AsReadOnly(lines);
        }
    }
}
