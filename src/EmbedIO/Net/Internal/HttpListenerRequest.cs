using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    // Application view of one parsed HTTP/1 head. Framing facts belong to the
    // head owner; body access borrows the existing session's request transport.
    internal sealed class HttpListenerRequest : IHttpRequestTrailers, IRequestPolicyTarget, IClientCertificateCarrier
    {
        private readonly HttpConnection _connection;
        private readonly HttpListenerContext _context;
        private readonly Http1RequestHead _head = new();
        private CookieList? _cookies;
        private NameValueCollection? _queryString;
        private Stream? _inputStream;
        private Stream? _applicationInput;
        private int _continueStarted;
        private Func<CancellationToken, Task>? _sendContinue;
        private int _headerFieldCount;
        private long _headerFieldBytes;
        private RequestBodyPolicy? _bodyPolicy;
        private RawRequestBodyLimitException? _knownBodyRefusal;
        private RawRequestBodyStream? _limitedInput;
        private Stream? _rawApplicationInput;
        private int _bodyLimitRetired;
        private object? _bodyCleanupSync;
        private Task? _bodyCleanup;
        private CancellationToken _bodyCleanupToken;
        private bool? _keepAlive;

        internal HttpListenerRequest(HttpListenerContext context)
        { _context = context; _connection = context.Connection; }
        public NameValueCollection Headers => _head.Fields;
        public string HttpMethod => _head.Method;
        public HttpVerbs HttpVerb { get; private set; }
        public string RawTarget => _head.Target;
        public Uri Url => _head.Address;
        public Version ProtocolVersion => _head.Version;
        public IPEndPoint LocalEndPoint => _connection.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;
        public bool IsSecureConnection => _connection.IsSecure;
        public System.Security.Cryptography.X509Certificates.X509Certificate2? ClientCertificate => _connection.ClientCertificate;
        public bool IsLocal => LocalEndPoint.Address.Equals(RemoteEndPoint.Address);
        public bool IsAuthenticated => false;
        public string? ContentType => Headers[HttpHeaderNames.ContentType];
        public string? UserAgent => Headers[HttpHeaderNames.UserAgent];
        public Uri? UrlReferrer { get; private set; }
        public ICookieCollection Cookies => _cookies ??= new CookieList();
        public long ContentLength64 => _head.Framed ? _head.Length
            : long.TryParse(Headers[HttpHeaderNames.ContentLength], out var length) ? length : 0;
        public bool HasEntityBody => _head.Chunked || ContentLength64 > 0;
        internal Stream RawInputStream => FramingInput;
        private Stream FramingInput => _inputStream ??= HasEntityBody
            ? _connection.GetRequestStream(ContentLength64, _head.Chunked) : Stream.Null;
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
                if (!HasEntityBody || (_sendContinue == null && _readTimeout == null && _countBody == null && _legacyBodyLimit < 0)) return FramingInput;
                var current = Volatile.Read(ref _applicationInput);
                if (current != null)
                {
                    if (Volatile.Read(ref _bodyLimitRetired) != 0) Volatile.Read(ref _timedInput)?.Retire();
                    return current;
                }
                // Resolve the physical framing owner before taking the publication
                // gate, so request publication cannot invert the connection lock.
                var framing = FramingInput;
                ReadTimeoutStream? timed;
                lock (LazyInitializer.EnsureInitialized(ref _applicationInputSync)
                    ?? throw new InvalidOperationException("Missing application input publication."))
                {
                    if (_applicationInput == null)
                    {
                        var input = framing;
                        if (_countBody != null && _bodyPolicy == null) input = new CountingReadStream(input, _countBody);
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
        internal void DeferContinue(Func<CancellationToken, Task> send) => _sendContinue = send;
        internal bool IsContinueOwed => _sendContinue != null && Volatile.Read(ref _continueStarted) == 0;
        private Task StartContinue(CancellationToken token)
        {
            Volatile.Write(ref _continueStarted, 1);
            return (_sendContinue ?? throw new InvalidOperationException("No 100 Continue is owed."))(token);
        }
        int IRequestPolicyTarget.HeaderFieldCount => _headerFieldCount;
        long IRequestPolicyTarget.HeaderFieldBytes => _headerFieldBytes;
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
                    var created = new RawRequestBodyStream(ApplicationInput, _bodyPolicy.MaximumBytes, KnownBodyRefusal,
                        FramingInput as IRequestTrailerSource);
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
        internal bool IsBodyReadTimeoutFailure(Exception error) => Volatile.Read(ref _timedInput)?.IsTimeoutFailure(error) == true;
        internal RequestPolicySnapshot? CapturedRequestPolicy { get; private set; }
        internal void CaptureRequestPolicy(RequestPolicySnapshot policy) => CapturedRequestPolicy = policy;
        internal void SetBodyPolicy(RequestBodyPolicy policy) => _bodyPolicy = policy;
        internal RawRequestBodyLimitException? KnownBodyRefusal => Volatile.Read(ref _knownBodyRefusal);
        internal void MarkKnownBodyRefusal(RawRequestBodyLimitException failure)
            => Interlocked.CompareExchange(ref _knownBodyRefusal, failure, null);
        internal bool IsBodyConsumed => !HasEntityBody || FramingInput is RequestStream body && body.IsBodyConsumed;
        internal bool HasBodyLimitFailure => Volatile.Read(ref _limitedInput)?.HasLimitFailure == true;
        internal void RetireBodyLimit()
        {
            if (_bodyPolicy == null && _readTimeout == null) return;
            Interlocked.Exchange(ref _bodyLimitRetired, 1);
            Volatile.Read(ref _limitedInput)?.Retire();
            Volatile.Read(ref _timedInput)?.Retire();
        }
        internal Task CompleteBodyInputAsync(CancellationToken token)
        {
            if (_bodyPolicy == null || KnownBodyRefusal != null || IsContinueOwed) return Task.CompletedTask;
            TaskCompletionSource<bool> completion;
            CancellationToken readToken;
            lock (LazyInitializer.EnsureInitialized(ref _bodyCleanupSync)
                ?? throw new InvalidOperationException("Missing raw body cleanup gate."))
            {
                if (_bodyCleanup != null) return _bodyCleanup;
                completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                readToken = token.CanBeCanceled ? token : _context.CancellationToken;
                _bodyCleanupToken = readToken;
                _bodyCleanup = completion.Task;
            }
            // Publication precedes the first read, and the borrowed reader is
            // never invoked under the publication gate. The actor and pipeline
            // retain this request's exact outcome even after transport retirement.
            var cleanup = CompleteBodyInputCoreAsync(readToken);
            _ = cleanup.ContinueWith(static (finished, state) =>
            {
                var result = state is ValueTuple<TaskCompletionSource<bool>, CancellationToken> captured
                    ? captured : throw new InvalidOperationException("Missing cleanup publication.");
                if (finished.IsCanceled) result.Item1.TrySetCanceled(result.Item2);
                else if (finished.IsFaulted)
                    result.Item1.TrySetException((finished.Exception
                        ?? throw new InvalidOperationException("Missing cleanup failure.")).InnerExceptions);
                else result.Item1.TrySetResult(finished.Result);
            }, (completion, readToken), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return completion.Task;
        }
        internal bool IsBodyCleanupCancellation(OperationCanceledException error)
        {
            var gate = Volatile.Read(ref _bodyCleanupSync);
            if (gate == null) return false;
            lock (gate)
                return _bodyCleanup?.IsCanceled == true && _bodyCleanupToken.IsCancellationRequested
                    && error.CancellationToken == _bodyCleanupToken;
        }
        private async Task<bool> CompleteBodyInputCoreAsync(CancellationToken token)
        {
            // Nonpersistent response completion may have already retired this
            // exact request. Its discarded input is not a validated EOF, and a
            // sticky limit failure remains the original failure after retirement.
            if (Volatile.Read(ref _bodyLimitRetired) != 0) return DiscardRetiredInput();
            try
            {
                _ = InputStream;
                await (_limitedInput ?? throw new InvalidOperationException("Missing raw body view."))
                    .CompleteInputAsync(token).ConfigureAwait(false);
                return true;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _bodyLimitRetired) != 0)
            { return DiscardRetiredInput(); }
        }
        private bool DiscardRetiredInput()
        {
            var failure = Volatile.Read(ref _limitedInput)?.LimitFailure;
            if (failure != null) throw failure;
            return false;
        }
        public bool TryGetTrailers(out IReadOnlyList<KeyValuePair<string, string>> trailers)
        {
            trailers = RequestTrailerSnapshot.Empty;
            var available = _inputStream is IRequestTrailerSource source
                ? source.TryGetTrailers(out trailers) : !HasEntityBody;
            // Check the monotonic raw outcome after the physical snapshot, so
            // overlapping disposal cannot publish metadata from a later EOF.
            if (Volatile.Read(ref _limitedInput) is { } raw && (raw.ReadInProgress || raw.HasLimitFailure || raw.TrailerInputAbandoned))
            {
                trailers = RequestTrailerSnapshot.Empty;
                return false;
            }
            return available;
        }
        internal bool HasBodyFramingFailure => _inputStream is RequestStream body && body.HasBodyFramingFailure;
        internal bool IsBodyFramingError(Exception error) => _inputStream is RequestStream body && body.IsFramingError(error);
        internal bool RequiresContinue => ProtocolVersion >= HttpVersion.Version11 && HasEntityBody
            && HttpExpectations.ContainsContinue(Headers["Expect"]);
        public bool IsWebSocketRequest => HttpVerb == HttpVerbs.Get && ProtocolVersion >= HttpVersion.Version11
            && Headers.Contains(HttpHeaderNames.Upgrade, "websocket", StringComparison.OrdinalIgnoreCase)
            && Headers.Contains(HttpHeaderNames.Connection, "Upgrade", StringComparison.OrdinalIgnoreCase);
        public bool KeepAlive => _keepAlive ??= !Headers.Contains(HttpHeaderNames.Connection, "close", StringComparison.OrdinalIgnoreCase)
            && (ProtocolVersion >= HttpVersion.Version11 || Headers.Contains(HttpHeaderNames.Connection, "keep-alive", StringComparison.OrdinalIgnoreCase));
        public Encoding ContentEncoding
            => HasEntityBody ? HeaderUtility.GetEncoding(ContentType) : WebServer.DefaultEncoding;
        public NameValueCollection QueryString
        {
            get
            {
                var current = Volatile.Read(ref _queryString);
                if (current != null) return current;
                var created = new NameValueCollection();
                return Interlocked.CompareExchange(ref _queryString, created, null) ?? created;
            }
        }
        internal void SetRequestLine(string line)
        {
            var error = _head.ReadLine(line);
            if (error != null) { _connection.SetError(error); return; }
            HttpVerb = IsKnownHttpMethod(HttpMethod, out var method) ? method : HttpVerbs.Any;
        }
        internal void AddHeader(string line)
        {
            var error = _head.ReadField(line, out var name, out var value);
            if (error != null) { _connection.SetError(error); return; }
            _headerFieldCount++;
            _headerFieldBytes += name.Length + value.Length;
            // The field combines repeated lines; parse it whole, as HTTP/2 parses its joined cookie field.
            if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) _cookies = ParseCookies(_head.Fields[name] ?? value);
            else if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                try { UrlReferrer = new Uri(value); }
                catch (UriFormatException) { UrlReferrer = null; }
            }
        }
        internal void FinishInitialization()
        {
            var error = _head.Complete(LocalEndPoint, IsSecureConnection);
            if (error != null) { _connection.SetError(error); return; }
            var query = Url.Query;
            if (query.Length == 0) return;
            QueryStringFields.AddTo(query, QueryString);
        }
        internal bool FlushInput()
        {
            if (!HasEntityBody) return true;
            if (KnownBodyRefusal != null || IsContinueOwed) return false;
            if (_bodyPolicy != null) return FlushInputAsync().GetAwaiter().GetResult();
            Stream input;
            try { input = InputStream; }
            catch (ObjectDisposedException) { _inputStream = null; return true; }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            if (input is RequestStream body && body.IsBodyConsumed) return true;
            var count = ContentLength64 > 0 ? (int)Math.Min(ContentLength64, 2048) : 2048;
            var buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                while (input.Read(buffer, 0, count) != 0) { }
                return true;
            }
            catch (ObjectDisposedException) { _inputStream = null; return true; }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        internal async Task<bool> FlushInputAsync()
        {
            if (!HasEntityBody) return true;
            if (IsContinueOwed) return false;
            if (_bodyPolicy != null)
            {
                try
                {
                    await CompleteBodyInputAsync(CancellationToken.None).ConfigureAwait(false);
                    return Volatile.Read(ref _bodyLimitRetired) == 0 && IsBodyConsumed;
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            }
            var input = InputStream;
            if (input is RequestStream body && body.IsBodyConsumed) return true;
            var buffer = ArrayPool<byte>.Shared.Rent(2048);
            try
            {
                while (await input.ReadAsync(buffer, 0, 2048, CancellationToken.None).ConfigureAwait(false) != 0) { }
                return input is not RequestStream remaining || remaining.IsBodyConsumed;
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { return false; }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        internal static CookieList ParseCookies(string value) => RequestCookieFields.Read(value);
        internal static bool IsKnownHttpMethod(string method, out HttpVerbs verb)
        {
            verb = method switch
            {
                "GET" => HttpVerbs.Get,
                "HEAD" => HttpVerbs.Head,
                "POST" => HttpVerbs.Post,
                "PUT" => HttpVerbs.Put,
                "DELETE" => HttpVerbs.Delete,
                "PATCH" => HttpVerbs.Patch,
                "OPTIONS" => HttpVerbs.Options,
                "QUERY" => HttpVerbs.Query,
                _ => HttpVerbs.Any,
            };
            return verb != HttpVerbs.Any;
        }
    }
}
