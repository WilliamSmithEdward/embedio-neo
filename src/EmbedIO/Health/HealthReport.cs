using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace EmbedIO.Health
{
    /// <summary>An immutable result of one readiness evaluation. Results are not cached between evaluations.</summary>
    public sealed class HealthReport
    {
        internal HealthReport(HealthStatus status, IDictionary<string, HealthCheckResult> checks)
        {
            Status = status;
            Checks = new ReadOnlyDictionary<string, HealthCheckResult>(checks);
        }
        /// <summary>Gets the aggregate outcome, including the server lifecycle.</summary>
        public HealthStatus Status { get; }
        /// <summary>Gets the individual results. Lifecycle rejection can return an empty collection.</summary>
        public IReadOnlyDictionary<string, HealthCheckResult> Checks { get; }
    }
}
