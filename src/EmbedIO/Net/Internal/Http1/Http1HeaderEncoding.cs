using System;
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // A response head encoded into a pooled array: the bytes [0, Length) are the head, and
    // the array also holds the room the output owner asked for (the first body segment it
    // appends behind the head), so the head and the first body bytes leave in one transport
    // write without a copy of the head. Dispose clears the head bytes and returns the lease;
    // the owner clears what it appended itself before that.
    internal readonly struct Http1Head : IDisposable
    {
        internal Http1Head(byte[] buffer, int length)
        {
            Buffer = buffer;
            Length = length;
        }

        internal byte[] Buffer { get; }
        internal int Length { get; }
        internal bool IsEmpty => Buffer == null;

        public void Dispose()
        {
            if (Buffer == null) return;
            Array.Clear(Buffer, 0, Length);
            ArrayPool<byte>.Shared.Return(Buffer);
        }
    }

    // Retains the independently authored bounded first-body header encoder from
    // the performance work. Metadata/transport ownership stays with its callers.
    internal static class Http1HeaderEncoding
    {
        // Status lines repeat a few codes; the code text is shared instead of formatted per response.
        private static readonly string?[] StatusTexts = new string?[600];

        internal static Http1Head Encode(HttpListenerResponse response, int bodyCount, bool ending)
        {
            var rawCookies = response.Headers.GetValues(HttpHeaderNames.SetCookie);
            var cookies = response.ExistingCookies != null && response.ExistingCookies.Count > 0
                ? EncodeCookies(response) : null;
            ValidateHead(response, rawCookies);
            var version = response.ProtocolVersion == HttpVersion.Version11 ? "1.1"
                : response.ProtocolVersion == HttpVersion.Version10 ? "1.0" : response.ProtocolVersion.ToString();
            var status = StatusText(response.StatusCode);
            var chunked = response.SendChunked && !response.SuppressesBody;
            var endingLength = ending && chunked && bodyCount != 0 ? ResponseStream.EndingLength(response) : 0;
#if NET10_0_OR_GREATER
            // Common headers fit on the stack. Compile the large-header encoder only if needed.
            Span<char> text = stackalloc char[2048];
            var characters = new HeaderWriter(text);
            WriteHeaderFields(response, ref characters, version, status, cookies, rawCookies);
            if (!characters.Overflow)
            {
                var encoding = WebServer.DefaultEncoding;
                var content = text.Slice(0, characters.Position);
                var size = encoding.GetByteCount(content);
                var buffer = ArrayPool<byte>.Shared.Rent(size + ResponseStream.FirstSegmentReserve(size, bodyCount, chunked, endingLength));
                encoding.GetBytes(content, buffer);
                return new Http1Head(buffer, size);
            }
#endif
            return EncodeLargeHead(response, bodyCount, chunked, endingLength, version, status, cookies, rawCookies);
        }

        // Keep uncommon work out of the first ordinary response's JIT compilation.
        private static string EncodeCookies(HttpListenerResponse response)
        {
            var builder = new StringBuilder();
            foreach (var cookie in response.ExistingCookies ?? throw new InvalidOperationException("Missing response cookies."))
                ResponseCookieFields.AppendLine(builder, cookie, response);
            return builder.ToString();
        }

        private static Http1Head EncodeLargeHead(HttpListenerResponse response, int bodyCount, bool chunked, int endingLength,
            string version, string status, string? cookies, string[]? rawCookies)
        {
            var encoding = WebServer.DefaultEncoding;
            var writer = new HeaderWriter(encoding, null, 0);
            WriteHeaderFields(response, ref writer, version, status, cookies, rawCookies);
            var size = writer.Position;
            var buffer = ArrayPool<byte>.Shared.Rent(size + ResponseStream.FirstSegmentReserve(size, bodyCount, chunked, endingLength));
            writer = new HeaderWriter(encoding, buffer, 0);
            WriteHeaderFields(response, ref writer, version, status, cookies, rawCookies);
            return new Http1Head(buffer, size);
        }

        private static string StatusText(int statusCode)
        {
            if ((uint)statusCode >= (uint)StatusTexts.Length) return statusCode.ToString(CultureInfo.InvariantCulture);
            return StatusTexts[statusCode] ??= statusCode.ToString(CultureInfo.InvariantCulture);
        }

        // WebHeaderCollection refuses a bare CRLF in a value but accepts one followed by a
        // space, which is obs-fold, and RFC 9112, Section 5.2 says a server must not generate
        // it. Nothing above 255 survives the ISO-8859-1 head either. The other three response
        // paths validate through Http2RequestHeaders.ValidateField, so HTTP/1 is held to the
        // same rule here, before any byte of the head exists. Values are scanned once; the
        // encoder reads them again immediately afterwards.
        private static void ValidateHead(HttpListenerResponse response, string[]? rawCookies)
        {
            if (!HttpTokenSyntax.IsValidFieldValue(response.StatusDescription))
                throw new InvalidOperationException("The reason phrase carries control characters.");

            var headers = response.Headers;
            for (var index = 0; index < headers.Count; index++)
            {
                var value = headers.Get(index);
                if (value != null && !HttpTokenSyntax.IsValidFieldValue(value))
                    throw new InvalidOperationException($"The {headers.GetKey(index)} response field carries control characters.");
            }

            if (rawCookies == null) return;
            foreach (var cookie in rawCookies)
                if (cookie != null && !HttpTokenSyntax.IsValidFieldValue(cookie))
                    throw new InvalidOperationException("A Set-Cookie response field carries control characters.");
        }

        private static void WriteHeaderFields(HttpListenerResponse response, ref HeaderWriter writer, string version, string status,
            string? cookies, string[]? rawCookies)
        {
            writer.Append("HTTP/");
            writer.Append(version);
            writer.Append(" ");
            writer.Append(status);
            writer.Append(" ");
            writer.Append(response.StatusDescription);
            writer.Append("\r\n");
            // Positional access keeps insertion order and the comma-joined value of each
            // field, without the AllKeys array or a second lookup per name.
            var headers = response.Headers;
            for (var index = 0; index < headers.Count; index++)
            {
                var key = headers.GetKey(index);
                if (string.Equals(key, HttpHeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase)) continue;
                writer.Append(key);
                writer.Append(": ");
                writer.Append(headers.Get(index));
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
