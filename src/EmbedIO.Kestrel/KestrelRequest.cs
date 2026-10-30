using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net;
using EmbedIO.Net.Internal;
using Microsoft.AspNetCore.Http.Features;
using OwnedRequest = EmbedIO.Net.Internal.HttpListenerRequest;

namespace EmbedIO.Kestrel
{
    internal sealed class KestrelRequest :
        IHttpRequestTrailers,
        IHttpBackendRequest,
        IClientCertificateCarrier,
        IDisposable
    {
        private readonly KestrelContext _context;
        private readonly Http1RequestHead _head = new();
        private readonly KestrelInput _input;
        private readonly string _method;
        private readonly string _target;
        private readonly bool _hasBody;
        private readonly bool _isWebSocketRequest;
        private Stream? _applicationInput;
        private RawRequestBodyStream? _limited;
        private ReadTimeoutStream? _timed;
        private RequestBodyRateStream? _rated;
        private RequestBodyPolicy? _policy;
        private TimeSpan? _timeout;
        private MinimumDataRate? _rate;
        private Action? _onTimeout;
        private Action? _onRate;
        private Action? _onLimit;
        private long? _legacyLimit;
        private RequestTelemetry? _count;
        private NameValueCollection? _query;
        private CookieList? _cookies;
        private IReadOnlyList<KeyValuePair<string, string>>? _trailers;
        private readonly object _sync = new();

        internal KestrelRequest(KestrelContext context, IFeatureCollection features)
        {
            _context = context;
            var request = features.Get<IHttpRequestFeature>()
                ?? throw new InvalidOperationException("Missing request feature.");
            var connection = features.Get<IHttpConnectionFeature>()
                ?? throw new InvalidOperationException("Missing connection feature.");
            LocalEndPoint = new IPEndPoint(connection.LocalIpAddress ?? IPAddress.None, connection.LocalPort);
            RemoteEndPoint = new IPEndPoint(connection.RemoteIpAddress ?? IPAddress.None, connection.RemotePort);
            IsSecureConnection = request.Scheme == "https";
            ProtocolVersion = request.Protocol switch
            {
                "HTTP/2" => HttpVersion.Version20,
                "HTTP/3" => HttpVersion.Version30,
                "HTTP/1.0" => HttpVersion.Version10,
                _ => HttpVersion.Version11
            };
            _method = request.Method;
            _target = request.RawTarget;
            _hasBody = features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
            ClientCertificate = features.Get<ITlsConnectionFeature>()?.ClientCertificate;
            var extended = features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;
            var connectionHeader = request.Headers.TryGetValue("Connection", out var values) ? values.ToString() : null;
            KeepAlive = ProtocolVersion >= HttpVersion.Version11
                ? !HasToken(connectionHeader, "close")
                : HasToken(connectionHeader, "keep-alive");
            _isWebSocketRequest = extended && features.Get<IHttpExtendedConnectFeature>()?.Protocol == "websocket"
                || request.Method == "GET" && features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest
                == true
                && HasToken(request.Headers["Upgrade"].ToString(), "websocket");
            InitializationError = _head.ReadLine(
                (extended ? "GET" : request.Method)
                    + " "
                    + request.RawTarget
                    + " "
                    + (ProtocolVersion >= HttpVersion.Version20 ? "HTTP/1.1" : request.Protocol)
            );

            foreach (var field in request.Headers)
                foreach (var value in field.Value)
                {
                    InitializationError ??= _head.ReadField(field.Key + ": " + value, out _, out _);
                    HeaderFieldCount++;
                    HeaderFieldBytes += field.Key.Length + (value?.Length ?? 0);
                }

            InitializationError ??= _head.Complete(LocalEndPoint, IsSecureConnection);
            _input = new KestrelInput(
                features.Get<IRequestBodyPipeFeature>()?.Reader
                    ?? throw new InvalidOperationException("Missing request body pipe feature.")
            );
        }

        internal string? InitializationError { get; }
        internal bool CanReleaseAfterResponse => !_hasBody || _input.Complete;
        public NameValueCollection Headers => _head.Fields;
        public bool KeepAlive { get; }
        public string RawTarget => _target;
        public NameValueCollection QueryString => _query ??= QueryStringFields.Parse(Url.Query);
        public string HttpMethod => _method;
        public HttpVerbs HttpVerb => OwnedRequest.IsKnownHttpMethod(HttpMethod, out var verb) ? verb : HttpVerbs.Any;
        public Uri Url => _head.Address;
        public bool HasEntityBody => _hasBody;
        public Encoding ContentEncoding =>
            HasEntityBody ? HeaderUtility.GetEncoding(ContentType) : WebServer.DefaultEncoding;
        public IPEndPoint RemoteEndPoint { get; }
        public IPEndPoint LocalEndPoint { get; }
        public bool IsLocal =>
            IPAddress.IsLoopback(RemoteEndPoint.Address) || LocalEndPoint.Address.Equals(RemoteEndPoint.Address);
        public bool IsSecureConnection { get; }
        public string? UserAgent => Headers["User-Agent"];
        public bool IsWebSocketRequest => _isWebSocketRequest;
        public string? ContentType => Headers["Content-Type"];
        public long ContentLength64 =>
            ProtocolVersion >= HttpVersion.Version20 && _hasBody && Headers["Content-Length"] == null
                ? -1
                : _head.Length;
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer => Uri.TryCreate(Headers["Referer"], UriKind.Absolute, out var value) ? value : null;
        public ICookieCollection Cookies => _cookies ??= OwnedRequest.ParseCookies(Headers["Cookie"] ?? string.Empty);
        public Version ProtocolVersion { get; }
        public X509Certificate2? ClientCertificate { get; }
        public int HeaderFieldCount { get; }
        public long HeaderFieldBytes { get; }
        public RequestPolicySnapshot? CapturedRequestPolicy { get; set; }
        public RawRequestBodyLimitException? KnownBodyRefusal { get; set; }

        private static bool HasToken(string? values, string token)
        {
            if (values == null) return false;

            foreach (var value in values.Split(','))
                if (
                    value
                        .Trim()
                        .Equals(token, StringComparison.OrdinalIgnoreCase)
                ) return true;

            return false;
        }

        public Stream InputStream
        {
            get
            {
                lock (_sync)
                {
                    if (_applicationInput != null) return _applicationInput;

                    Stream input = _input;

                    if (_rate is MinimumDataRate rate)
                        input = _rated = new RequestBodyRateStream(input, rate, _onRate, null);

                    if (_timeout is TimeSpan timeout)
                        input = _timed = new ReadTimeoutStream(input, timeout, _onTimeout);

                    if (_legacyLimit is long maximum)
                        input = new LimitedRequestStream(
                            input,
                            maximum,
                            LimitedRequestStream.TransferredLimitMessage,
                            _onLimit
                        );

                    if (_policy != null)
                        input = _limited = new RawRequestBodyStream(input, _policy.MaximumBytes, KnownBodyRefusal);

                    if (_count != null) input = new CountingReadStream(input, _count);

                    return _applicationInput = input;
                }
            }
        }

        private void RequireUnopened()
        {
            if (_applicationInput != null) throw new InvalidOperationException("The request body was already opened.");
        }

        public void SetBodyPolicy(RequestBodyPolicy policy)
        {
            RequireUnopened();
            _policy = policy;
        }

        public void LimitBody(long maximumBytes, Action? onExceeded)
        {
            RequireUnopened();
            _legacyLimit = maximumBytes;
            _onLimit = onExceeded;
        }

        public void TimeBodyReads(TimeSpan timeout, Action? onTimeout)
        {
            RequireUnopened();
            _timeout = timeout;
            _onTimeout = onTimeout;
        }

        public void EnforceBodyDataRate(MinimumDataRate rate, Action? onFailure)
        {
            RequireUnopened();
            _rate = rate;
            _onRate = onFailure;
        }

        public void CountBody(RequestTelemetry scope)
        {
            RequireUnopened();
            _count = scope;
        }

        public bool IsBodyReadTimeoutFailure(Exception error) =>
            _timed?.IsTimeoutFailure(error) == true || _rated?.IsRateFailure(error) == true;

        public Task CompleteBodyInputAsync(CancellationToken token) =>
            _limited != null && !_limited.IsValidatedEnd && !_limited.ReadInProgress && KnownBodyRefusal == null
                ? _limited.CompleteInputAsync(token)
                : Task.CompletedTask;

        internal async Task RetireAsync()
        {
            _ = TryGetTrailers(out _);
            _limited?.Retire();
            _timed?.Retire();
            _rated?.Retire();
            await _input
                .RetireAsync()
                .ConfigureAwait(false);
            Dispose();
        }

        public void Dispose()
        {
            _applicationInput?.Dispose();
            _limited?.Dispose();
            _timed?.Dispose();
            _rated?.Dispose();
            _input.Dispose();
        }

        public bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers)
        {
            trailers = [];

            if (_trailers != null)
            {
                trailers = _trailers;
                return true;
            }

            if (!_input.Complete || _limited?.HasLimitFailure == true || _limited?.TrailerInputAbandoned == true)
                return false;

            var feature = _context.Features?.Get<IHttpRequestTrailersFeature>();

            if (feature?.Available != true) return false;

            var snapshot = new List<KeyValuePair<string, string>>();

            foreach (var field in feature.Trailers)
                foreach (var value in field.Value) snapshot.Add(new(field.Key, value ?? string.Empty));

            trailers = _trailers = snapshot.AsReadOnly();
            return true;
        }
    }
}
