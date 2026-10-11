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
        private readonly IMultiplexedExchange _exchange;
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
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
            // A present query is parsed here, before the request is queued. Deferring it
            // to the handler's first read raised HTTP/2 tail latency with many streams
            // per connection (docs/project/http-request-model-allocations.md).
            if (EmbedIO.Internal.StringOperations.IndexOfOrdinal(request.Path, '?') >= 0) _queryString = ParseQuery(Url.Query);
            if (Uri.TryCreate(Headers[HttpHeaderNames.Referer], UriKind.Absolute, out var referer)) UrlReferrer = referer;
        }
        public NameValueCollection Headers => _exchange.Request.Headers;
        public bool KeepAlive => true;
        public string RawTarget { get; }

        // Without a query the empty collection is created on first read. Concurrent
        // first readers may each create one; only one is published and all get it.
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
        public Uri? UrlReferrer { get; }
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
