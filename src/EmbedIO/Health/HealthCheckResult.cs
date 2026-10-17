using System;

namespace EmbedIO.Health
{
    /// <summary>An immutable, bounded application-supplied health result. Never put secrets in descriptions.</summary>
    public readonly struct HealthCheckResult
    {
        /// <summary>Creates a result with an optional description of at most 512 characters.</summary>
        /// <param name="status">The check outcome.</param>
        /// <param name="description">Optional diagnostic text, excluded from minimal HTTP responses.</param>
        public HealthCheckResult(HealthStatus status, string? description = null)
        {
            if (status < HealthStatus.Unhealthy || status > HealthStatus.Healthy)
                throw new ArgumentOutOfRangeException(nameof(status));
            if (description?.Length > 512) throw new ArgumentOutOfRangeException(nameof(description));
            Status = status;
            Description = description;
        }

        /// <summary>Gets the check outcome.</summary>
        public HealthStatus Status { get; }
        /// <summary>Gets optional application diagnostic text.</summary>
        public string? Description { get; }
    }
}
