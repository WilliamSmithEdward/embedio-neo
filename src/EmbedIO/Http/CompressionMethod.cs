namespace EmbedIO
{
    /// <summary>
    /// Specifies an HTTP content-coding method.
    /// </summary>
    public enum CompressionMethod : byte
    {
        /// <summary>
        /// Specifies no compression.
        /// </summary>
        None,

        /// <summary>
        /// Specifies "Deflate" compression.
        /// </summary>
        Deflate,

        /// <summary>
        /// Specifies GZip compression.
        /// </summary>
        Gzip,

        /// <summary>Specifies Brotli compression, supported by the .NET 10 asset.</summary>
        Brotli,
    }
}
