using System;
using System.Buffers;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    /// <summary>
    /// Represents an HTTP Listener Request.
    /// </summary>
    internal sealed partial class HttpListenerRequest : IHttpRequest
    {


        private readonly HttpConnection _connection;
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
        private Stream? _inputStream;
        internal bool HasBodyFramingFailure => _inputStream is RequestStream body && body.HasBodyFramingFailure;
        internal bool IsBodyFramingError(Exception error) => _inputStream is RequestStream body && body.IsFramingError(error);
        private bool _kaSet;
        private bool _keepAlive;
        private bool _chunked;
        private bool _framingInitialized;
        private long _contentLength;

        internal HttpListenerRequest(HttpListenerContext context)
        {
            _connection = context.Connection;
        }

        /// <summary>
        /// Gets the MIME accept types.
        /// </summary>
        /// <value>
        /// The accept types.
        /// </value>
        public string[] AcceptTypes { get; private set; } = Array.Empty<string>();

        /// <inheritdoc />
        public Encoding ContentEncoding
        {
            get
            {
                if (!HasEntityBody || ContentType == null)
                {
                    return WebServer.DefaultEncoding;
                }

                var charSet = HeaderUtility.GetCharset(ContentType);
                if (string.IsNullOrEmpty(charSet))
                {
                    return WebServer.DefaultEncoding;
                }

                try
                {
                    return Encoding.GetEncoding(charSet);
                }
                catch (ArgumentException)
                {
                    return WebServer.DefaultEncoding;
                }
            }
        }

        /// <inheritdoc />
        public long ContentLength64 => _framingInitialized ? _contentLength : long.TryParse(Headers[HttpHeaderNames.ContentLength], out var val) ? val : 0;

        /// <inheritdoc />
        public string? ContentType => Headers[HttpHeaderNames.ContentType];

        /// <inheritdoc />
        public ICookieCollection Cookies => _cookies ??= new CookieList();

        /// <inheritdoc />
        public bool HasEntityBody => _chunked || ContentLength64 > 0;

        /// <inheritdoc />
        public NameValueCollection Headers { get; } = new();

        /// <inheritdoc />
        public string HttpMethod { get; private set; } = string.Empty;

        /// <inheritdoc />
        public HttpVerbs HttpVerb { get; private set; }

        /// <inheritdoc />
        public Stream InputStream => _inputStream ??= HasEntityBody ? _connection.GetRequestStream(ContentLength64, _chunked) : Stream.Null;

        /// <inheritdoc />
        public bool IsAuthenticated => false;

        /// <inheritdoc />
        public bool IsLocal => LocalEndPoint.Address?.Equals(RemoteEndPoint.Address) ?? true;

        /// <inheritdoc />
        public bool IsSecureConnection => _connection.IsSecure;

        /// <inheritdoc />
        public bool KeepAlive
        {
            get
            {
                if (!_kaSet)
                {
                    _keepAlive = !Headers.Contains(HttpHeaderNames.Connection, "close", StringComparison.OrdinalIgnoreCase)
                        && (ProtocolVersion >= HttpVersion.Version11 || Headers.Contains(HttpHeaderNames.Connection, "keep-alive", StringComparison.OrdinalIgnoreCase));

                    _kaSet = true;
                }

                return _keepAlive;
            }
        }

        /// <inheritdoc />
        public IPEndPoint LocalEndPoint => _connection.LocalEndPoint;

        /// <inheritdoc />
        public Version ProtocolVersion { get; private set; } = HttpVersion.Version11;

        /// <inheritdoc />
        public NameValueCollection QueryString
        {
            get
            {
                var query = Volatile.Read(ref _queryString);
                if (query != null) return query;
                var created = new NameValueCollection();
                return Interlocked.CompareExchange(ref _queryString, created, null) ?? created;
            }
        }

        /// <inheritdoc />
        public string RawTarget { get; private set; } = string.Empty;

        /// <inheritdoc />
        public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

        /// <inheritdoc />
        public Uri Url { get; private set; } = WebServer.NullUri;

        /// <inheritdoc />
        public Uri? UrlReferrer { get; private set; }

        /// <inheritdoc />
        public string? UserAgent => Headers[HttpHeaderNames.UserAgent];

        public string UserHostAddress => LocalEndPoint.ToString();

        public string? UserHostName => Headers[HttpHeaderNames.Host];

        public string[] UserLanguages { get; private set; } = Array.Empty<string>();

        /// <inheritdoc />
        public bool IsWebSocketRequest
            => HttpVerb == HttpVerbs.Get
            && ProtocolVersion >= HttpVersion.Version11
            && Headers.Contains(HttpHeaderNames.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            && Headers.Contains(HttpHeaderNames.Connection, "Upgrade", StringComparison.OrdinalIgnoreCase);

        internal bool RequiresContinue => ProtocolVersion >= HttpVersion.Version11 && HasEntityBody
            && HttpExpectations.ContainsContinue(Headers["Expect"]);

        internal void SetRequestLine(string req)
        {
            var first = EmbedIO.Internal.StringOperations.IndexOfOrdinal(req, ' ');
            var second = first < 0 ? -1 : req.IndexOf(' ', first + 1);
            if (first <= 0 || second <= first + 1 || second + 9 != req.Length)
            {
                _connection.SetError("Invalid request line.");
                return;
            }
            for (var i = 0; i < first; i++)
                if (!HttpRequestFraming.IsTokenCharacter(req[i]))
                {
                    _connection.SetError("Invalid method.");
                    return;
                }
            for (var i = first + 1; i < second; i++)
                if (req[i] <= 32 || req[i] == 127 || req[i] == '#' || req[i] == '\\'
                    || (req[i] == '%' && (i + 2 >= second || !Uri.IsHexDigit(req[i + 1]) || !Uri.IsHexDigit(req[i + 2]))))
                {
                    _connection.SetError("Invalid request target.");
                    return;
                }
            HttpMethod = req.Substring(0, first);
            HttpVerb = IsKnownHttpMethod(HttpMethod, out var verb) ? verb : HttpVerbs.Any;
            RawTarget = req.Substring(first + 1, second - first - 1);
            if (string.CompareOrdinal(req, second + 1, "HTTP/1.1", 0, 8) == 0)
                ProtocolVersion = HttpVersion.Version11;
            else if (string.CompareOrdinal(req, second + 1, "HTTP/1.0", 0, 8) == 0)
                ProtocolVersion = HttpVersion.Version10;
            else if (string.CompareOrdinal(req, second + 1, "HTTP/1.", 0, 7) == 0
                && req[second + 8] is >= '2' and <= '9')
                // RFC 9110: process higher minor versions using supported semantics,
                // while preserving the version actually received for applications.
                ProtocolVersion = new Version(1, req[second + 8] - '0');
            else _connection.SetError("Unsupported HTTP version.");
        }

        internal void FinishInitialization()
        {
            var transfer = Headers[HttpHeaderNames.TransferEncoding];
            var length = Headers[HttpHeaderNames.ContentLength];
            if (transfer != null)
            {
                if (length != null || ProtocolVersion < HttpVersion.Version11
                    || !string.Equals(transfer, "chunked", StringComparison.OrdinalIgnoreCase))
                {
                    _connection.SetError("Unsupported or ambiguous request framing.");
                    return;
                }
                _chunked = true;
                _contentLength = -1;
            }
            else if (length != null && !HttpRequestFraming.TryContentLength(length, out _contentLength))
            {
                _connection.SetError("Invalid Content-Length.");
                return;
            }
            _framingInitialized = true;
            var host = UserHostName;
            if ((ProtocolVersion > HttpVersion.Version10 && string.IsNullOrEmpty(host))
                || (host != null && host.Length != 0 && !HttpRequestFraming.IsValidHost(host)))
            {
                _connection.SetError("Invalid host name");
                return;
            }

            var targetPathStart = 0;
            if (RawTarget[0] != '/')
            {
                var schemeEnd = RawTarget.IndexOf("://", StringComparison.Ordinal);
                if (schemeEnd >= 0)
                {
                    targetPathStart = schemeEnd + 3;
                    while (targetPathStart < RawTarget.Length && RawTarget[targetPathStart] != '/' && RawTarget[targetPathStart] != '?')
                    {
                        if (RawTarget[targetPathStart] == '@')
                        {
                            _connection.SetError("Userinfo is not allowed in a request target.");
                            return;
                        }
                        targetPathStart++;
                    }
                }
            }
            if (!HttpRequestFraming.IsValidPathAndQuery(RawTarget, targetPathStart))
            {
                _connection.SetError("Invalid request target syntax.");
                return;
            }

            var rawUri = UriUtility.StringToAbsoluteUri(RawTarget);
            if (RawTarget[0] != '/' && RawTarget != "*"
                && (rawUri == null || (rawUri.Scheme != Uri.UriSchemeHttp && rawUri.Scheme != Uri.UriSchemeHttps)
                    || RawTarget.IndexOf("://", StringComparison.Ordinal) < 0))
            {
                _connection.SetError("Invalid request target form.");
                return;
            }
            if (RawTarget == "*" && HttpVerb != HttpVerbs.Options)
            {
                _connection.SetError("Asterisk-form requires OPTIONS.");
                return;
            }
            var path = RawTarget == "*" ? "/" : rawUri?.PathAndQuery ?? RawTarget;
            if (rawUri != null) host = rawUri.Host;

            if (string.IsNullOrEmpty(host))
            {
                host = rawUri?.Host ?? UserHostAddress;
            }

            host ??= UserHostAddress;
            var colon = host.LastIndexOf(':');
            if (colon >= 0 && colon > host.LastIndexOf(']'))
            {
                host = host.Substring(0, colon);
            }

            var baseUri = $"{(IsSecureConnection ? "https" : "http")}://{host}:{LocalEndPoint.Port}";

            if (!Uri.TryCreate(baseUri + path, UriKind.Absolute, out var url))
            {
                _connection.SetError(WebUtility.HtmlEncode($"Invalid url: {baseUri}{path}"));
                return;
            }

            Url = url;
            InitializeQueryString(Url.Query);


        }

        internal void AddHeader(string header)
        {
            var colon = header.IndexOf(":", System.StringComparison.Ordinal);
            if (colon == -1 || colon == 0)
            {
                _connection.SetError("Bad Request");
                return;
            }

            for (var i = 0; i < colon; i++)
                if (!HttpRequestFraming.IsTokenCharacter(header[i]))
                {
                    _connection.SetError("Invalid header name.");
                    return;
                }
            for (var i = colon + 1; i < header.Length; i++)
                if ((header[i] < 32 && header[i] != '\t') || header[i] == 127)
                {
                    _connection.SetError("Invalid header value.");
                    return;
                }
            var name = header.Substring(0, colon);
            var val = header.Substring(colon + 1).Trim(' ', '\t');
            var previous = Headers[name];
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!HttpRequestFraming.TryContentLength(val, out var parsed)
                    || (previous != null && (!HttpRequestFraming.TryContentLength(previous, out var existing) || existing != parsed)))
                {
                    _connection.SetError("Invalid or conflicting Content-Length.");
                    return;
                }
            }
            else if (previous != null && (name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                _connection.SetError("Duplicate framing header.");
                return;
            }
            else if (previous != null && (name.Equals(HttpHeaderNames.SecWebSocketKey, StringComparison.OrdinalIgnoreCase)
                || name.Equals(HttpHeaderNames.SecWebSocketVersion, StringComparison.OrdinalIgnoreCase)))
            {
                // Preserve repeated singleton fields so handshake validation rejects
                // them instead of silently accepting the last supplied value.
                Headers.Add(name, val);
                return;
            }
            else if (previous != null && (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || name.Equals(HttpHeaderNames.Upgrade, StringComparison.OrdinalIgnoreCase)
                || name.Equals(HttpHeaderNames.SecWebSocketProtocol, StringComparison.OrdinalIgnoreCase)))
                val = previous + ", " + val;
            Headers.Set(name, val);

            switch (name.ToUpperInvariant())
            {
                case "ACCEPT-LANGUAGE":
                    UserLanguages = val.SplitByComma(); // yes, only split with a ','
                    break;
                case "ACCEPT":
                    AcceptTypes = val.SplitByComma(); // yes, only split with a ','
                    break;
                case "CONTENT-LENGTH":
                    Headers[HttpHeaderNames.ContentLength] = val.Trim();

                    if (ContentLength64 < 0)
                    {
                        _connection.SetError("Invalid Content-Length.");
                    }

                    break;
                case "REFERER":
                    try
                    {
                        UrlReferrer = new Uri(val);
                    }
                    catch (UriFormatException)
                    {
                        UrlReferrer = null;
                    }

                    break;
                case "COOKIE":
                    _cookies = ParseCookies(val);

                    break;
            }
        }

        // returns true is the stream could be reused.
        internal bool FlushInput()
        {
            if (!HasEntityBody)
            {
                return true;
            }

            Stream input;
            try { input = InputStream; }
            catch (ObjectDisposedException)
            {
                _inputStream = null;
                return true;
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { return false; }
            if (input is RequestStream body && body.IsBodyConsumed)
                return true;

            var length = 2048;
            if (ContentLength64 > 0)
            {
                length = (int)Math.Min(ContentLength64, length);
            }

            var bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                while (true)
                {
                    try
                    {
                        if (input.Read(bytes, 0, length) <= 0)
                            return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        _inputStream = null;
                        return true;
                    }
                    catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                    {
                        return false;
                    }
                }
            }
            finally
            {
                // Request data must not survive in a buffer shared with another caller.
                ArrayPool<byte>.Shared.Return(bytes, clearArray: true);
            }
        }

        internal async Task<bool> FlushInputAsync()
        {
            if (!HasEntityBody) return true;
            var input = InputStream;
            if (input is RequestStream body && body.IsBodyConsumed) return true;
            var bytes = ArrayPool<byte>.Shared.Rent(2048);
            try
            {
                while (await input.ReadAsync(bytes, 0, 2048, CancellationToken.None).ConfigureAwait(false) > 0) { }
                return input is not RequestStream request || request.IsBodyConsumed;
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { return false; }
            finally { ArrayPool<byte>.Shared.Return(bytes, true); }
        }

        // Optimized for the following list of methods:
        // "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "QUERY"
        // ***NOTE***: The verb parameter is NOT VALID upon exit if false is returned.
        internal static bool IsKnownHttpMethod(string method, out HttpVerbs verb)
        {
            switch (method.Length)
            {
                case 3:
                    switch (method[0])
                    {
                        case 'G':
                            verb = HttpVerbs.Get;
                            return method[1] == 'E' && method[2] == 'T';

                        case 'P':
                            verb = HttpVerbs.Put;
                            return method[1] == 'U' && method[2] == 'T';

                        default:
                            verb = HttpVerbs.Any;
                            return false;
                    }

                case 4:
                    switch (method[0])
                    {
                        case 'H':
                            verb = HttpVerbs.Head;
                            return method[1] == 'E' && method[2] == 'A' && method[3] == 'D';

                        case 'P':
                            verb = HttpVerbs.Post;
                            return method[1] == 'O' && method[2] == 'S' && method[3] == 'T';

                        default:
                            verb = HttpVerbs.Any;
                            return false;
                    }

                case 5:
                    if (method[0] == 'Q')
                    {
                        verb = HttpVerbs.Query;
                        return method[1] == 'U' && method[2] == 'E' && method[3] == 'R' && method[4] == 'Y';
                    }
                    verb = HttpVerbs.Patch;
                    return method[0] == 'P'
                        && method[1] == 'A'
                        && method[2] == 'T'
                        && method[3] == 'C'
                        && method[4] == 'H';

                case 6:
                    verb = HttpVerbs.Delete;
                    return method[0] == 'D'
                        && method[1] == 'E'
                        && method[2] == 'L'
                        && method[3] == 'E'
                        && method[4] == 'T'
                        && method[5] == 'E';

                case 7:
                    verb = HttpVerbs.Options;
                    return method[0] == 'O'
                        && method[1] == 'P'
                        && method[2] == 'T'
                        && method[3] == 'I'
                        && method[4] == 'O'
                        && method[5] == 'N'
                        && method[6] == 'S';

                default:
                    verb = HttpVerbs.Any;
                    return false;
            }
        }

        internal static CookieList ParseCookies(string val)
        {
            var cookies = new CookieList();

            var cookieStrings = val.SplitByAny(';')
                .Where(x => !string.IsNullOrEmpty(x));
            Cookie? current = null;
            var version = 0;

            foreach (var cookieString in cookieStrings)
            {
                var str = cookieString.Trim();
                if (str.StartsWith("$Version", StringComparison.Ordinal))
                {
                    version = int.Parse(str.Substring(str.IndexOf("=", System.StringComparison.Ordinal) + 1).Unquote(), CultureInfo.InvariantCulture);
                }
                else if (str.StartsWith("$Path", StringComparison.Ordinal) && current != null)
                {
                    current.Path = str.Substring(str.IndexOf("=", System.StringComparison.Ordinal) + 1).Trim();
                }
                else if (str.StartsWith("$Domain", StringComparison.Ordinal) && current != null)
                {
                    current.Domain = str.Substring(str.IndexOf("=", System.StringComparison.Ordinal) + 1).Trim();
                }
                else if (str.StartsWith("$Port", StringComparison.Ordinal) && current != null)
                {
                    current.Port = $"\"{str.Substring(str.IndexOf("=", System.StringComparison.Ordinal) + 1).Trim()}\"";
                }
                else
                {
                    if (current != null)
                    {
                        cookies.Add(current);
                    }

                    try
                    {
                        var ck = new Cookie();
                        var idx = str.IndexOf("=", System.StringComparison.Ordinal);
                        if (idx > 0)
                        {
                            ck.Name = str.Substring(0, idx).Trim();
                            ck.Value = str.Substring(idx + 1).Trim();
                        }
                        else
                        {
                            ck.Name = str.Trim();
                            ck.Value = string.Empty;
                        }

                        ck.Version = version;

                        current = ck;
                    }
                    catch (Exception error) when (error is CookieException or ArgumentException)
                    {
                        current = null;
                    }
                }
            }

            if (current != null)
            {
                cookies.Add(current);
            }

            return cookies;
        }

        private void InitializeQueryString(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return;
            }

            if (query[0] == '?')
            {
                query = query.Substring(1);
            }

            var components = query.Split('&');

            foreach (var kv in components)
            {
                var pos = kv.IndexOf("=", System.StringComparison.Ordinal);
                if (pos == -1)
                {
                    QueryString.Add(null, WebUtility.UrlDecode(kv));
                }
                else
                {
                    var key = WebUtility.UrlDecode(kv.Substring(0, pos));
                    var val = WebUtility.UrlDecode(kv.Substring(pos + 1));

                    QueryString.Add(key, val);
                }
            }
        }
    }
}
