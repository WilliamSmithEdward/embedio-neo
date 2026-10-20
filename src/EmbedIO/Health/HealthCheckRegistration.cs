using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Health
{
    /// <summary>An immutable readiness check. A service snapshots registrations when constructed.</summary>
    public sealed class HealthCheckRegistration
    {
        /// <summary>Creates a named check with a finite deadline (five seconds by default).</summary>
        /// <param name="name">A unique name of 1 to 64 characters.</param>
        /// <param name="check">The check. Honor cancellation and avoid blocking or mutating application state.</param>
        /// <param name="timeout">A timeout from one millisecond through one minute.</param>
        public HealthCheckRegistration(
            string name,
            Func<CancellationToken, Task<HealthCheckResult>> check,
            TimeSpan? timeout = null
        )
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
                throw new ArgumentException("A check needs a name of 1 to 64 characters.", nameof(name));

            Name = name;
            Check = check ?? throw new ArgumentNullException(nameof(check));
            Timeout = timeout ?? TimeSpan.FromSeconds(5);

            if (Timeout < TimeSpan.FromMilliseconds(1) || Timeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        /// <summary>Gets the check name.</summary>
        public string Name { get; }

        /// <summary>Gets the callback.</summary>
        public Func<CancellationToken, Task<HealthCheckResult>> Check { get; }

        /// <summary>Gets the deadline.</summary>
        public TimeSpan Timeout { get; }
    }
}
