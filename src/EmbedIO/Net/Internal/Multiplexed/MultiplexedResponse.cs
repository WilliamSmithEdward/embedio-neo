using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedResponse : IHttpResponseSections, IDisposable
    {
        private readonly IMultiplexedExchange _exchange;
        private readonly SemaphoreSlim _gate = new(1, 1);
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0330:Use 'System.Threading.Lock'",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock."
        )]
        private readonly object _lifecycle = new();
        internal Security.SecurityHeadersState? SecurityHeaders;
        private int _operations;
        private Task? _closeTask;
        private readonly Output _output;
        private CookieList? _cookies;
        private int _status = 200;
        private string _contentType = MimeType.Html;
        private bool _headersSent;
        private long _sentBody;

        // Body bytes handed to the exchange, for telemetry.
        internal long SentBodyBytes => Volatile.Read(ref _sentBody);

        // Alt-Svc value from combined hosting, sent unless the application set its own.
        internal string? AlternativeService { get; set; }

        private bool _tunnel;
        private bool _extendedConnectAccepted;
        private bool _extendedConnectRejected;
        private bool _capsuleCarrier;
        private bool _contentTypeConfigured;
        private HashSet<string>? _trailerNames;
        private HpackField[] _trailers = [];
        private volatile bool _closed;

        // Set when the output stream is disposed; later writes fail even before the close takes the gate.
        private volatile bool _outputDisposed;
        private bool _chunked;
        private bool _keepAlive = true;

        internal MultiplexedResponse(IMultiplexedExchange exchange)
        {
            _exchange = exchange;
            _output = new Output(this);
        }

        // The engine reads and writes the head's fields here; the collection exists once the
        // application reads Headers. Room for the seven an ordinary head can have: Content-Type,
        // Server, Alt-Svc, Date and Content-Length, and Vary and Content-Encoding from content
        // negotiation.
        internal ResponseHeaderFields Fields { get; } = new(7);
        public WebHeaderCollection Headers => Fields.Collection;

        public int StatusCode
        {
            get => _status;
            set
            {
                EnsureHeaders();
                SecurityHeaders?.ValidateStatus(value);
                HttpResponseFieldSections.ValidateFinalStatus(value);

                if (_trailerNames != null && (value < 200 || value is 204 or 205 or 304))
                    throw new InvalidOperationException("Reserved trailers require a body-capable final response.");

                _status = value;
                StatusDescription = HttpListenerResponseHelper.GetStatusDescription(value);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1512:Use ArgumentOutOfRangeException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers."
        )]
        public long ContentLength64
        {
            get => long.TryParse(Fields.Value(HttpHeaderNames.ContentLength), out var value) ? value : 0;
            set
            {
                EnsureHeaders();

                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));

                Fields.Set(HttpHeaderNames.ContentLength, value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public string ContentType
        {
            get => _contentType;
            set
            {
                EnsureHeaders();

                if (string.IsNullOrEmpty(value))
                    throw new ArgumentException("Content type is required.", nameof(value));

                _contentType = value;
                _contentTypeConfigured = true;
            }
        }

        public Stream OutputStream => _output;
        internal bool HeadersSent => _headersSent;
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;

        public bool KeepAlive
        {
            get => _keepAlive;
            set
            {
                EnsureHeaders();
                _keepAlive = value;
            }
        }

        public bool SendChunked
        {
            get => _chunked;
            set
            {
                EnsureHeaders();
                _chunked = value;
            }
        }

        private string _statusDescription = "OK";

        // HTTP/2 and HTTP/3 carry no reason phrase, but IHttpResponse is one contract: an
        // application must not get a different answer depending on the protocol it is serving.
        public string StatusDescription
        {
            get => _statusDescription;
            set => _statusDescription = EmbedIO.Internal.HttpTokenSyntax.ValidatedReasonPhrase(value, nameof(value));
        }

        public ICookieCollection Cookies => _cookies ??= [];
        public Version ProtocolVersion => _exchange.ProtocolVersion;
        private bool SuppressBody =>
            _exchange.Request.Method == "HEAD" || _status == 204 || _status == 205 || _status == 304;

        public void SetCookie(Cookie cookie)
        {
            cookie = global::EmbedIO.Utilities.Validate.NotNull(nameof(cookie), cookie);

            EnsureHeaders();
            _cookies ??= [];

            if (
                _cookies.Any(
                    existing => existing.Name == cookie.Name
                        && existing.Domain == cookie.Domain
                        && existing.Path == cookie.Path
                )
            ) throw new ArgumentException("The cookie already exists.", nameof(cookie));

            _cookies.Add(cookie);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        private void EnsureHeaders()
        {
            if (_closed) throw new ObjectDisposedException(nameof(MultiplexedResponse));

            if (_headersSent) throw new InvalidOperationException("Response headers were already sent.");
        }

        internal void BeginTunnel(bool capsules)
        {
            EnsureHeaders();

            if (_trailerNames != null)
                throw new InvalidOperationException("A trailer response cannot become a tunnel.");

            if (_exchange.Request.Method != "CONNECT" || _status < 200 || _status >= 300)
                throw new InvalidOperationException("A tunnel requires a successful CONNECT response.");

            if (capsules)
            {
                HttpCapsuleProtocol.ValidateCarrierHeaders(_exchange.Request.Headers);
                HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);

                if (_contentTypeConfigured || _chunked)
                    throw new InvalidOperationException(
                        "A capsule carrier cannot configure representation type or chunked framing."
                    );

                Fields.Set(HttpCapsuleProtocol.HeaderName, "?1");
            }

            _tunnel = true;
            _extendedConnectAccepted = true;
            _capsuleCarrier = capsules;
        }

        internal void BeginWebSocket()
        {
            EnsureHeaders();
            _extendedConnectAccepted = true;
        }

        // Called under the output gate, before either final-header commitment path.
        // Application dispatch and explicit error responses remain authoritative.
        private void RejectUnacceptedExtendedConnect()
        {
            if (
                _headersSent
                || _extendedConnectAccepted
                || _exchange.Request.Method != "CONNECT"
                || _exchange.Request.Protocol == null
                || _status < 200
                || _status >= 300
            ) return;

            _extendedConnectRejected = true;
            _status = 501;
            StatusDescription = HttpListenerResponseHelper.GetStatusDescription(_status);
            Fields.Set(HttpHeaderNames.ContentLength, "0");
            Fields.Remove("Trailer");
            Fields.Remove(HttpCapsuleProtocol.HeaderName);
            _trailerNames = null;
            _chunked = false;
        }

        private HpackField[] BuildHeaders(bool closing)
        {
            SecurityHeaders?.Commit();

            if (_trailerNames != null && (SuppressBody || _status == 205 || _tunnel))
                throw new InvalidOperationException("Reserved trailers require a body-capable response.");

            if (_capsuleCarrier) HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);

            if (!_tunnel || (_contentTypeConfigured && !_capsuleCarrier))
            {
                var contentType = Fields.Value(HttpHeaderNames.ContentType) ?? _contentType;

                if (ContentEncoding != null)
                    contentType = HttpResponseFieldSections.WithCharset(contentType, ContentEncoding.WebName);

                Fields.Set(HttpHeaderNames.ContentType, contentType);
            }

            if (Fields.Value(HttpHeaderNames.Server) == null) Fields.Set(HttpHeaderNames.Server, WebServer.Signature);

            if (AlternativeService != null && Fields.Value(HttpHeaderNames.AltSvc) == null)
                Fields.Set(HttpHeaderNames.AltSvc, AlternativeService);

            if (Fields.Value(HttpHeaderNames.Date) == null)
                Fields.Set(HttpHeaderNames.Date, HttpResponseFieldSections.CurrentDate());

            if (_chunked) Fields.Remove(HttpHeaderNames.ContentLength);

            if (
                _status == 204
                || _status < 200
                || (_exchange.Request.Method == "CONNECT" && _status >= 200 && _status < 300)
            )
                Fields.Remove(HttpHeaderNames.ContentLength);

            else if (_status == 205 || (closing && !SuppressBody && _trailerNames == null))
                Fields.Set(HttpHeaderNames.ContentLength, "0");

            var status = new HpackField(":status", _status.ToString(CultureInfo.InvariantCulture));

            // Fields that each have one value under a name the collection does not split are
            // read in place, into an array of their own size; any other head goes through the
            // collection's GetValues(name), as it always has.
            if (Fields.IsSimple)
            {
                var result = new HpackField[1 + Fields.Count + (_cookies?.Count ?? 0)];
                var count = 0;
                result[count++] = status;

                for (var index = 0; index < Fields.Count; index++)
                {
                    var name = WireHeaderName(Fields.NameAt(index));

                    if (!IsConnectionField(name)) result[count++] = new HpackField(name, Fields.ValueAt(index) ?? "");
                }

                if (_cookies != null)
                    foreach (var cookie in _cookies)
                        result[count++] = CookieField(cookie);

                if (count < result.Length) Array.Resize(ref result, count);

                return result;
            }

            var headers = Fields.Collection;
            var fields = new List<HpackField>(headers.Count + 1 + (_cookies?.Count ?? 0)) { status };

            foreach (var key in headers.AllKeys)
            {
                if (key == null) continue;

                var name = WireHeaderName(key);

                if (IsConnectionField(name)) continue;

                var values = headers.GetValues(key);

                if (values != null) foreach (var value in values) fields.Add(new HpackField(name, value));
            }

            if (_cookies != null)
                foreach (var cookie in _cookies)
                    fields.Add(CookieField(cookie));

            return [.. fields];
        }

        // The application API preserves these legacy options; Multiplexed HTTP uses its own
        // framing and connection-control frames on the wire.
        private static bool IsConnectionField(string name) =>
            name == "connection"
                || name == "keep-alive"
                || name == "proxy-connection"
                || name == "transfer-encoding"
                || name == "upgrade";

        private HpackField CookieField(Cookie cookie)
        {
            var text = new StringBuilder();
            HttpListenerResponse.AppendCookieHeaderValue(text, cookie, this);
            return new HpackField("set-cookie", text.ToString(), true);
        }

        // Valid names only, bounded so application-generated names cannot grow it without limit.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> WireNames = new(
            StringComparer.Ordinal
        );
        private const int WireNameCacheLimit = 256;

        private static string WireHeaderName(string name)
        {
            if (WireNames.TryGetValue(name, out var cached)) return cached;

            var wire = LowercaseFieldName(name);

            if (WireNames.Count < WireNameCacheLimit) WireNames.TryAdd(name, wire);

            return wire;
        }

        private static string LowercaseFieldName(string name)
        {
            // HTTP/2 and HTTP/3 field names are ASCII tokens and must be lowercase on the wire.
            // Unicode case folding could turn an invalid application name into a valid one.
            char[]? characters = null;

            for (var i = 0; i < name.Length; i++)
            {
                var character = name[i];

                if (!HttpRequestFraming.IsTokenCharacter(character))
                    throw new InvalidDataException("Invalid HTTP field name.");

                if (character >= 'A' && character <= 'Z')
                {
                    characters ??= name.ToCharArray();
                    characters[i] = (char)(character + ('a' - 'A'));
                }
            }

            return characters == null ? name : new string(characters);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        private void EnterSectionConfiguration()
        {
            lock (_lifecycle)
            {
                if (_closed || _outputDisposed) throw new ObjectDisposedException(nameof(MultiplexedResponse));

                _operations++;
            }

            try
            {
                if (!_gate.Wait(0))
                    throw new InvalidOperationException("Cannot configure response sections during output.");
            }

            catch
            {
                ReleaseReference();
                throw;
            }
        }

        public void DeclareTrailers(params string[] fieldNames) => PrepareTrailers(fieldNames);

        internal void PrepareTrailers(string[] names)
        {
            var declared = HttpResponseTrailerFields.Declaration(names);
            EnterSectionConfiguration();

            try
            {
                EnsureHeaders();

                if (
                    SuppressBody
                    || _status == 205
                    || _tunnel
                    || (_exchange.Request.Method == "CONNECT" && _status >= 200 && _status < 300)
                )
                    throw new InvalidOperationException("This response cannot carry trailers.");

                var transport = _exchange as IMultiplexedResponseTrailers
                    ?? throw new NotSupportedException("Transport does not support response trailers.");
                transport.ExpectTrailers();
                Fields.Set("Trailer", string.Join(", ", declared));
                _trailerNames = declared;
            }

            finally
            {
                Exit();
            }
        }

        public void SetTrailers(WebHeaderCollection trailers)
        {
            EnterSectionConfiguration();

            try
            {
                if (_trailerNames == null || SuppressBody || _status == 205 || _tunnel)
                    throw new InvalidOperationException("Trailers were not reserved for an open response.");

                _trailers = HttpResponseTrailerFields.Snapshot(trailers, _trailerNames);
            }

            finally
            {
                Exit();
            }
        }

        public async Task SendInformationalAsync(
            int statusCode,
            WebHeaderCollection headers,
            CancellationToken cancellationToken = default
        )
        {
            var token = cancellationToken;
            var fields = HttpResponseFieldSections.Informational(statusCode, headers);
            await EnterAsync(false, token).ConfigureAwait(false);

            try
            {
                EnsureHeaders();

                if (_tunnel) throw new InvalidOperationException("A tunnel cannot carry informational sections.");

                await _exchange
                    .SendHeadersAsync(fields, false, token)
                    .ConfigureAwait(false);
            }

            finally
            {
                Exit();
            }
        }

        private async Task EnsureSentAsync(bool closing, CancellationToken token)
        {
            if (_headersSent) return;

            RejectUnacceptedExtendedConnect();
            await _exchange
                .SendHeadersAsync(
                    BuildHeaders(closing),
                    _extendedConnectRejected || (closing && _trailerNames == null) || SuppressBody,
                    token
                )
                .ConfigureAwait(false);
            _headersSent = true;
            CompleteAbortObservation();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1513:Use ObjectDisposedException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ObjectDisposedException.ThrowIf."
        )]
        private async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            buffer = global::EmbedIO.Utilities.Validate.NotNull(nameof(buffer), buffer);

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));

            await EnterAsync(false, token).ConfigureAwait(false);

            try
            {
                if (_closed) throw new ObjectDisposedException(nameof(MultiplexedResponse));

                if (count == 0) return;

                RejectUnacceptedExtendedConnect();

                if (_extendedConnectRejected)
                {
                    await EnsureSentAsync(true, token).ConfigureAwait(false);
                    return;
                }

                if (
                    !_headersSent
                    && !SuppressBody
                    && _status >= 200
                    && _exchange is IMultiplexedHeaderCoalescing coalescing
                )
                {
                    try
                    {
                        await coalescing
                            .SendHeadersAndWriteAsync(BuildHeaders(false), buffer, offset, count, token)
                            .ConfigureAwait(false);
                    }

                    finally
                    {
                        _headersSent = coalescing.FinalHeadersSent;
                    }

                    CompleteAbortObservation();
                    Volatile.Write(ref _sentBody, _sentBody + count);
                    return;
                }

                await EnsureSentAsync(false, token).ConfigureAwait(false);

                if (!SuppressBody)
                {
                    await _exchange
                        .WriteAsync(buffer, offset, count, false, token)
                        .ConfigureAwait(false);
                    CompleteAbortObservation();
                    Volatile.Write(ref _sentBody, _sentBody + count);
                }
            }

            finally
            {
                Exit();
            }
        }

        private async Task FlushAsync(CancellationToken token)
        {
            if (!await EnterAsync(true, token).ConfigureAwait(false)) return;

            try
            {
                if (!_closed) await EnsureSentAsync(false, token).ConfigureAwait(false);
            }

            finally
            {
                Exit();
            }
        }

        // The first caller's token governs the shared close; later callers await the same task.
        internal Task CloseAsync(CancellationToken token = default)
        {
            lock (_lifecycle) return _closeTask ??= CloseCoreAsync(token);
        }

        private void CloseFromDispose()
        {
            _outputDisposed = true;
            var closing = CloseAsync();

            // Failures surface to whoever awaits the context close; never leave them unobserved.
            if (!closing.IsCompleted)
                _ = closing.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

            else if (closing.IsFaulted) _ = closing.Exception;
        }

        private async Task CloseCoreAsync(CancellationToken token)
        {
            if (!await EnterAsync(true, CancellationToken.None).ConfigureAwait(false)) return;

            try
            {
                if (_closed) return;

                _closed = true;

                // No further wire operation remains after a committed final end.
                // Drain or peer cancellation cannot undo that successful response.
                if (_headersSent && _exchange.Ended)
                {
                    _exchange.CloseConnectionAfterResponse = !_keepAlive;
                    return;
                }

                if (!token.CanBeCanceled) token = _exchange.CancellationToken;

                token.ThrowIfCancellationRequested();
                _exchange.CloseConnectionAfterResponse = !_keepAlive;
                await EnsureSentAsync(true, token).ConfigureAwait(false);

                if (!_exchange.Ended)
                {
                    if (_trailerNames != null)
                        await ((_exchange as IMultiplexedResponseTrailers)
                            ?? throw new NotSupportedException("Transport does not support response trailers."))
                            .SendTrailersAsync(_trailers, token)
                            .ConfigureAwait(false);

                    else
                        await _exchange
                            .CompleteAsync(token)
                            .ConfigureAwait(false);
                }
            }

            catch
            {
                EmbedIO.Internal.RequestAbortRegistry
                    .Find(_exchange)
                    ?.Abort();
                throw;
            }

            finally
            {
                CompleteAbortObservation();
                _output.Dispose();
                Exit();
            }
        }

        private void CompleteAbortObservation()
        {
            if (_exchange.Ended) EmbedIO.Internal.RequestAbortRegistry.Complete(_exchange);
        }

        private async Task<bool> EnterAsync(bool closing, CancellationToken token)
        {
            lock (_lifecycle)
            {
                if (_closed || (_outputDisposed && !closing))
                {
                    if (closing) return false;

                    throw new ObjectDisposedException(nameof(MultiplexedResponse));
                }

                _operations++;
            }

            try
            {
                await _gate
                    .WaitAsync(token)
                    .ConfigureAwait(false);
                return true;
            }

            catch
            {
                ReleaseReference();
                throw;
            }
        }

        private void Exit()
        {
            _gate.Release();
            ReleaseReference();
        }

        private void ReleaseReference()
        {
            lock (_lifecycle)
            {
                _operations--;

                if (_closed && _operations == 0) _gate.Dispose();
            }
        }

        public void Close() =>
            CloseAsync()
                .GetAwaiter()
                .GetResult();

        public void Dispose()
        {
            try
            {
                Close();
            }

            finally
            {
                lock (_lifecycle)
                    if (_closed && _operations == 0) _gate.Dispose();
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1844:Override memory-based WriteAsync",
            Justification = "The shared implementation targets netstandard2.0, which has no memory-based Stream.WriteAsync override."
        )]
        private sealed class Output : Stream
        {
            private readonly MultiplexedResponse _owner;

            internal Output(MultiplexedResponse owner) => _owner = owner;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !_owner._closed && !_owner._outputDisposed;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() =>
                _owner
                    .FlushAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override Task FlushAsync(CancellationToken token) => _owner.FlushAsync(token);

            public override void Write(byte[] buffer, int offset, int count) =>
                _owner
                    .WriteAsync(buffer, offset, count, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
                _owner.WriteAsync(buffer, offset, count, token);
#if NET10_0_OR_GREATER
            public override ValueTask DisposeAsync() => new(_owner.CloseAsync());
#endif
            // Writers such as StreamWriter close their stream synchronously, often on a
            // worker. Waiting here for the shared multiplexed output can starve the pool,
            // so disposal only starts the close; the context's CloseAsync awaits the same task.
            protected override void Dispose(bool disposing)
            {
                if (disposing && !_owner._closed) _owner.CloseFromDispose();

                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
