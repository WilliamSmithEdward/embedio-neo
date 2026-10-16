using System.Collections.Generic;
using System.IO;

namespace EmbedIO
{
    /// <summary>A multipart form part. Metadata is untrusted client input; filenames are never filesystem paths.</summary>
    public sealed class MultipartSection
    {
        internal MultipartSection(string name, string? fileName, string? contentType, IReadOnlyDictionary<string, string> headers, Stream body)
        {
            Name = name;
            FileName = fileName;
            ContentType = contentType;
            Headers = headers;
            Body = body;
        }

        /// <summary>Gets the form field name, without percent decoding.</summary>
        public string Name { get; }

        /// <summary>Gets the untrusted filename, or null for a non-file field. A valid UTF-8 filename* takes precedence; an empty filename still denotes a file.</summary>
        public string? FileName { get; }

        /// <summary>Gets the declared content type, or null. No content sniffing is performed.</summary>
        public string? ContentType { get; }

        /// <summary>Gets the part's case-insensitive, read-only headers.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>Gets the forward-only body stream. Read it before advancing the reader; disposing it does not close the request.</summary>
        public Stream Body { get; }
    }
}
