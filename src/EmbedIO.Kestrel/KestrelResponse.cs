using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net;
using EmbedIO.Net.Internal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace EmbedIO.Kestrel
{
    internal sealed class KestrelResponse : IHttpResponse, IDisposable, IHttpBackendResponse
    {
        private readonly KestrelContext _context;
        private readonly bool _ignoreErrors;
        private readonly string _keepAliveHeader;
        private readonly AsyncWriteGate _writers = new();
        private readonly Output _output;
        private readonly object _sync = new();
        private Task? _completion;
        private int _closed;
        private int _status = 200;
        private string _reason = "OK";
        private bool _chunked;
        private bool _keepAlive;
        private bool _upgraded;
        private bool _headersSent;
        private long _sentBodyBytes;
        private CookieList? _cookies;

        internal KestrelResponse(KestrelContext context, bool ignoreErrors, TimeSpan keepAliveTimeout)
        {
            _context = context;
            _ignoreErrors = ignoreErrors;
            _keepAliveHeader = "timeout="
                + Math
                    .Ceiling(keepAliveTimeout.TotalSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            _keepAlive = context.Request.KeepAlive;
            _output = new Output(this);
        }

        public ResponseHeaderFields Fields { get; } = new(9);
        public long SentBodyBytes => Volatile.Read(ref _sentBodyBytes);
        public Security.SecurityHeadersState? SecurityHeaders { get; set; }
        public WebHeaderCollection Headers => Fields.Collection;
        internal bool HeadersSent => Volatile.Read(ref _headersSent);

        public int StatusCode
        {
            get => _status;
            set
            {
                RequireMutable();
                HttpResponseFieldSections.ValidateFinalStatus(value);
                SecurityHeaders?.ValidateStatus(value);
                _status = value;
                _reason = HttpListenerResponseHelper.GetStatusDescription(value);
            }
        }

        public long ContentLength64
        {
            get =>
                long.TryParse(
                    Fields.Value("Content-Length"),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value
                )
                    ? value
                    : 0;
            set
            {
                RequireMutable();
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                Fields.Set("Content-Length", value.ToString(CultureInfo.InvariantCulture));
            }
        }

        public string ContentType
        {
            get => Fields.Value("Content-Type") ?? MimeType.Html;
            set
            {
                RequireMutable();
                ArgumentException.ThrowIfNullOrEmpty(value);
                Fields.Set("Content-Type", value);
            }
        }

        public Stream OutputStream => _output;
        public Encoding? ContentEncoding { get; set; } = WebServer.DefaultEncoding;

        public bool KeepAlive
        {
            get => _keepAlive;
            set
            {
                RequireMutable();
                _keepAlive = value;
            }
        }

        public bool SendChunked
        {
            get => _chunked;
            set
            {
                RequireMutable();
                _chunked = value;
            }
        }

        public string StatusDescription
        {
            get => _reason;
            set
            {
                RequireMutable();
                _reason = HttpTokenSyntax.ValidatedReasonPhrase(value, nameof(value));
            }
        }

        public ICookieCollection Cookies => _cookies ??= [];
        public Version ProtocolVersion => _context.Request.ProtocolVersion;
        private bool SuppressBody => _context.Request.HttpMethod == "HEAD" || _status is 204 or 205 or 304;

        public void SetCookie(Cookie cookie)
        {
            ArgumentNullException.ThrowIfNull(cookie);
            RequireMutable();

            if (
                _cookies?.Any(
                    existing => existing.Name == cookie.Name
                        && existing.Domain == cookie.Domain
                        && existing.Path == cookie.Path
                )
                == true
            )
                throw new ArgumentException("The cookie already exists.", nameof(cookie));

            (_cookies ??= []).Add(cookie);
        }

        private void RequireMutable()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);

            if (HeadersSent) throw new InvalidOperationException("Response headers were already sent.");
        }

        private void PrepareHeaders(bool ending)
        {
            if (HeadersSent) return;

            var feature = _context.Features?.Get<IHttpResponseFeature>()
                ?? throw new ObjectDisposedException(nameof(KestrelContext));
            SecurityHeaders?.Commit();

            if (!_upgraded)
            {
                Fields.Set(
                    "Content-Type",
                    ContentEncoding != null
                        ? HttpResponseFieldSections.WithCharset(ContentType, ContentEncoding.WebName)
                        : ContentType
                );

                if (_status == 204)
                {
                    Fields.Remove("Content-Length");
                    Fields.Remove("Transfer-Encoding");
                }

                else if (_status == 205)
                {
                    Fields.Set("Content-Length", "0");
                    Fields.Remove("Transfer-Encoding");
                }

                else if (ending && Fields.Value("Content-Length") == null && _context.Request.HttpMethod != "HEAD")
                    Fields.Set("Content-Length", "0");

                if (_chunked && !SuppressBody && !ending)
                {
                    Fields.Remove("Content-Length");
                    Fields.Set("Transfer-Encoding", "chunked");
                }

                var persistent = _keepAlive && _status is not (400 or 408 or 411 or 413 or 414 or 500 or 503);

                if (ProtocolVersion < HttpVersion.Version20)
                {
                    Fields.Set("Connection", persistent ? "keep-alive" : "close");

                    if (persistent && ProtocolVersion >= HttpVersion.Version11)
                        Fields.Set("Keep-Alive", _keepAliveHeader);
                }

                else
                {
                    Fields.Remove("Connection");
                    Fields.Remove("Keep-Alive");
                    Fields.Remove("Transfer-Encoding");
                }
            }

            if (Fields.Value("Server") == null) Fields.Set("Server", WebServer.Signature);

            if (Fields.Value("Date") == null) Fields.Set("Date", HttpResponseFieldSections.CurrentDate());

            feature.StatusCode = _status;
            feature.ReasonPhrase = _reason;

            for (var index = 0; index < Fields.Count; index++)
            {
                var name = Fields.NameAt(index);
                var values = Fields.Values(name) ?? [];
                feature.Headers[name] = new StringValues(values);
            }

            if (_cookies != null)
                foreach (var cookie in _cookies)
                {
                    var value = new StringBuilder();
                    ResponseCookieFormatter.Append(value, cookie);
                    CookieSameSiteStore.Append(value, this, cookie);
                    feature.Headers.Append("Set-Cookie", value.ToString());
                }

            Volatile.Write(ref _headersSent, true);
        }

        internal void PrepareWebSocket(string key, string protocol)
        {
            RequireMutable();
            _upgraded = true;
            var extended = ProtocolVersion >= HttpVersion.Version20;
            _status = extended ? 200 : 101;
            _reason = extended ? "OK" : "Switching Protocols";

            if (!extended)
            {
                Fields.Set("Connection", "Upgrade");
                Fields.Set("Upgrade", "websocket");
                Fields.Set(
                    "Sec-WebSocket-Accept",
                    Convert.ToBase64String(
                        SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
                    )
                );
            }

            if (protocol.Length != 0) Fields.Set("Sec-WebSocket-Protocol", protocol);

            PrepareHeaders(false);
        }

        internal async Task WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0 || _upgraded, this);

            if (SuppressBody || buffer.Length == 0) return;

            PrepareHeaders(false);
            var body = _context.Features?.Get<IHttpResponseBodyFeature>()
                ?? throw new ObjectDisposedException(nameof(KestrelContext));

            try
            {
                await body.Stream
                    .WriteAsync(buffer, token)
                    .ConfigureAwait(false);
                Interlocked.Add(ref _sentBodyBytes, buffer.Length);
            }

            catch (IOException) when (_ignoreErrors)
            {
                _context.AbortTransport();
            }
        }

        internal async Task FlushAsync(CancellationToken token)
        {
            if (Volatile.Read(ref _closed) != 0 || _upgraded) return;

            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);

            if (Volatile.Read(ref _closed) != 0 || _upgraded) return;

            PrepareHeaders(false);
            var body = _context.Features?.Get<IHttpResponseBodyFeature>()
                ?? throw new ObjectDisposedException(nameof(KestrelContext));

            try
            {
                await body.Stream
                    .FlushAsync(token)
                    .ConfigureAwait(false);
            }

            catch (IOException) when (_ignoreErrors)
            {
                _context.AbortTransport();
            }
        }

        internal Task CompleteAsync()
        {
            TaskCompletionSource<bool> completion;

            lock (_sync)
            {
                if (_completion != null) return _completion;

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _completion = completion.Task;
            }

            _ = FinishCompleteAsync(completion);
            return completion.Task;
        }

        private async Task FinishCompleteAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await CompleteCoreAsync().ConfigureAwait(false);
                completion.TrySetResult(true);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                completion.TrySetException(error);
            }

            finally
            {
                _context.ResponseEnded();
            }
        }

        private async Task CompleteCoreAsync()
        {
            using var writer = await _writers
                .EnterAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (Interlocked.Exchange(ref _closed, 1) != 0 || _upgraded) return;

            try
            {
                PrepareHeaders(true);
                var body = _context.Features?.Get<IHttpResponseBodyFeature>();

                if (body != null)
                    await body
                        .CompleteAsync()
                        .ConfigureAwait(false);
            }

            catch (IOException) when (_ignoreErrors)
            {
                _context.AbortTransport();
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                _context.AbortTransport();
                throw;
            }

            finally
            {
                _writers.Dispose();
            }
        }

        public void Close() =>
            CompleteAsync()
                .GetAwaiter()
                .GetResult();

        internal void Retire() => _writers.Dispose();

        public void Dispose()
        {
            Close();
            _output.Dispose();
            _writers.Dispose();
        }

        private sealed class Output(KestrelResponse response) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => Volatile.Read(ref response._closed) == 0;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() =>
                FlushAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override Task FlushAsync(CancellationToken cancellationToken) =>
                response.FlushAsync(cancellationToken);

            public override void Write(byte[] buffer, int offset, int count) =>
                WriteAsync(buffer, offset, count, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                ValidateBufferArguments(buffer, offset, count);
                return response.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default
            ) => new(response.WriteAsync(buffer, cancellationToken));

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing) response.Close();

                base.Dispose(disposing);
            }
        }
    }
}
