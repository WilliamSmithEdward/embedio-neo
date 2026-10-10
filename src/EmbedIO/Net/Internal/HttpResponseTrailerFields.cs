using System;
using System.IO;
using System.Text;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    // Applications remain responsible for choosing fields whose definitions
    // permit trailer use (RFC 9110 6.5.1). Reject known early-only fields here.
    internal static class HttpResponseTrailerFields
    {
        internal static string LowercaseName(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            var characters = name.ToCharArray();
            for (var i = 0; i < characters.Length; i++)
            {
                var character = characters[i];
                if (!HttpRequestFraming.IsTokenCharacter(character)) throw new InvalidDataException("Invalid trailer field name.");
                if (character >= 'A' && character <= 'Z') characters[i] = (char)(character + ('a' - 'A'));
            }
            return new string(characters);
        }

        internal static void Validate(HpackField[] fields)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(0, true, fields, 0)); }
            catch (Http2ProtocolException error) { throw new InvalidDataException(error.Message, error); }
            foreach (var field in fields)
            {
                switch (field.Name)
                {
                    case "te":
                    case "trailer":
                    case "content-type":
                    case "content-encoding":
                    case "content-range":
                    case "authorization":
                    case "proxy-authorization":
                    case "www-authenticate":
                    case "proxy-authenticate":
                    case "set-cookie":
                    case "cache-control":
                    case "location":
                    case "retry-after":
                        throw new InvalidDataException("This field must be sent before response content.");
                }
            }
        }

        internal static byte[] ChunkEnd(HpackField[] fields)
        {
            Validate(fields);
            var text = new StringBuilder("0\r\n");
            foreach (var field in fields)
            {
                if (field.Name.Length + (long)field.Value.Length + text.Length + 4 > 32766)
                    throw new InvalidDataException("Response trailer section exceeds 32 KiB.");
                text.Append(field.Name).Append(": ").Append(field.Value).Append("\r\n");
            }
            text.Append("\r\n");
            return Encoding.GetEncoding(28591).GetBytes(text.ToString());
        }
    }
}
