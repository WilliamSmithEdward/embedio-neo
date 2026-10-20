using System;

namespace EmbedIO.WebSockets
{
    /// <summary>Immutable optional limits on outstanding module message deliveries.</summary>
    /// <remarks>
    /// A delivery includes partial and queued messages and the module's unfinished callback Task.
    /// Payload bytes are logical reservations, not total heap usage or application-retained data.
    /// Null leaves a limit unconfigured. Empty messages still consume message counts.
    /// </remarks>
    public sealed class WebSocketReceiveLimits
    {
        /// <summary>Initializes limits for one connection and the entire accepting module.</summary>
        /// <param name="maximumOutstandingMessagesPerConnection">Positive connection count, or null.</param>
        /// <param name="maximumOutstandingPayloadBytesPerConnection">Nonnegative connection bytes, or null.</param>
        /// <param name="maximumOutstandingMessagesPerModule">Positive aggregate count, or null.</param>
        /// <param name="maximumOutstandingPayloadBytesPerModule">Nonnegative aggregate bytes, or null.</param>
        /// <exception cref="ArgumentOutOfRangeException">A finite count is nonpositive or byte limit is negative.</exception>
        public WebSocketReceiveLimits(
            int? maximumOutstandingMessagesPerConnection = null,
            long? maximumOutstandingPayloadBytesPerConnection = null,
            int? maximumOutstandingMessagesPerModule = null,
            long? maximumOutstandingPayloadBytesPerModule = null
        )
        {
            try
            {
                Limits = new Internal.WebSocketDeliveryLimits(
                    maximumOutstandingMessagesPerConnection,
                    maximumOutstandingPayloadBytesPerConnection,
                    maximumOutstandingMessagesPerModule,
                    maximumOutstandingPayloadBytesPerModule
                );
            }

            catch (ArgumentOutOfRangeException error)
            {
                var parameter = error.ParamName switch
                {
                    "connectionMessages" => nameof(maximumOutstandingMessagesPerConnection),
                    "connectionBytes" => nameof(maximumOutstandingPayloadBytesPerConnection),
                    "moduleMessages" => nameof(maximumOutstandingMessagesPerModule),
                    "moduleBytes" => nameof(maximumOutstandingPayloadBytesPerModule),
                    _ => error.ParamName
                };
                throw new ArgumentOutOfRangeException(
                    parameter,
                    "Finite counts must be positive and byte limits nonnegative."
                );
            }
        }

        internal Internal.WebSocketDeliveryLimits Limits { get; }

        /// <summary>Gets the maximum outstanding count on one connection, or null.</summary>
        public int? MaximumOutstandingMessagesPerConnection => Limits.ConnectionMessages;

        /// <summary>Gets the maximum outstanding logical payload bytes on one connection, or null.</summary>
        public long? MaximumOutstandingPayloadBytesPerConnection => Limits.ConnectionBytes;

        /// <summary>Gets the maximum outstanding count across the module, or null.</summary>
        public int? MaximumOutstandingMessagesPerModule => Limits.ModuleMessages;

        /// <summary>Gets the maximum outstanding logical payload bytes across the module, or null.</summary>
        public long? MaximumOutstandingPayloadBytesPerModule => Limits.ModuleBytes;
    }
}
