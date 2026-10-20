using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedRequest : IHttpRequestTrailers, IRequestPolicyTarget, IClientCertificateCarrier
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
        private Stream? _rawApplicationInput;
        private int _bodyLimitRetired;

        internal MultiplexedRequest(
            IMultiplexedExchange exchange,
            IPEndPoint local,
            IPEndPoint remote,
            bool secure,
            System.Security.Cryptography.X509Certificates.X509Certificate2? clientCertificate
        )
        {
            _exchange = exchange;
            LocalEndPoint = local;
            RemoteEndPoint = remote;
            IsSecureConnection = secure;
            ClientCertificate = clientCertificate;
            var request = exchange.Request;
            HttpMethod = request.Method;
            HttpVerb = HttpListenerRequest.IsKnownHttpMethod(HttpMethod, out var verb) ? verb : HttpVerbs.Any;
            RawTarget = request.Path.Length == 0 ? request.Authority : request.Path;
            var scheme = request.Scheme.Length == 0 ? (secure ? "https" : "http") : request.Scheme;
            Url = new Uri(
                scheme
                    + "://"
                    + request.Authority
                    + (request.Path.Length == 0 || request.Path == "*" ? "/" : request.Path)
            );
            HasEntityBody = !exchange.InitialBodyComplete || request.ContentLength.GetValueOrDefault() > 0;

            // A present query is parsed here, before the request is queued. Deferring it
            // to the handler's first read raised HTTP/2 tail latency with many streams
            // per connection (docs/project/http-request-model-allocations.md).
            if (EmbedIO.Internal.StringOperations.IndexOfOrdinal(request.Path, '?') >= 0)
                _queryString = QueryStringFields.Parse(Url.Query);

            if (Uri.TryCreate(Headers[HttpHeaderNames.Referer], UriKind.Absolute, out var referer))
                UrlReferrer = referer;
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

                var created = QueryStringFields.Parse(Url.Query);
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
        private MinimumDataRate? _dataRate;
        private Action? _onDataRateFailure;
        private RequestBodyRateStream? _ratedInput;
        private object? _applicationInputSync;
        private long _legacyBodyLimit = -1;
        private Action? _onLegacyLimitExceeded;

        private Stream ApplicationInput
        {
            get
            {
                if (
                    !HasEntityBody
                    || (_sendContinue == null
                        && _readTimeout == null
                        && _dataRate == null
                        && _countBody == null
                        && _legacyBodyLimit < 0)
                ) return _exchange.InputStream;

                var current = Volatile.Read(ref _applicationInput);

                if (current != null)
                {
                    if (Volatile.Read(ref _bodyLimitRetired) != 0) RetireBodyViews();

                    return current;
                }

                // Resolve the physical framing owner before taking the publication
                // gate, so request publication cannot invert the connection lock.
                var framing = _exchange.InputStream;

                lock (LazyInitializer.EnsureInitialized(ref _applicationInputSync)
                    ?? throw new InvalidOperationException("Missing application input publication."))
                {
                    if (_applicationInput == null)
                    {
                        var input = framing;
                        // The rate view sits on the transport body, so it times transport waits only.
                        // An HTTP/2 body the connection window holds back is not the client's doing.
                        var rated = _dataRate is MinimumDataRate rate
                            ? new RequestBodyRateStream(
                                input,
                                rate,
                                _onDataRateFailure,
                                _exchange is IMultiplexedInputBackPressure pressure
                                    ? pressure.IsInputBackPressured
                                    : null
                            )
                            : null;

                        if (rated != null) input = rated;

                        if (_countBody != null && _bodyPolicy == null)
                            input = new CountingReadStream(input, _countBody);

                        var timed = _readTimeout is TimeSpan timeout
                            ? new ReadTimeoutStream(input, timeout, _onReadTimeout)
                            : null;

                        if (timed != null) input = timed;

                        if (_sendContinue != null) input = new DeferredContinueStream(input, StartContinue);

                        if (_legacyBodyLimit >= 0)
                            input = new LimitedRequestStream(
                                input,
                                _legacyBodyLimit,
                                LimitedRequestStream.TransferredLimitMessage,
                                _onLegacyLimitExceeded
                            );

                        Volatile.Write(ref _ratedInput, rated);
                        Volatile.Write(ref _timedInput, timed);
                        Volatile.Write(ref _applicationInput, input);
                    }

                    current = _applicationInput
                        ?? throw new InvalidOperationException("Missing published application input.");
                }

                // Retirement never takes the publication gate or disposes a
                // borrowed source. This recheck covers publication after retire.
                if (Volatile.Read(ref _bodyLimitRetired) != 0) RetireBodyViews();

                return current;
            }
        }

        private void RetireBodyViews()
        {
            Volatile
                .Read(ref _timedInput)
                ?.Retire();
            Volatile
                .Read(ref _ratedInput)
                ?.Retire();
        }

        private RequestTelemetry? _countBody;

        void IRequestPolicyTarget.LimitBody(long maximumBytes, Action? onExceeded)
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

        void IRequestPolicyTarget.TimeBodyReads(TimeSpan timeout, Action? onTimeout)
        {
            if (_applicationInput != null || Volatile.Read(ref _limitedInput) != null)
                throw new InvalidOperationException("The request body was already opened.");

            _readTimeout = timeout;
            _onReadTimeout = onTimeout;
        }

        void IRequestPolicyTarget.EnforceBodyDataRate(MinimumDataRate rate, Action? onFailure)
        {
            if (_applicationInput != null || Volatile.Read(ref _limitedInput) != null)
                throw new InvalidOperationException("The request body was already opened.");

            _dataRate = rate;
            _onDataRateFailure = onFailure;
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

                var application = Volatile.Read(ref _rawApplicationInput);

                if (application != null) return application;

                var view = Volatile.Read(ref _limitedInput);

                if (view == null)
                {
                    var created = new RawRequestBodyStream(
                        ApplicationInput,
                        _bodyPolicy.MaximumBytes,
                        KnownBodyRefusal,
                        _exchange.InputStream as IRequestTrailerSource
                    );
                    view = Interlocked.CompareExchange(ref _limitedInput, created, null) ?? created;

                    if (!ReferenceEquals(created, view)) created.Retire();
                }

                if (Volatile.Read(ref _bodyLimitRetired) != 0) view.Retire();

                // Only public reads are application bytes. Cleanup and the
                // private EOF/excess probe use the raw view without this counter.
                application = _countBody != null ? new CountingReadStream(view, _countBody) : view;
                return Interlocked.CompareExchange(ref _rawApplicationInput, application, null) ?? application;
            }
        }

        internal bool IsBodyReadTimeoutFailure(Exception error) =>
            Volatile
                .Read(ref _timedInput)
                ?.IsTimeoutFailure(error)
                == true
                || Volatile.Read(ref _ratedInput)?.IsRateFailure(error)
                == true;

        internal RequestPolicySnapshot? CapturedRequestPolicy { get; private set; }

        internal void CaptureRequestPolicy(RequestPolicySnapshot policy) => CapturedRequestPolicy = policy;

        internal void SetBodyPolicy(RequestBodyPolicy policy) => _bodyPolicy = policy;

        internal RawRequestBodyLimitException? KnownBodyRefusal => Volatile.Read(ref _knownBodyRefusal);

        internal void MarkKnownBodyRefusal(RawRequestBodyLimitException failure) =>
            Interlocked.CompareExchange(ref _knownBodyRefusal, failure, null);

        // Validates the rest of a body the handler opened, so an excess byte it never read
        // still fails the request. A body the handler never opened is left to the transport,
        // which ends it after the response as it does without a limit: HTTP/2 resets the
        // stream and HTTP/3 stops reading it. Draining it first would build the counted view
        // only to have the transport abort the read, and HTTP/3 would pay for that on every
        // GET sent without content-length, which cannot be known bodyless at dispatch.
        internal async System.Threading.Tasks.Task CompleteBodyInputAsync(CancellationToken token)
        {
            if (_bodyPolicy == null || KnownBodyRefusal != null || IsContinueOwed) return;

            var view = Volatile.Read(ref _limitedInput);

            if (view == null || view.IsValidatedEnd) return;

            // A body the transport stopped reading, as HTTP/3 does after a read the handler
            // canceled, or one with a read still outstanding, is left to the transport like an
            // unopened one: draining it could only fail. An expired read deadline or a refused
            // data rate still drains, which reports its 408 without touching the transport.
            var timed = Volatile.Read(ref _timedInput);

            if (
                timed?.HasExpired != true && Volatile.Read(ref _ratedInput)?.HasFailed != true
                && (view.ReadInProgress || timed?.HasUnsettledRead == true || !_exchange.InputStream.CanRead)
            )
                return;

            await view
                .CompleteInputAsync(token)
                .ConfigureAwait(false);
        }

        internal void RetireBodyLimit()
        {
            if (_bodyPolicy == null && _readTimeout == null && _dataRate == null) return;

            Interlocked.Exchange(ref _bodyLimitRetired, 1);
            Volatile
                .Read(ref _limitedInput)
                ?.Retire();
            RetireBodyViews();
        }

        public bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers)
        {
            trailers = RequestTrailerSnapshot.Empty;
            var available = _exchange.InputStream is IRequestTrailerSource source
                && source.TryGetTrailers(out trailers);

            // Check the monotonic raw outcome after the physical snapshot, so
            // overlapping disposal cannot publish metadata from a later EOF.
            if (
                Volatile.Read(ref _limitedInput) is { } raw
                && (raw.ReadInProgress || raw.HasLimitFailure || raw.TrailerInputAbandoned)
            )
            {
                trailers = RequestTrailerSnapshot.Empty;
                return false;
            }

            return available;
        }

        public Encoding ContentEncoding =>
            HasEntityBody ? HeaderUtility.GetEncoding(ContentType) : WebServer.DefaultEncoding;
        public IPEndPoint RemoteEndPoint { get; }
        public bool IsLocal => LocalEndPoint.Address.Equals(RemoteEndPoint.Address);
        public bool IsSecureConnection { get; }
        public System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate { get; }
        public string UserAgent => Headers[HttpHeaderNames.UserAgent] ?? string.Empty;
        public bool IsWebSocketRequest => _exchange.Request.Protocol == "websocket";
        public IPEndPoint LocalEndPoint { get; }
        public string? ContentType => Headers[HttpHeaderNames.ContentType];
        public long ContentLength64 => _exchange.Request.ContentLength ?? (_exchange.InitialBodyComplete ? 0 : -1);
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer { get; }
        public ICookieCollection Cookies =>
            _cookies ??= HttpListenerRequest.ParseCookies(Headers[HttpHeaderNames.Cookie] ?? string.Empty);
        public Version ProtocolVersion => _exchange.ProtocolVersion;
    }
}
