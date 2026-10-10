using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
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

        internal static HashSet<string> Declaration(string[] names)
        {
            if (names == null) throw new ArgumentNullException(nameof(names));
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long size = 0;
            foreach (var name in names)
            {
                var wire = LowercaseName(name);
                Validate(new[] { new HpackField(wire, "") });
                if (!result.Add(wire)) continue;
                size += wire.Length + 2L;
                if (size > 32768) throw new InvalidDataException("Trailer declaration exceeds 32 KiB.");
            }
            if (result.Count == 0) throw new ArgumentException("Declare at least one trailer field.", nameof(names));
            return result;
        }

        internal static HpackField[] Snapshot(WebHeaderCollection trailers, HashSet<string> declared)
        {
            if (trailers == null) throw new ArgumentNullException(nameof(trailers));
            var fields = new List<HpackField>();
            long size = 0;
            foreach (var name in trailers.AllKeys)
            {
                if (name == null || !declared.Contains(name)) throw new InvalidDataException("Trailer field was not declared before response headers.");
                var wire = LowercaseName(name);
                foreach (var value in trailers.GetValues(name) ?? Array.Empty<string>())
                {
                    size += wire.Length + (long)value.Length + 32;
                    if (size > 32768) throw new InvalidDataException("Response trailer section exceeds 32 KiB.");
                    fields.Add(new HpackField(wire, value));
                }
            }
            var result = fields.ToArray();
            Validate(result);
            return result;
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
