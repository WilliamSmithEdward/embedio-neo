using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace EmbedIO.Net.Internal
{
    // Retains the independently authored bounded first-body header encoder from
    // the performance work. Metadata/transport ownership stays with its callers.
    internal static class Http1HeaderEncoding
    {
        internal static MemoryStream Encode(HttpListenerResponse response, int bodyCount)
        {
            var encoding = WebServer.DefaultEncoding;
            var preamble = encoding.GetPreamble();
            var keys = response.Headers.AllKeys;
            var rawCookies = response.Headers.GetValues(HttpHeaderNames.SetCookie);
            string? cookies = null;
            if (response.ExistingCookies != null && response.ExistingCookies.Count > 0)
            {
                var builder = new StringBuilder();
                foreach (var cookie in response.ExistingCookies) ResponseCookieFields.AppendLine(builder, cookie, response);
                cookies = builder.ToString();
            }
            var version = response.ProtocolVersion == HttpVersion.Version11 ? "1.1"
                : response.ProtocolVersion == HttpVersion.Version10 ? "1.0" : response.ProtocolVersion.ToString();
            var status = response.StatusCode.ToString(CultureInfo.InvariantCulture);
            MemoryStream stream;
#if NET10_0_OR_GREATER
            // Common headers fit on the stack: concatenate without a builder, then
            // perform one encoding operation. Large fields use the exact-size path.
            Span<char> text = stackalloc char[2048];
            var characters = new HeaderWriter(text);
            WriteHeaderFields(response, ref characters, version, status, keys, cookies, rawCookies);
            if (!characters.Overflow)
            {
                var content = text.Slice(0, characters.Position);
                var size = checked(preamble.Length + encoding.GetByteCount(content));
                stream = new MemoryStream(GetHeaderBufferCapacity(response, size, preamble.Length, bodyCount));
                stream.SetLength(size);
                preamble.CopyTo(stream.GetBuffer(), 0);
                encoding.GetBytes(content, stream.GetBuffer().AsSpan(preamble.Length));
            }
            else
#endif
            {
                var writer = new HeaderWriter(encoding, null, preamble.Length);
                WriteHeaderFields(response, ref writer, version, status, keys, cookies, rawCookies);
                stream = new MemoryStream(GetHeaderBufferCapacity(response, writer.Position, preamble.Length, bodyCount));
                stream.SetLength(writer.Position);
                var buffer = stream.GetBuffer();
                Buffer.BlockCopy(preamble, 0, buffer, 0, preamble.Length);
                writer = new HeaderWriter(encoding, buffer, preamble.Length);
                WriteHeaderFields(response, ref writer, version, status, keys, cookies, rawCookies);
            }

            stream.Position = preamble.Length;

            return stream;
        }

        private static int GetHeaderBufferCapacity(HttpListenerResponse response, int headerSize, int preambleSize, int bodyCount)
        {
            if (bodyCount == 0 || response.SuppressesBody) return headerSize;
            var chunkSize = 0;
            if (response.SendChunked)
            {
                chunkSize = 3; // At least one hexadecimal digit and CRLF.
                for (var remaining = (uint)bodyCount >> 4; remaining != 0; remaining >>= 4) chunkSize++;
            }
            // Match ResponseStream's bounded first write exactly. Large headers and
            // bodies retain their existing transport write boundaries.
            var prefix = Math.Min(bodyCount, Math.Max(0, 16384 - (headerSize - preambleSize + chunkSize)));
            return checked(headerSize + chunkSize + prefix);
        }
        private static void WriteHeaderFields(HttpListenerResponse response, ref HeaderWriter writer, string version, string status,
            string[] keys, string? cookies, string[]? rawCookies)
        {
            writer.Append("HTTP/");
            writer.Append(version);
            writer.Append(" ");
            writer.Append(status);
            writer.Append(" ");
            writer.Append(response.StatusDescription);
            writer.Append("\r\n");
            foreach (var key in keys)
            {
                if (string.Equals(key, HttpHeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase)) continue;
                writer.Append(key);
                writer.Append(": ");
                writer.Append(response.Headers[key]);
                writer.Append("\r\n");
            }
            writer.Append(cookies);
            if (rawCookies != null)
                foreach (var value in rawCookies)
                {
                    writer.Append("Set-Cookie: ");
                    writer.Append(value);
                    writer.Append("\r\n");
                }
            writer.Append("\r\n");
        }

        // Count once, then encode directly into the one output allocation. Ordinary
        // and large headers never need an intermediate UTF-16 builder or string.
        private ref struct HeaderWriter
        {
            private readonly Encoding _encoding;
            private readonly byte[]? _buffer;
            internal int Position;
#if NET10_0_OR_GREATER
            private readonly Span<char> _characters;
            private readonly bool _textMode;
            internal bool Overflow;

            internal HeaderWriter(Span<char> characters)
            {
                _encoding = WebServer.DefaultEncoding;
                _buffer = null;
                _characters = characters;
                _textMode = true;
                Position = 0;
                Overflow = false;
            }
#endif

            internal HeaderWriter(Encoding encoding, byte[]? buffer, int position)
            {
                _encoding = encoding;
                _buffer = buffer;
                Position = position;
#if NET10_0_OR_GREATER
                _characters = default;
                _textMode = false;
                Overflow = false;
#endif
            }

            internal void Append(string? text)
            {
                if (text == null || text.Length == 0) return;
#if NET10_0_OR_GREATER
                if (_textMode)
                {
                    if (Overflow || text.Length > _characters.Length - Position) { Overflow = true; return; }
                    text.AsSpan().CopyTo(_characters.Slice(Position));
                    Position += text.Length;
                    return;
                }
#endif
                Position = checked(Position + (_buffer == null ? _encoding.GetByteCount(text)
                    : _encoding.GetBytes(text, 0, text.Length, _buffer, Position)));
            }
        }

    }
}
