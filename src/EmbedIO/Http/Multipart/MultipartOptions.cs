using System;

namespace EmbedIO
{
    /// <summary>Bounds streaming multipart parsing. A reader snapshots these values at construction.</summary>
    public sealed class MultipartOptions
    {
        /// <summary>Gets or sets the maximum decoded request size, including framing and epilogue. Default: 128 MiB.</summary>
        public long MaxTotalBytes { get; set; } = 134217728;

        /// <summary>Gets or sets the maximum body size of each file part. Default: 64 MiB.</summary>
        public long MaxFileBytes { get; set; } = 67108864;

        /// <summary>Gets or sets the maximum body size of each non-file part. Default: 64 KiB.</summary>
        public long MaxFieldBytes { get; set; } = 65536;

        /// <summary>Gets or sets the maximum number of parts. Default: 128.</summary>
        public int MaxParts { get; set; } = 128;

        /// <summary>Gets or sets the maximum number of parts with a filename (including an empty filename). Default: 32.</summary>
        public int MaxFiles { get; set; } = 32;

        /// <summary>Gets or sets the maximum number of parts without a filename. Default: 128.</summary>
        public int MaxFields { get; set; } = 128;

        /// <summary>Gets or sets the maximum bytes in each header block, including CRLFs, and each framing line. Default: 16 KiB.</summary>
        public int MaxHeaderBytes { get; set; } = 16384;

        /// <summary>Gets or sets the maximum number of headers per part. Default: 32.</summary>
        public int MaxHeaders { get; set; } = 32;

        internal MultipartOptions Snapshot()
        {
            if (
                MaxTotalBytes < 0
                || MaxFileBytes < 0
                || MaxFieldBytes < 0
                || MaxParts < 0
                || MaxFiles < 0
                || MaxFields < 0
                || MaxHeaderBytes < 0
                || MaxHeaders < 0
            )
                throw new ArgumentOutOfRangeException(
                    nameof(MultipartOptions),
                    "Multipart limits must be nonnegative."
                );

            return (MultipartOptions)MemberwiseClone();
        }
    }
}
