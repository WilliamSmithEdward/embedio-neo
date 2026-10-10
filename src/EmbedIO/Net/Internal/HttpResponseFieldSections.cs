using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    internal static class HttpResponseFieldSections
    {
        internal static HpackField[] Informational(int statusCode, WebHeaderCollection headers)
        {
            if (statusCode < 100 || statusCode >= 200 || statusCode == 101)
                throw new ArgumentOutOfRangeException(nameof(statusCode), "An informational section cannot switch protocols or be final.");
            if (headers == null) throw new ArgumentNullException(nameof(headers));
            var fields = new List<HpackField> { new(":status", statusCode.ToString(CultureInfo.InvariantCulture)) };
            long size = 32;
            foreach (var name in headers.AllKeys)
            {
                if (name == null) throw new InvalidDataException("Null informational field name.");
                var wireName = HttpResponseTrailerFields.LowercaseName(name);
                foreach (var value in headers.GetValues(name) ?? Array.Empty<string>())
                {
                    size += wireName.Length + (long)value.Length + 32;
                    if (size > 32768) throw new InvalidDataException("Informational field section exceeds 32 KiB.");
                    fields.Add(new HpackField(wireName, value));
                }
            }
            var result = fields.ToArray();
            Http2ResponseHeaders.Validate(result, "GET", false);
            return result;
        }

        internal static byte[] Http1Informational(int statusCode, HpackField[] fields)
        {
            var text = new StringBuilder("HTTP/1.1 ").Append(statusCode.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(HttpListenerResponseHelper.GetStatusDescription(statusCode)).Append("\r\n");
            for (var i = 1; i < fields.Length; i++)
                text.Append(fields[i].Name).Append(": ").Append(fields[i].Value).Append("\r\n");
            text.Append("\r\n");
            return Encoding.GetEncoding(28591).GetBytes(text.ToString());
        }
    }
}
