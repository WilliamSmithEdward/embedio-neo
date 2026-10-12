using System;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Utilities;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedRequest : IHttpRequestTrailers, IRequestPolicyTarget
    {
        private readonly IMultiplexedExchange _exchange;
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
        private Func<CancellationToken, System.Threading.Tasks.Task>? _sendContinue;
        private Stream? _applicationInput;
        private int _continueStarted;
        private RequestBodyPolicy? _bodyPolicy;
        private RawRequestBodyLimitException? _knownBodyRefusal;
        private RawRequestBodyStream? _limitedInput;
        private int _bodyLimitRetired;
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
        private TimeSpan? _readTimeout;
        private Action? _onReadTimeout;
        private ReadTimeoutStream? _timedInput;
        private object? _applicationInputSync;
        private long _legacyBodyLimit = -1;
        private Action? _onLegacyLimitExceeded;
        private Stream ApplicationInput
        {
            get
            {
                if (!HasEntityBody || (_sendContinue == null && _readTimeout == null && _countBody == null && _legacyBodyLimit < 0)) return _exchange.InputStream;
                var current = Volatile.Read(ref _applicationInput);
                if (current != null)
                {
                    if (Volatile.Read(ref _bodyLimitRetired) != 0) Volatile.Read(ref _timedInput)?.Retire();
                    return current;
                }
                // Resolve the physical framing owner before taking the publication
                // gate, so request publication cannot invert the connection lock.
                var framing = _exchange.InputStream;
                ReadTimeoutStream? timed;
                lock (LazyInitializer.EnsureInitialized(ref _applicationInputSync)
                    ?? throw new InvalidOperationException("Missing application input publication."))
                {
                    if (_applicationInput == null)
                    {
                        var input = framing;
                        if (_countBody != null) input = new CountingReadStream(input, _countBody);
                        timed = _readTimeout is TimeSpan timeout ? new ReadTimeoutStream(input, timeout, _onReadTimeout) : null;
                        if (timed != null) input = timed;
                        if (_sendContinue != null) input = new DeferredContinueStream(input, StartContinue);
                        if (_legacyBodyLimit >= 0) input = new LimitedRequestStream(input, _legacyBodyLimit,
                            LimitedRequestStream.TransferredLimitMessage, _onLegacyLimitExceeded);
                        Volatile.Write(ref _timedInput, timed);
                        Volatile.Write(ref _applicationInput, input);
                    }
                    timed = _timedInput;
                    current = _applicationInput ?? throw new InvalidOperationException("Missing published application input.");
                }
                // Retirement never takes the publication gate or disposes a
                // borrowed source. This recheck covers publication after retire.
                if (Volatile.Read(ref _bodyLimitRetired) != 0) timed?.Retire();
                return current;
            }
        }
        private RequestTelemetry? _countBody;
        void IRequestPolicyTarget.LimitBody(long maximumBytes, Action onExceeded)
        {
            if (_applicationInput != null || Volatile.Read(ref _limitedInput) != null)
                throw new InvalidOperationException("The request body was already opened.");
            _legacyBodyLimit = maximumBytes;
            _onLegacyLimitExceeded = onExceeded;
        }
        void IRequestPolicyTarget.CountBody(RequestTelemetry scope)
        {
            if (_applicationInput != null || Volatile.Read(ref _limitedInput) != null)
                throw new InvalidOperationException("The request body was already opened.");
            _countBody = scope;
        }
        void IRequestPolicyTarget.TimeBodyReads(TimeSpan timeout, Action onTimeout)
        {
            if (_applicationInput != null || Volatile.Read(ref _limitedInput) != null)
                throw new InvalidOperationException("The request body was already opened.");
            _readTimeout = timeout;
            _onReadTimeout = onTimeout;
        }
        internal void DeferContinue(Func<CancellationToken, System.Threading.Tasks.Task> send) => _sendContinue = send;
        private bool IsContinueOwed => _sendContinue != null && Volatile.Read(ref _continueStarted) == 0;
        private System.Threading.Tasks.Task StartContinue(CancellationToken token)
        {
            Volatile.Write(ref _continueStarted, 1);
            return (_sendContinue ?? throw new InvalidOperationException("No 100 Continue is owed."))(token);
        }
        int IRequestPolicyTarget.HeaderFieldCount => _exchange.Request.FieldCount;
        long IRequestPolicyTarget.HeaderFieldBytes => _exchange.Request.FieldBytes;
        public Stream InputStream
        {
            get
            {
                if (_bodyPolicy == null) return ApplicationInput;
                var view = Volatile.Read(ref _limitedInput);
                if (view != null) return view;
                var created = new RawRequestBodyStream(ApplicationInput, _bodyPolicy.MaximumBytes, KnownBodyRefusal);
                view = Interlocked.CompareExchange(ref _limitedInput, created, null) ?? created;
                if (!ReferenceEquals(created, view)) created.Retire();
                if (Volatile.Read(ref _bodyLimitRetired) != 0) view.Retire();
                return view;
            }
        }
        internal bool IsBodyReadTimeoutFailure(Exception error) => Volatile.Read(ref _timedInput)?.IsTimeoutFailure(error) == true;
        internal RequestPolicySnapshot? CapturedRequestPolicy { get; private set; }
        internal void CaptureRequestPolicy(RequestPolicySnapshot policy) => CapturedRequestPolicy = policy;
        internal void SetBodyPolicy(RequestBodyPolicy policy) => _bodyPolicy = policy;
        internal RawRequestBodyLimitException? KnownBodyRefusal => Volatile.Read(ref _knownBodyRefusal);
        internal void MarkKnownBodyRefusal(RawRequestBodyLimitException failure)
            => Interlocked.CompareExchange(ref _knownBodyRefusal, failure, null);
        internal async System.Threading.Tasks.Task CompleteBodyInputAsync(CancellationToken token)
        {
            if (_bodyPolicy == null || KnownBodyRefusal != null || IsContinueOwed) return;
            _ = InputStream;
            await (_limitedInput ?? throw new InvalidOperationException("Missing raw body view."))
                .CompleteInputAsync(token).ConfigureAwait(false);
        }
        internal void RetireBodyLimit()
        {
            if (_bodyPolicy == null && _readTimeout == null) return;
            Interlocked.Exchange(ref _bodyLimitRetired, 1);
            Volatile.Read(ref _limitedInput)?.Retire();
            Volatile.Read(ref _timedInput)?.Retire();
        }
        public bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers)
        {
            if (_exchange.InputStream is IRequestTrailerSource source) return source.TryGetTrailers(out trailers);
            trailers = RequestTrailerSnapshot.Empty;
            return false;
        }
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
