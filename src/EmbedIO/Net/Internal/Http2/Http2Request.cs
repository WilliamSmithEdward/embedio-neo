using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2Request : IHttpRequest
    {
        internal static readonly Version Http2Version = new(2, 0);
        private readonly Http2Exchange _exchange;
        private CookieList? _cookies;
        internal Http2Request(Http2Exchange exchange, IPEndPoint local, IPEndPoint remote, bool secure)
        {
            _exchange = exchange;
            LocalEndPoint = local; RemoteEndPoint = remote; IsSecureConnection = secure;
            var request = exchange.Request;
            HttpMethod = request.Method;
            HttpVerb = HttpListenerRequest.IsKnownHttpMethod(HttpMethod, out var verb) ? verb : HttpVerbs.Any;
            RawTarget = request.Path.Length == 0 ? request.Authority : request.Path;
            var scheme = request.Scheme.Length == 0 ? (secure ? "https" : "http") : request.Scheme;
            Url = new Uri(scheme + "://" + request.Authority + (request.Path.Length == 0 || request.Path == "*" ? "/" : request.Path));
            HasEntityBody = !exchange.State.InitialEndStream || request.ContentLength.GetValueOrDefault() > 0;
            var query = Url.Query;
            if (query.Length > 0)
            {
                foreach (var part in query.Substring(1).Split('&'))
                {
                    var equals = EmbedIO.Internal.StringOperations.IndexOfOrdinal(part, '=');
                    if (equals < 0) QueryString.Add(null, WebUtility.UrlDecode(part));
                    else QueryString.Add(WebUtility.UrlDecode(part.Substring(0, equals)), WebUtility.UrlDecode(part.Substring(equals + 1)));
                }
            }
            if (Uri.TryCreate(Headers[HttpHeaderNames.Referer], UriKind.Absolute, out var referer)) UrlReferrer = referer;
        }
        public NameValueCollection Headers => _exchange.Request.Headers;
        public bool KeepAlive => true;
        public string RawTarget { get; }
        public NameValueCollection QueryString { get; } = new();
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
        public long ContentLength64 => _exchange.Request.ContentLength ?? (_exchange.State.InitialEndStream ? 0 : -1);
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer { get; }
        public ICookieCollection Cookies => _cookies ??= HttpListenerRequest.ParseCookies(Headers[HttpHeaderNames.Cookie] ?? string.Empty);
        public Version ProtocolVersion => Http2Version;
    }
}
