using System;
using System.Globalization;

namespace EmbedIO
{
    /// <summary>
    /// A minimum rate at which data must arrive, enforced after a grace period. Used by
    /// <see cref="WebServerOptionsBase.MinimumRequestBodyDataRate"/>.
    /// </summary>
    /// <remarks>
    /// <para>Modelled on <c>MinDataRate</c> in ASP.NET Core 10.0.13 Kestrel. The rate is measured
    /// only while the application has a read pending on the body, and is checked once per
    /// second: a body whose bytes read, divided by the time spent waiting for them, fall
    /// below <see cref="BytesPerSecond"/> once that time exceeds <see cref="GracePeriod"/> is
    /// refused.</para>
    /// </remarks>
    public sealed class MinimumDataRate
    {
        private MinimumDataRate() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="MinimumDataRate"/> class.
        /// </summary>
        /// <param name="bytesPerSecond">The minimum rate, in bytes per second.</param>
        /// <param name="gracePeriod">How long the rate may stay below the minimum before it is
        /// enforced, counted from the time the application first waits for data. It must be
        /// longer than one second, the interval at which the rate is checked.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytesPerSecond"/> is not
        /// a positive finite number, or <paramref name="gracePeriod"/> is one second or less or
        /// exceeds the largest supported timer interval.</exception>
        public MinimumDataRate(double bytesPerSecond, TimeSpan gracePeriod)
        {
            if (!(bytesPerSecond > 0) || double.IsInfinity(bytesPerSecond))
                throw new ArgumentOutOfRangeException(
                    nameof(bytesPerSecond),
                    "The rate must be a positive finite number of bytes per second."
                );

            if (
                gracePeriod <= EmbedIO.Internal.RequestBodyRateHeartbeat.Interval
                || gracePeriod.TotalMilliseconds > int.MaxValue
            )
                throw new ArgumentOutOfRangeException(
                    nameof(gracePeriod),
                    "The grace period must be longer than the check interval and fit a timer."
                );

            BytesPerSecond = bytesPerSecond;
            GracePeriod = gracePeriod;
        }

        /// <summary>
        /// Gets a value that removes a minimum data rate inherited from the server when it is
        /// assigned to <see cref="RequestLimitPolicy.MinimumRequestBodyDataRate"/>. Assigning it
        /// to <see cref="WebServerOptionsBase.MinimumRequestBodyDataRate"/> is the same as
        /// assigning null.
        /// </summary>
        public static MinimumDataRate None { get; } = new MinimumDataRate();

        /// <summary>
        /// Gets the minimum rate, in bytes per second. Zero for <see cref="None"/>.
        /// </summary>
        public double BytesPerSecond { get; }

        /// <summary>
        /// Gets how long the rate may stay below the minimum before it is enforced.
        /// <see cref="TimeSpan.Zero"/> for <see cref="None"/>.
        /// </summary>
        public TimeSpan GracePeriod { get; }
        internal bool IsNone => ReferenceEquals(this, None);

        /// <inheritdoc />
        public override string ToString() =>
            IsNone
                ? "None"
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} bytes per second after {1}",
                    BytesPerSecond,
                    GracePeriod
                );
    }
}
