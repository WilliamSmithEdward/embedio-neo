using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbedIO.Idempotency
{
    /// <summary>Immutable settings for explicitly opted-in finite write endpoints.</summary>
    public sealed class IdempotencyPolicy
    {
        /// <summary>Creates a policy. Caller scope must be stable, trusted and isolated by tenant/user.</summary>
        /// <param name="partition">Stable application/policy namespace, including a revision when behavior changes.</param>
        /// <param name="getCallerScope">Resolves a trusted caller scope after authentication; never use the client key as identity.</param>
        /// <param name="retention">Completed and uncertain result retention, at most one day.</param>
        /// <param name="maxRequestBytes">Maximum buffered transferred request bytes.</param>
        /// <param name="maxResponseBytes">Maximum staged response bytes.</param>
        /// <param name="duplicateWaitTimeout">Maximum wait for a simultaneous request, at most thirty seconds.</param>
        /// <param name="varyByHeaders">Additional representation-selecting headers, copied during construction.</param>
        public IdempotencyPolicy(string partition, Func<IHttpContext, string> getCallerScope, TimeSpan retention,
            int maxRequestBytes = 256 * 1024, int maxResponseBytes = 64 * 1024,
            TimeSpan? duplicateWaitTimeout = null, IEnumerable<string>? varyByHeaders = null)
        {
            if (string.IsNullOrWhiteSpace(partition) || partition.Length > 256) throw new ArgumentException("A bounded stable partition is required.", nameof(partition));
            GetCallerScope = getCallerScope ?? throw new ArgumentNullException(nameof(getCallerScope));
            if (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(retention));
            if (maxRequestBytes < 1 || maxRequestBytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxRequestBytes));
            if (maxResponseBytes < 1 || maxResponseBytes > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
            var wait = duplicateWaitTimeout ?? TimeSpan.FromSeconds(5);
            if (wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(duplicateWaitTimeout));
            var headers = new HashSet<string>(new[] { "Content-Type", "Content-Encoding", "Accept", "Accept-Encoding", "Accept-Language", "Origin" }, StringComparer.OrdinalIgnoreCase);
            foreach (var header in varyByHeaders ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(header) || header.Length > 128 || header.Any(c => !HeaderToken(c)))
                    throw new ArgumentException("Invalid vary header.", nameof(varyByHeaders));
                headers.Add(header);
                if (headers.Count > 32) throw new ArgumentException("Too many vary headers.", nameof(varyByHeaders));
            }
            Partition = partition; Retention = retention; MaxRequestBytes = maxRequestBytes; MaxResponseBytes = maxResponseBytes;
            DuplicateWaitTimeout = wait; Headers = headers.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        /// <summary>Gets the stable policy namespace.</summary>
        public string Partition { get; }
        /// <summary>Gets terminal-record retention. In-flight records never expire automatically.</summary>
        public TimeSpan Retention { get; }
        /// <summary>Gets the transferred request body cap.</summary>
        public int MaxRequestBytes { get; }
        /// <summary>Gets the staged response body cap.</summary>
        public int MaxResponseBytes { get; }
        /// <summary>Gets the bounded duplicate wait.</summary>
        public TimeSpan DuplicateWaitTimeout { get; }
        internal Func<IHttpContext, string> GetCallerScope { get; }
        internal string[] Headers { get; }
        internal static bool HeaderToken(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
            || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }

    /// <summary>Selects a named idempotency policy; null disables an inherited declaration.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
    public sealed class IdempotencyAttribute : Attribute
    {
        /// <summary>Creates an opt-in declaration.</summary>
        /// <param name="policy">Named policy, or null to disable.</param>
        public IdempotencyAttribute(string? policy) => Policy = policy;
        /// <summary>Gets the selected policy.</summary>
        public string? Policy { get; }
    }
}
