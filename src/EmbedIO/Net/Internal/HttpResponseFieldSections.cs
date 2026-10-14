using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal static class HttpResponseFieldSections
    {
        internal static void ValidateFinalStatus(int statusCode)
        {
            if (statusCode < 200 || statusCode > 599)
                throw new ArgumentOutOfRangeException(nameof(statusCode), "A final response status must be between 200 and 599. Use informational response or protocol handoff APIs for 1xx.");
        }

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

        private sealed class CachedDate
        {
            internal CachedDate(long second, string text) { Second = second; Text = text; }
            internal readonly long Second;
            internal readonly string Text;
        }

        private sealed class CachedContentType
        {
            internal CachedContentType(string contentType, string charset, string text) { ContentType = contentType; Charset = charset; Text = text; }
            internal readonly string ContentType;
            internal readonly string Charset;
            internal readonly string Text;
        }

        private static CachedDate? _date;
        private static CachedContentType? _contentTypeWithCharset;

        // RFC 1123 dates have one-second resolution, so one string serves a whole second.
        internal static string CurrentDate()
        {
            var now = DateTime.UtcNow;
            var second = now.Ticks / TimeSpan.TicksPerSecond;
            var cached = Volatile.Read(ref _date);
            if (cached != null && cached.Second == second) return cached.Text;
            var text = HttpDate.Format(now);
            Volatile.Write(ref _date, new CachedDate(second, text));
            return text;
        }

        // Appends the charset unless the media type already declares one. The result
        // depends only on both inputs, so the last pair is reused.
        internal static string WithCharset(string contentType, string charset)
        {
            var cached = Volatile.Read(ref _contentTypeWithCharset);
            if (cached != null && string.Equals(cached.ContentType, contentType, StringComparison.Ordinal)
                && string.Equals(cached.Charset, charset, StringComparison.Ordinal))
                return cached.Text;
            var text = MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.CharSet != null
                ? contentType : contentType + "; charset=" + charset;
            Volatile.Write(ref _contentTypeWithCharset, new CachedContentType(contentType, charset, text));
            return text;
        }
    }
}
