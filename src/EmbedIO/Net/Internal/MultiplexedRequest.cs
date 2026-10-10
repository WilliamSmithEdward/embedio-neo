using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedRequest : IHttpRequest
    {
        private static readonly object NoReferrer = new();
        private readonly IMultiplexedExchange _exchange;
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
        private object? _referrer;
        internal MultiplexedRequest(IMultiplexedExchange exchange, IPEndPoint local, IPEndPoint remote, bool secure)
        {
            _exchange = exchange;
            LocalEndPoint = local; RemoteEndPoint = remote; IsSecureConnection = secure;
            var request = exchange.Request;
            HttpMethod = request.Method;
            HttpVerb = HttpListenerRequest.IsKnownHttpMethod(HttpMethod, out var verb) ? verb : HttpVerbs.Any;
            RawTarget = request.Path.Length == 0 ? request.Authority : request.Path;
            var scheme = request.Scheme.Length == 0 ? (secure ? "https" : "http") : request.Scheme;
            Url = new Uri(scheme + "://" + request.Authority + (request.Path.Length == 0 || request.Path == "*" ? "/" : request.Path));
            HasEntityBody = !exchange.InitialBodyComplete || request.ContentLength.GetValueOrDefault() > 0;
            // The referrer is the header value at construction. Parsing waits for the
            // first read, so a request whose referrer is never used builds no Uri.
            _referrer = Headers[HttpHeaderNames.Referer];
        }
        public NameValueCollection Headers => _exchange.Request.Headers;
        public bool KeepAlive => true;
        public string RawTarget { get; }

        // Parsed on first read from the immutable Url. Concurrent first readers may
        // each parse, but only one collection is published and every caller gets it.
        public NameValueCollection QueryString
        {
            get
            {
                var query = Volatile.Read(ref _queryString);
                if (query != null) return query;
                var created = ParseQuery(Url.Query);
                return Interlocked.CompareExchange(ref _queryString, created, null) ?? created;
            }
        }
        public string HttpMethod { get; }
        public HttpVerbs HttpVerb { get; }
        public Uri Url { get; }
        public bool HasEntityBody { get; }
        public Stream InputStream => _exchange.InputStream;
        public Encoding ContentEncoding
        {
            get
            {
                if (!HasEntityBody || ContentType == null) return WebServer.DefaultEncoding;
                var charset = HeaderUtility.GetCharset(ContentType);
                if (string.IsNullOrEmpty(charset)) return WebServer.DefaultEncoding;
                try { return Encoding.GetEncoding(charset); } catch (ArgumentException) { return WebServer.DefaultEncoding; }
            }
        }
        public IPEndPoint RemoteEndPoint { get; }
        public bool IsLocal => LocalEndPoint.Address.Equals(RemoteEndPoint.Address);
        public bool IsSecureConnection { get; }
        public string UserAgent => Headers[HttpHeaderNames.UserAgent] ?? string.Empty;
        public bool IsWebSocketRequest => _exchange.Request.Protocol == "websocket";
        public IPEndPoint LocalEndPoint { get; }
        public string? ContentType => Headers[HttpHeaderNames.ContentType];
        public long ContentLength64 => _exchange.Request.ContentLength ?? (_exchange.InitialBodyComplete ? 0 : -1);
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer
        {
            get
            {
                // _referrer holds the raw header until the first read, then the parsed
                // Uri, or NoReferrer when the header was absent or not an absolute URI.
                var current = Volatile.Read(ref _referrer);
                if (current is string text)
                {
                    object parsed = Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : NoReferrer;
                    current = Interlocked.CompareExchange(ref _referrer, parsed, text);
                    if (ReferenceEquals(current, text)) current = parsed;
                }
                return current as Uri;
            }
        }
        public ICookieCollection Cookies => _cookies ??= HttpListenerRequest.ParseCookies(Headers[HttpHeaderNames.Cookie] ?? string.Empty);
        public Version ProtocolVersion => _exchange.ProtocolVersion;

        // Same pairs as splitting the query on '&' and each part on its first '=':
        // empty parts add a null key with an empty value, and every name and value is
        // URL-decoded. Only the substrings themselves are allocated.
        private static NameValueCollection ParseQuery(string query)
        {
            var result = new NameValueCollection();
            if (query.Length == 0) return result;
            for (var start = 1; ;)
            {
                var end = query.IndexOf('&', start);
                if (end < 0) end = query.Length;
                var equals = query.IndexOf('=', start, end - start);
                if (equals < 0) result.Add(null, WebUtility.UrlDecode(query.Substring(start, end - start)));
                else result.Add(WebUtility.UrlDecode(query.Substring(start, equals - start)), WebUtility.UrlDecode(query.Substring(equals + 1, end - equals - 1)));
                if (end == query.Length) return result;
                start = end + 1;
            }
        }
    }
}
