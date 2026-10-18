using System;

namespace EmbedIO.Tus
{
    /// <summary>An immutable upload snapshot. Metadata is untrusted client input, never a filesystem path.</summary>
    public sealed class TusUpload
    {
        internal TusUpload(string id, long length, long offset, DateTimeOffset expires, string metadata)
        {
            Id = id;
            Length = length;
            Offset = offset;
            Expires = expires;
            Metadata = metadata;
        }

        /// <summary>Gets the server-generated opaque identifier.</summary>
        public string Id { get; }

        /// <summary>Gets the fixed declared length.</summary>
        public long Length { get; }

        /// <summary>Gets the committed byte offset.</summary>
        public long Offset { get; }

        /// <summary>Gets the fixed expiration, also applicable to complete files.</summary>
        public DateTimeOffset Expires { get; }

        /// <summary>Gets the original validated tus Upload-Metadata header, with Base64-encoded values.</summary>
        public string Metadata { get; }

        /// <summary>Gets whether all declared bytes have been committed.</summary>
        public bool IsComplete => Length == Offset;
    }
}
