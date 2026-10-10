using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Utilities;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedResponse : IHttpResponse, IDisposable
    {
        private readonly IMultiplexedExchange _exchange;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lifecycle = new();
        private int _operations;
        private Task? _closeTask;
        private readonly Output _output;
        private CookieList? _cookies;
        private int _status = 200;
        private string _contentType = MimeType.Html;
        private bool _headersSent;
        private bool _tunnel;
        private bool _capsuleCarrier;
        private bool _contentTypeConfigured;
        private volatile bool _closed;
        // Set when the output stream is disposed; later writes fail even before the close takes the gate.
        private volatile bool _outputDisposed;
        private bool _chunked;
        private bool _keepAlive = true;
        internal MultiplexedResponse(IMultiplexedExchange exchange) { _exchange = exchange; _output = new Output(this); }
        public WebHeaderCollection Headers { get; } = new();
        public int StatusCode
        {
            get => _status;
            set { EnsureHeaders(); if (value < 100 || value > 999) throw new ArgumentOutOfRangeException(nameof(value)); _status = value; StatusDescription = HttpListenerResponseHelper.GetStatusDescription(value); }
        }
        public long ContentLength64
        {
            get => long.TryParse(Headers[HttpHeaderNames.ContentLength], out var value) ? value : 0;
            set { EnsureHeaders(); if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); Headers[HttpHeaderNames.ContentLength] = value.ToString(CultureInfo.InvariantCulture); }
        }
        public string ContentType
        {
            get => _contentType;
            set { EnsureHeaders(); if (string.IsNullOrEmpty(value)) throw new ArgumentException("Content type is required.", nameof(value)); _contentType = value; _contentTypeConfigured = true; }
        }
        public Stream OutputStream => _output;
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;
        public bool KeepAlive { get => _keepAlive; set { EnsureHeaders(); _keepAlive = value; } }
        public bool SendChunked { get => _chunked; set { EnsureHeaders(); _chunked = value; } }
        public string StatusDescription { get; set; } = "OK";
        public ICookieCollection Cookies => _cookies ??= new CookieList();
        public Version ProtocolVersion => _exchange.ProtocolVersion;
        private bool SuppressBody => _exchange.Request.Method == "HEAD" || _status == 204 || _status == 205 || _status == 304;
        public void SetCookie(Cookie cookie)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            EnsureHeaders();
            _cookies ??= new CookieList();
            if (_cookies.Any(existing => existing.Name == cookie.Name && existing.Domain == cookie.Domain && existing.Path == cookie.Path)) throw new ArgumentException("The cookie already exists.", nameof(cookie));
            _cookies.Add(cookie);
        }
        private void EnsureHeaders()
        {
            if (_closed) throw new ObjectDisposedException(nameof(MultiplexedResponse));
            if (_headersSent) throw new InvalidOperationException("Response headers were already sent.");
        }
        internal void BeginTunnel(bool capsules)
        {
            EnsureHeaders();
            if (_exchange.Request.Method != "CONNECT" || _status < 200 || _status >= 300)
                throw new InvalidOperationException("A tunnel requires a successful CONNECT response.");
            if (capsules)
            {
                HttpCapsuleProtocol.ValidateCarrierHeaders(_exchange.Request.Headers);
                HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);
                if (_contentTypeConfigured || _chunked)
                    throw new InvalidOperationException("A capsule carrier cannot configure representation type or chunked framing.");
                Headers[HttpCapsuleProtocol.HeaderName] = "?1";
            }
            _tunnel = true;
            _capsuleCarrier = capsules;
        }
        private HpackField[] BuildHeaders(bool closing)
        {
            if (_capsuleCarrier) HttpCapsuleProtocol.ValidateCarrierHeaders(Headers, _status);
            if (!_tunnel || (_contentTypeConfigured && !_capsuleCarrier))
            {
                var contentType = Headers[HttpHeaderNames.ContentType] ?? _contentType;
                if (ContentEncoding != null) contentType = WithCharset(contentType, ContentEncoding.WebName);
                Headers[HttpHeaderNames.ContentType] = contentType;
            }
            if (Headers[HttpHeaderNames.Server] == null) Headers[HttpHeaderNames.Server] = WebServer.Signature;
            if (Headers[HttpHeaderNames.Date] == null) Headers[HttpHeaderNames.Date] = CurrentDate();
            if (_chunked) Headers.Remove(HttpHeaderNames.ContentLength);
            if (_status == 204 || _status < 200 || (_exchange.Request.Method == "CONNECT" && _status >= 200 && _status < 300))
                Headers.Remove(HttpHeaderNames.ContentLength);
            else if (_status == 205 || (closing && !SuppressBody)) Headers[HttpHeaderNames.ContentLength] = "0";
            var fields = new List<HpackField>(Headers.Count + 1 + (_cookies?.Count ?? 0)) { new(":status", _status.ToString(CultureInfo.InvariantCulture)) };
            foreach (var key in Headers.AllKeys)
            {
                if (key == null) continue;
                var name = WireHeaderName(key);
                // The application API preserves these legacy options; Multiplexed HTTP uses
                // its own framing and connection-control frames on the wire.
                if (name == "connection" || name == "keep-alive" || name == "proxy-connection" || name == "transfer-encoding" || name == "upgrade") continue;
                var values = Headers.GetValues(key);
                if (values != null) foreach (var value in values) fields.Add(new HpackField(name, value));
            }
            if (_cookies != null)
            {
                foreach (var cookie in _cookies)
                {
                    var text = new StringBuilder();
                    HttpListenerResponse.AppendCookieHeaderValue(text, cookie, this);
                    fields.Add(new HpackField("set-cookie", text.ToString(), true));
                }
            }
            return fields.ToArray();
        }
        // Valid names only, bounded so application-generated names cannot grow it without limit.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> WireNames = new(StringComparer.Ordinal);
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
                if (!HttpRequestFraming.IsTokenCharacter(character)) throw new InvalidDataException("Invalid HTTP field name.");
                if (character >= 'A' && character <= 'Z')
                {
                    characters ??= name.ToCharArray();
                    characters[i] = (char)(character + ('a' - 'A'));
                }
            }
            return characters == null ? name : new string(characters);
        }
        private sealed class CachedDate
        {
            internal CachedDate(long second, string text) { Second = second; Text = text; }
            internal readonly long Second;
            internal readonly string Text;
        }

        private sealed class CachedContentType
        {
            internal CachedContentType(string contentType, string charset, string text) { ContentType = contentType; Charset = charset; Text = text; }
            internal readonly string ContentType;
            internal readonly string Charset;
            internal readonly string Text;
        }

        private static CachedDate? _date;
        private static CachedContentType? _contentTypeWithCharset;

        // RFC 1123 dates have one-second resolution, so one string serves a whole second.
        private static string CurrentDate()
        {
            var now = DateTime.UtcNow;
            var second = now.Ticks / TimeSpan.TicksPerSecond;
            var cached = Volatile.Read(ref _date);
            if (cached != null && cached.Second == second) return cached.Text;
            var text = HttpDate.Format(now);
            Volatile.Write(ref _date, new CachedDate(second, text));
            return text;
        }

        // Appends the charset unless the media type already declares one. The result
        // depends only on both inputs, so the last pair is reused.
        private static string WithCharset(string contentType, string charset)
        {
            var cached = Volatile.Read(ref _contentTypeWithCharset);
            if (cached != null && string.Equals(cached.ContentType, contentType, StringComparison.Ordinal)
                && string.Equals(cached.Charset, charset, StringComparison.Ordinal))
                return cached.Text;
            var text = MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.CharSet != null
                ? contentType : contentType + "; charset=" + charset;
            Volatile.Write(ref _contentTypeWithCharset, new CachedContentType(contentType, charset, text));
            return text;
        }

        private async Task EnsureSentAsync(bool closing, CancellationToken token)
        {
            if (_headersSent) return;
            await _exchange.SendHeadersAsync(BuildHeaders(closing), closing || SuppressBody, token).ConfigureAwait(false);
            _headersSent = true;
        }
        private async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await EnterAsync(token, false).ConfigureAwait(false);
            try
            {
                if (_closed) throw new ObjectDisposedException(nameof(MultiplexedResponse));
                if (count == 0) return;
                if (!_headersSent && !SuppressBody && _status >= 200 && _exchange is IMultiplexedHeaderCoalescing coalescing)
                {
                    try { await coalescing.SendHeadersAndWriteAsync(BuildHeaders(false), buffer, offset, count, token).ConfigureAwait(false); }
                    finally { _headersSent = coalescing.FinalHeadersSent; }
                    return;
                }
                await EnsureSentAsync(false, token).ConfigureAwait(false);
                if (!SuppressBody) await _exchange.WriteAsync(buffer, offset, count, false, token).ConfigureAwait(false);
            }
            finally { Exit(); }
        }
        private async Task FlushAsync(CancellationToken token)
        {
            if (!await EnterAsync(token, true).ConfigureAwait(false)) return;
            try { if (!_closed) await EnsureSentAsync(false, token).ConfigureAwait(false); }
            finally { Exit(); }
        }
        // The first caller's token governs the shared close; later callers await the same task.
        internal Task CloseAsync(CancellationToken token = default) { lock (_lifecycle) return _closeTask ??= CloseCoreAsync(token); }

        private void CloseFromDispose()
        {
            _outputDisposed = true;
            var closing = CloseAsync();
            // Failures surface to whoever awaits the context close; never leave them unobserved.
            if (!closing.IsCompleted)
                _ = closing.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else if (closing.IsFaulted) _ = closing.Exception;
        }
        private async Task CloseCoreAsync(CancellationToken token)
        {
            if (!await EnterAsync(CancellationToken.None, true).ConfigureAwait(false)) return;
            try
            {
                if (_closed) return;
                _closed = true;
                if (!token.CanBeCanceled) token = _exchange.CancellationToken;
                token.ThrowIfCancellationRequested();
                _exchange.CloseConnectionAfterResponse = !_keepAlive;
                await EnsureSentAsync(true, token).ConfigureAwait(false);
                if (!_exchange.Ended) await _exchange.CompleteAsync(token).ConfigureAwait(false);
            }
            finally { _output.Dispose(); Exit(); }
        }
        private async Task<bool> EnterAsync(CancellationToken token, bool closing)
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
            try { await _gate.WaitAsync(token).ConfigureAwait(false); return true; }
            catch { ReleaseReference(); throw; }
        }
        private void Exit() { _gate.Release(); ReleaseReference(); }
        private void ReleaseReference()
        {
            lock (_lifecycle)
            {
                _operations--;
                if (_closed && _operations == 0) _gate.Dispose();
            }
        }
        public void Close() => CloseAsync().GetAwaiter().GetResult();
        public void Dispose()
        {
            try { Close(); }
            finally
            {
                lock (_lifecycle)
                    if (_closed && _operations == 0) _gate.Dispose();
            }
        }

        private sealed class Output : Stream
        {
            private readonly MultiplexedResponse _owner;
            internal Output(MultiplexedResponse owner) { _owner = owner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => !_owner._closed && !_owner._outputDisposed;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => _owner.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
            public override Task FlushAsync(CancellationToken token) => _owner.FlushAsync(token);
            public override void Write(byte[] buffer, int offset, int count) => _owner.WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => _owner.WriteAsync(buffer, offset, count, token);
#if NET10_0_OR_GREATER
            public override ValueTask DisposeAsync() => new(_owner.CloseAsync());
#endif
            // Writers such as StreamWriter close their stream synchronously, often on a
            // worker. Waiting here for the shared multiplexed output can starve the pool,
            // so disposal only starts the close; the context's CloseAsync awaits the same task.
            protected override void Dispose(bool disposing) { if (disposing && !_owner._closed) _owner.CloseFromDispose(); base.Dispose(disposing); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
