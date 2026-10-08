using System;
using System.IO;

namespace EmbedIO.Net.Internal.Http2
{
    internal readonly struct Http2ResponseHeaders
    {
        private Http2ResponseHeaders(int status, bool bodyAllowed, long? length)
        { Status = status; BodyAllowed = bodyAllowed; ContentLength = length; }
        public int Status { get; }
        public bool BodyAllowed { get; }
        public long? ContentLength { get; }

        internal static Http2ResponseHeaders Validate(HpackField[] fields, string method, bool endStream)
        {
            try { return ValidateCore(fields, method, endStream); }
            catch (Http2ProtocolException error) { throw new InvalidDataException(error.Message, error); }
        }

        private static Http2ResponseHeaders ValidateCore(HpackField[] fields, string method, bool endStream)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            var status = 0;
            var regular = false;
            long? length = null;
            foreach (var field in fields)
            {
                // Outbound validation occurs before encoder state changes.
                Http2RequestHeaders.ValidateField(field, 0);
                if (field.Name[0] == ':')
                {
                    if (regular || status != 0 || field.Name != ":status" || field.Value.Length != 3)
                        throw new InvalidDataException("Invalid response pseudo-header.");
                    foreach (var digit in field.Value)
                    {
                        if (digit < '0' || digit > '9') throw new InvalidDataException("Invalid response status.");
                        status = status * 10 + digit - '0';
                    }
                    if (status < 100 || status > 599 || status == 101) throw new InvalidDataException("Invalid HTTP/2 response status.");
                    continue;
                }
                regular = true;
                Http2RequestHeaders.ValidateConnectionField(field, 0);
                if (field.Name == "te") throw new InvalidDataException("TE is only valid in requests.");
                if (field.Name == "content-length") length = Http2RequestHeaders.ParseLength(field.Value, length, 0);
            }
            if (status == 0) throw new InvalidDataException("Missing response status.");
            var informational = status < 200;
            var tunnel = method == "CONNECT" && status >= 200 && status < 300;
            if (length.HasValue && (informational || status == 204 || tunnel))
                throw new InvalidDataException("Content-Length is forbidden for this response.");
            if (informational && endStream) throw new InvalidDataException("Informational response cannot end a stream.");
            if (status == 205 && length.GetValueOrDefault() != 0) throw new InvalidDataException("Reset Content cannot declare a nonempty body.");
            var bodyAllowed = !informational && method != "HEAD" && status != 204 && status != 205 && status != 304;
            if (bodyAllowed && endStream && length.GetValueOrDefault() != 0)
                throw new InvalidDataException("Response ends before Content-Length.");
            return new Http2ResponseHeaders(status, bodyAllowed, length);
        }
    }
}
