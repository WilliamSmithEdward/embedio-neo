using System.Collections.Generic;

namespace EmbedIO
{
    /// <summary>Optional access to request trailer fields kept separate from initial headers.</summary>
    public interface IHttpRequestTrailers : IHttpRequest
    {
        /// <summary>Gets an immutable, ordered snapshot after complete valid input has been consumed.</summary>
        /// <param name="trailers">The trailer lines, preserving duplicates, or an empty list when unavailable.</param>
        /// <returns>True for completed input, including messages without trailers; false otherwise.</returns>
        /// <remarks>This method never reads or drains input. Trailer declarations do not authorize fields:
        /// applications must recognize and verify metadata before use. These fields never alter initial headers.</remarks>
        bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers);
    }
}
