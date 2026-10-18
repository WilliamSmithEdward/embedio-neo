using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbedIO.Caching
{
    /// <summary>Immutable policy for explicitly public, reusable response representations.</summary>
    /// <remarks>Declare every request header used to select the representation. URL, method,
    /// Accept, Accept-Encoding, Accept-Language and Origin are always part of the key.
    /// Responses remain snapshots until expiry or explicit invalidation.</remarks>
    public sealed class OutputCachePolicy
    {
        internal readonly string Identity = Guid.NewGuid().ToString("N");
        internal readonly string[] Headers;
        internal readonly string[] Tags;

        /// <summary>Creates a bounded-lifetime policy.</summary>
        /// <param name="duration">Positive lifetime, at most one day, measured from fill start.</param>
        /// <param name="varyByHeaders">Additional representation-selecting request fields.</param>
        /// <param name="tags">Tags used for invalidation; at most sixteen, each at most 128 characters.</param>
        public OutputCachePolicy(TimeSpan duration, IEnumerable<string>? varyByHeaders = null, IEnumerable<string>? tags = null)
        {
            if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(duration));
            Duration = duration;
            Headers = new[] { "Accept", "Accept-Encoding", "Accept-Language", "Origin" }
                .Concat(varyByHeaders ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (Headers.Length > 32 || Headers.Any(h => string.IsNullOrEmpty(h) || h.Length > 128 || h.Any(c => !IsToken(c))))
                throw new ArgumentException("Use at most 32 valid header names, including the four defaults.", nameof(varyByHeaders));
            Tags = (tags ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (Tags.Length > 16 || Tags.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 128))
                throw new ArgumentException("Use at most sixteen nonempty tags of at most 128 characters.", nameof(tags));
        }

        /// <summary>Gets the maximum age of a stored representation.</summary>
        public TimeSpan Duration { get; }

        private static bool IsToken(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
