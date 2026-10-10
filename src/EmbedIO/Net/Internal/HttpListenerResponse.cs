using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    /// <summary>
    /// Represents an HTTP Listener's response.
    /// </summary>
    /// <seealso cref="IDisposable" />
    internal sealed class HttpListenerResponse : IHttpResponse, IDisposable
    {
        private readonly HttpConnection _connection;
        private readonly HttpListenerRequest _request;
        private readonly string _id;
        private int _disposed;
        private string _contentType = MimeType.Html; // Same default value as Microsoft's implementation
        private CookieList? _cookies;
        private bool? _keepAlive;
        private ResponseStream? _outputStream;
        private int _statusCode = 200;
        private bool _chunked;

        internal HttpListenerResponse(HttpListenerContext context)
        {
            _request = context.HttpListenerRequest;
            _connection = context.Connection;
            _id = context.Id;
        }

        /// <inheritdoc />
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
        /// <exception cref="InvalidOperationException">This property is being set and headers were already sent.</exception>
        public long ContentLength64
        {
            get => Headers.ContainsKey(HttpHeaderNames.ContentLength) && long.TryParse(Headers[HttpHeaderNames.ContentLength], out var val) ? val : 0;

            set
            {
                EnsureCanChangeHeaders();
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), "Must be >= 0");
                }

                Headers[HttpHeaderNames.ContentLength] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
        /// <exception cref="InvalidOperationException">This property is being set and headers were already sent.</exception>
        /// <exception cref="ArgumentNullException">This property is being set to <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">This property is being set to the empty string.</exception>
        public string ContentType
        {
            get => _contentType;

            set
            {
                EnsureCanChangeHeaders();
                _contentType = Validate.NotNullOrEmpty(nameof(value), value);
            }
        }

        /// <inheritdoc />
        public ICookieCollection Cookies => CookieCollection;

        /// <inheritdoc />
        public WebHeaderCollection Headers { get; } = new WebHeaderCollection();

        /// <inheritdoc />
        public bool KeepAlive
        {
            // The context is constructed before request parsing. Reading the default
            // here avoids caching a premature request policy; an explicit override wins.
            get => _keepAlive ?? _request.KeepAlive;

            set
            {
                EnsureCanChangeHeaders();
                _keepAlive = value;
            }
        }

        /// <inheritdoc />
        public Stream OutputStream => _outputStream ??= _connection.GetResponseStream();

        /// <inheritdoc />
        public Version ProtocolVersion => _request.ProtocolVersion.Minor == 0 ? HttpVersion.Version10 : HttpVersion.Version11;

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
        /// <exception cref="InvalidOperationException">This property is being set and headers were already sent.</exception>
        public bool SendChunked
        {
            get => _chunked;

            set
            {
                EnsureCanChangeHeaders();
                _chunked = value;
            }
        }

        /// <inheritdoc />
        /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
        /// <exception cref="InvalidOperationException">This property is being set and headers were already sent.</exception>
        public int StatusCode
        {
            get => _statusCode;

            set
            {
                EnsureCanChangeHeaders();
                if (value < 100 || value > 999)
                {
                    throw new ArgumentOutOfRangeException(nameof(StatusCode), "StatusCode must be between 100 and 999.");
                }

                _statusCode = value;
                StatusDescription = HttpListenerResponseHelper.GetStatusDescription(value);
            }
        }

        /// <inheritdoc />
        public string StatusDescription { get; set; } = "OK";

        internal CookieList CookieCollection
        {
            get => _cookies ??= new CookieList();
            set => _cookies = value;
        }

        internal bool HeadersSent { get; set; }

        internal bool SuppressesBody => IsHeadResponse || _statusCode < 200 || _statusCode is 204 or 304;

        internal bool IsHeadResponse => _request.HttpVerb == HttpVerbs.Head;

        void IDisposable.Dispose() => Close(true);

        public void Close() => Close(false);

        internal void Abort() => Close(true, true);

        /// <inheritdoc />
        public void SetCookie(Cookie cookie)
        {
            if (cookie == null)
            {
                throw new ArgumentNullException(nameof(cookie));
            }

            if (_cookies != null)
            {
                if (_cookies.Any(c => cookie.Name == c.Name && cookie.Domain == c.Domain && cookie.Path == c.Path))
                {
                    throw new ArgumentException("The cookie already exists.");
                }
            }
            else
            {
                _cookies = new CookieList();
            }

            _cookies.Add(cookie);
        }

        internal MemoryStream SendHeaders(bool closing)
        {
            if (_contentType != null)
            {
                var encoding = ContentEncoding;
                var hasCharset = _contentType.IndexOf(";", System.StringComparison.Ordinal) >= 0
                    && MediaTypeHeaderValue.TryParse(_contentType, out var parsedType)
                    && parsedType.CharSet != null;
                var contentTypeValue = encoding != null && !hasCharset
                    ? $"{_contentType}; charset={encoding.WebName}"
                    : _contentType;

                Headers.Add(HttpHeaderNames.ContentType, contentTypeValue);
            }

            if (Headers[HttpHeaderNames.Server] == null)
            {
                Headers.Add(HttpHeaderNames.Server, WebServer.Signature);
            }

            if (Headers[HttpHeaderNames.Date] == null)
            {
                Headers.Add(HttpHeaderNames.Date, HttpDate.Format(DateTime.UtcNow));
            }

            if (_statusCode < 200 || _statusCode is 204 or 304)
            {
                // No message body or terminating chunk follows these response heads.
                // A 304 may retain explicit selected-representation metadata.
                if (_statusCode < 200 || _statusCode == 204)
                {
                    Headers.Remove(HttpHeaderNames.ContentLength);
                    Headers.Remove(HttpHeaderNames.TransferEncoding);
                }
                _chunked = false;
            }
            else if (closing)
            {
                if (_request.HttpVerb != HttpVerbs.Head)
                    Headers[HttpHeaderNames.ContentLength] = "0";
                _chunked = false;
            }
            else
            {
                if (ProtocolVersion < HttpVersion.Version11)
                {
                    _chunked = false;
                }

                var haveContentLength = !_chunked
                                     && Headers.ContainsKey(HttpHeaderNames.ContentLength)
                                     && long.TryParse(Headers[HttpHeaderNames.ContentLength], out var contentLength)
                                     && contentLength >= 0L;

                if (!haveContentLength)
                {
                    Headers.Remove(HttpHeaderNames.ContentLength);
                    if (ProtocolVersion >= HttpVersion.Version11)
                    {
                        _chunked = true;
                    }
                }
            }

            if (_chunked)
            {
                Headers.Add(HttpHeaderNames.TransferEncoding, "chunked");
            }

            //// Apache forces closing the connection for these status codes:
            //// HttpStatusCode.BadRequest            400
            //// HttpStatusCode.RequestTimeout        408
            //// HttpStatusCode.LengthRequired        411
            //// HttpStatusCode.RequestEntityTooLarge 413
            //// HttpStatusCode.RequestUriTooLong     414
            //// HttpStatusCode.InternalServerError   500
            //// HttpStatusCode.ServiceUnavailable    503
            var keepAlive = _statusCode switch
            {
                400 => false,
                408 => false,
                411 => false,
                413 => false,
                414 => false,
                500 => false,
                503 => false,
                _ => !_connection.IsDraining && KeepAlive
            };

            // HTTP/1.0 has no chunked delimiter. An unknown-length body ends at
            // transport EOF even when the client requests persistent service.
            if (ProtocolVersion < HttpVersion.Version11 && !SuppressesBody
                && (!long.TryParse(Headers[HttpHeaderNames.ContentLength], out var framedLength) || framedLength < 0))
                keepAlive = false;

            // RFC 9931 section 8: bytes after a rejected HTTP/1.1 CONNECT
            // might already belong to the requested tunnel, never a successor.
            if (ProtocolVersion == HttpVersion.Version11 && _request.HttpMethod == "CONNECT" && _statusCode >= 300)
                keepAlive = false;

            _keepAlive = keepAlive;
            if (keepAlive)
            {
                Headers.Add(HttpHeaderNames.Connection, "keep-alive");
                if (ProtocolVersion >= HttpVersion.Version11)
                {
                    Headers.Add(HttpHeaderNames.KeepAlive, "timeout=15");
                }
            }
            else
            {
                Headers.Add(HttpHeaderNames.Connection, "close");
            }

            return WriteHeaders();
        }

        private void AppendSetCookieHeader(StringBuilder sb, Cookie cookie)
        {
            if (cookie.Name.Length == 0)
            {
                return;
            }

            _ = sb.Append("Set-Cookie: ");
            AppendCookieHeaderValue(sb, cookie, this);
            _ = sb.Append("\r\n");
        }

        internal static void AppendCookieHeaderValue(StringBuilder sb, Cookie cookie, IHttpResponse? response = null)
        {
            if (cookie.Version > 0)
            {
                _ = sb.Append("Version=").Append(cookie.Version).Append("; ");
            }

            _ = sb
                .Append(cookie.Name)
                .Append('=')
                .Append(cookie.Value);

            if (cookie.Expires != DateTime.MinValue)
            {
                _ = sb
                    .Append("; Expires=")
                    .Append(HttpDate.Format(cookie.Expires));
            }

            if (!string.IsNullOrEmpty(cookie.Path))
            {
                _ = sb.Append("; Path=").Append(QuotedString(cookie, cookie.Path));
            }

            if (!string.IsNullOrEmpty(cookie.Domain))
            {
                _ = sb.Append("; Domain=").Append(QuotedString(cookie, cookie.Domain));
            }

            if (!string.IsNullOrEmpty(cookie.Port))
            {
                _ = sb.Append("; Port=").Append(cookie.Port);
            }

            if (cookie.Secure)
            {
                _ = sb.Append("; Secure");
            }

            if (cookie.HttpOnly)
            {
                _ = sb.Append("; HttpOnly");
            }

            if (response != null) CookieSameSiteStore.Append(sb, response, cookie);

        }

        private static string QuotedString(Cookie cookie, string value)
            => cookie.Version == 0 || value.IsToken() ? value : "\"" + EmbedIO.Internal.StringOperations.ReplaceOrdinal(value, "\"", "\\\"") + "\"";

        private void Close(bool force, bool abort = false)
        {
            // A completed response may outlive its TCP connection's current request.
            // Its repeated close/dispose must never close that newer request.
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            if (abort) _connection.ForceClose();
            else _connection.Close(force);
        }

        private MemoryStream WriteHeaders()
        {
            var encoding = WebServer.DefaultEncoding;
            var preamble = encoding.GetPreamble();
            var keys = Headers.AllKeys;
            var rawCookies = Headers.GetValues(HttpHeaderNames.SetCookie);
            string? cookies = null;
            if (_cookies != null && _cookies.Count > 0)
            {
                var builder = new StringBuilder();
                foreach (var cookie in _cookies) AppendSetCookieHeader(builder, cookie);
                cookies = builder.ToString();
            }
            var version = ProtocolVersion == HttpVersion.Version11 ? "1.1"
                : ProtocolVersion == HttpVersion.Version10 ? "1.0" : ProtocolVersion.ToString();
            var status = _statusCode.ToString(CultureInfo.InvariantCulture);
            MemoryStream stream;
#if NET10_0_OR_GREATER
            // Common headers fit on the stack: concatenate without a builder, then
            // perform one encoding operation. Large fields use the exact-size path.
            Span<char> text = stackalloc char[2048];
            var characters = new HeaderWriter(text);
            WriteHeaderFields(ref characters, version, status, keys, cookies, rawCookies);
            if (!characters.Overflow)
            {
                var content = text.Slice(0, characters.Position);
                var size = checked(preamble.Length + encoding.GetByteCount(content));
                stream = new MemoryStream(size);
                stream.SetLength(size);
                preamble.CopyTo(stream.GetBuffer(), 0);
                encoding.GetBytes(content, stream.GetBuffer().AsSpan(preamble.Length));
            }
            else
#endif
            {
                var writer = new HeaderWriter(encoding, null, preamble.Length);
                WriteHeaderFields(ref writer, version, status, keys, cookies, rawCookies);
                stream = new MemoryStream(writer.Position);
                stream.SetLength(writer.Position);
                var buffer = stream.GetBuffer();
                Buffer.BlockCopy(preamble, 0, buffer, 0, preamble.Length);
                writer = new HeaderWriter(encoding, buffer, preamble.Length);
                WriteHeaderFields(ref writer, version, status, keys, cookies, rawCookies);
            }
            _outputStream ??= _connection.GetResponseStream();
            stream.Position = preamble.Length;
            HeadersSent = true;
            return stream;
        }

        private void WriteHeaderFields(ref HeaderWriter writer, string version, string status,
            string[] keys, string? cookies, string[]? rawCookies)
        {
            writer.Append("HTTP/");
            writer.Append(version);
            writer.Append(" ");
            writer.Append(status);
            writer.Append(" ");
            writer.Append(StatusDescription);
            writer.Append("\r\n");
            foreach (var key in keys)
            {
                if (string.Equals(key, HttpHeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase)) continue;
                writer.Append(key);
                writer.Append(": ");
                writer.Append(Headers[key]);
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

        private void EnsureCanChangeHeaders()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(_id);
            }

            if (HeadersSent)
            {
                throw new InvalidOperationException("Header values cannot be changed after headers are sent.");
            }
        }
    }
}
