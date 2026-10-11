namespace EmbedIO.Net.Internal
{
    // RFC 9218 priority parameters carried in an RFC 9651 Dictionary.
    internal readonly struct HttpPriority
    {
        internal HttpPriority(int urgency, bool incremental) { Urgency = urgency; Incremental = incremental; }
        public int Urgency { get; }
        public bool Incremental { get; }
        internal static bool TryParse(string field, out HttpPriority priority)
        {
            priority = new HttpPriority(3, false);
            return field != null && field.Length <= 16384 && HttpStructuredFields.TryParsePriority(field, out priority);
        }
    }
}
