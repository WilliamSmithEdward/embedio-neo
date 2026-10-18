namespace EmbedIO
{
    /// <summary>An immutable channel event. IDs are opaque and scoped to one hub lifetime.</summary>
    public sealed class ServerSentEvent
    {
        internal ServerSentEvent(string id, string data, string? eventName, byte[] frame, long timestamp, long sequence)
        {
            Id = id;
            Data = data;
            EventName = eventName;
            Frame = frame;
            Timestamp = timestamp;
            Sequence = sequence;
        }

        /// <summary>Gets the opaque reconnect cursor.</summary>
        public string Id { get; }

        /// <summary>Gets the original event data.</summary>
        public string Data { get; }

        /// <summary>Gets the event name, or null for a browser message event.</summary>
        public string? EventName { get; }
        internal byte[] Frame { get; }
        internal long Timestamp { get; }
        internal long Sequence { get; }
    }
}
