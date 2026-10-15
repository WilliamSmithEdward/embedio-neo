namespace EmbedIO
{
    /// <summary>Describes one capsule in a negotiated reliable carrier.</summary>
    public readonly struct HttpCapsuleHeader
    {
        internal HttpCapsuleHeader(long type, long length) { Type = type; Length = length; }
        /// <summary>Gets the unsigned 62-bit type, including unknown extension types.</summary>
        public long Type { get; }
        /// <summary>Gets the declared payload length without buffering that payload.</summary>
        public long Length { get; }
    }
}
