using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;

namespace EmbedIO.Net.Internal
{
    /// <summary>
    /// Represents a wrapper for HttpListenerContext.Response.
    /// </summary>
    /// <seealso cref="IHttpResponse" />
    public class SystemHttpResponse : IHttpResponse
    {
        private readonly System.Net.HttpListenerResponse _response;
        private Stream? _outputStream;
        private bool _headersPrepared;
        private bool _webSocketAccepted;
        private readonly bool _isHeadResponse;

        /// <summary>
        /// Initializes a new instance of the <see cref="SystemHttpResponse"/> class.
        /// </summary>
        /// <param name="context">The context.</param>
        public SystemHttpResponse(System.Net.HttpListenerContext context)
        {
            _response = context.Response;
            _isHeadResponse = string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);
            Cookies = new SystemCookieCollection(_response.Cookies);
        }

        /// <inheritdoc />
        public WebHeaderCollection Headers => _response.Headers;

        /// <inheritdoc />
        public int StatusCode
        {
            get => _response.StatusCode;
            set => _response.StatusCode = value;
        }

        /// <inheritdoc />
        public long ContentLength64
        {
            get => _isHeadResponse && long.TryParse(Headers[HttpHeaderNames.ContentLength], NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0
                ? length : _response.ContentLength64;
            set
            {
                _response.ContentLength64 = value;
                if (_isHeadResponse)
                    Headers[HttpHeaderNames.ContentLength] = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <inheritdoc />
        public string ContentType
        {
            get => _response.ContentType;
            set => _response.ContentType = value;
        }

        /// <inheritdoc />
        // Native Unix responses can become disposed when a write fails or their stream closes.
        // Reuse the acquired stream so final cleanup does not reacquire it from a disposed response.
        public Stream OutputStream => _outputStream ??= new SystemResponseStream(_response.OutputStream, PrepareHeaders, _isHeadResponse);

        // A successful upgrade transfers the transport to the WebSocket.
        // Final HTTP cleanup must not reacquire the disposed native response stream.
        internal void MarkWebSocketAccepted()
        {
            _webSocketAccepted = true;
            _outputStream = Stream.Null;
        }

        /// <inheritdoc />
        public ICookieCollection Cookies { get; }

        /// <inheritdoc />
        public Encoding? ContentEncoding
        {
            get => _response.ContentEncoding;
            set => _response.ContentEncoding = value;
        }

        /// <inheritdoc />
        public bool KeepAlive
        {
            get => _response.KeepAlive;
            set => _response.KeepAlive = value;
        }

        /// <inheritdoc />
        public bool SendChunked
        {
            get => _response.SendChunked;
            set => _response.SendChunked = value;
        }

        /// <inheritdoc />
        public Version ProtocolVersion
        {
            get => _response.ProtocolVersion;
            set => _response.ProtocolVersion = value;
        }

        /// <inheritdoc />
        public string StatusDescription
        {
            get => _response.StatusDescription;
            set => _response.StatusDescription = value;
        }

        /// <inheritdoc />
        public void SetCookie(Cookie cookie) => _response.SetCookie(cookie);

        /// <inheritdoc />
        public void Close()
        {
            if (!_webSocketAccepted)
                PrepareHeaders();
            _response.Close();
        }

        private void GetExplicitScope(Cookie cookie, out bool explicitPath, out bool explicitDomain)
        {
            // Native SetCookie clones private implicit-scope flags through a public API.
            // Inspect the clone's client format at version 1 without changing the caller's cookie.
            var original = _response.Cookies;
            Cookie copy;
            try
            {
                _response.Cookies = new CookieCollection();
                _response.SetCookie(cookie);
                copy = _response.Cookies[cookie.Name]!;
            }
            finally
            {
                _response.Cookies = original;
            }
            copy.Version = Math.Max(1, copy.Version);
            var text = copy.ToString();
            var attributes = text.Substring(text.IndexOf("; ", StringComparison.Ordinal) + 2 + copy.Name.Length + 1 + copy.Value.Length);
            explicitPath = attributes.StartsWith("; $Path=", StringComparison.Ordinal);
            if (explicitPath)
                attributes = attributes.Substring(8 + copy.Path.Length);
            explicitDomain = attributes.StartsWith("; $Domain=", StringComparison.Ordinal);
        }

        internal void PrepareHeaders()
        {
            if (_headersPrepared)
                return;

            // Direct header assignments do not update the native framing field.
            // HEAD has no body, but its advertised length needs one consistent value.
            if (_isHeadResponse && long.TryParse(Headers[HttpHeaderNames.ContentLength], NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length >= 0)
                _response.ContentLength64 = length;

            // Leave ordinary native serialization intact when no protected cookie needs correction.
            if (!Cookies.Any(cookie => cookie.HttpOnly || cookie.Secure))
            {
                _headersPrepared = true;
                return;
            }

            // Keep native collection replacement/SetCookie validation until headers commit.
            // Detach it afterwards so the runtime cannot overwrite the complete headers
            // with its serializer, which omits HttpOnly and Secure.
            foreach (var cookie in Cookies)
            {
                if (cookie.Name.Length == 0)
                    continue;
                GetExplicitScope(cookie, out var explicitPath, out var explicitDomain);
                var value = new StringBuilder().Append(cookie.Name).Append('=').Append(cookie.Value);
                if (cookie.Comment.Length > 0)
                    value.Append("; Comment=").Append(cookie.Comment);
                if (cookie.CommentUri != null)
                    value.Append("; CommentURL=\"").Append(cookie.CommentUri).Append('"');
                if (cookie.Discard)
                    value.Append("; Discard");
                if (explicitDomain && cookie.Domain.Length > 0)
                    value.Append("; Domain=").Append(cookie.Domain);
                if (cookie.Expires != DateTime.MinValue)
                {
                    var seconds = Math.Max(0, (int)(cookie.Expires.ToUniversalTime() - DateTime.UtcNow).TotalSeconds);
                    value.Append("; Max-Age=").Append(seconds.ToString(CultureInfo.InvariantCulture));
                }
                if (explicitPath && cookie.Path.Length > 0)
                    value.Append("; Path=").Append(cookie.Path);
                if (cookie.Port.Length > 0)
                    value.Append("; Port=").Append(cookie.Port);
                if (cookie.Version > 0)
                    value.Append("; Version=").Append(cookie.Version.ToString(CultureInfo.InvariantCulture));
                if (cookie.Secure)
                    value.Append("; Secure");
                if (cookie.HttpOnly)
                    value.Append("; HttpOnly");
                var header = cookie.Port.Length > 0 || cookie.ToString().EndsWith("; $Port", StringComparison.Ordinal)
                    ? "Set-Cookie2" : "Set-Cookie";
                _response.Headers.Add(header, value.ToString());
            }
            _response.Cookies = new CookieCollection();
            _headersPrepared = true;
        }
    }
}
