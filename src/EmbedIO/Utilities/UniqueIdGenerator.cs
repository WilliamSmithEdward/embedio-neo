using System;

namespace EmbedIO.Utilities
{
    /// <summary>
    /// <para>Generates locally unique string IDs, mainly for logging purposes.</para>
    /// </summary>
    public static class UniqueIdGenerator
    {
        /// <summary>
        /// Generates and returns a unique ID.
        /// </summary>
        /// <returns>The generated ID.</returns>
        public static string GetNext()
        {
#if NET
            // The first 22 base64 characters of a new GUID, without the intermediate
            // byte array and 24-character string. Every request context takes one.
            Span<byte> bytes = stackalloc byte[16];
            Span<char> chars = stackalloc char[24];
            if (Guid.NewGuid().TryWriteBytes(bytes) && Convert.TryToBase64Chars(bytes, chars, out var written) && written == 24)
                return new string(chars.Slice(0, 22));
#endif
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 22);
        }
    }
}
