using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;

namespace EmbedIO.Caching
{
    // Only explicitly opted-in handlers receive a delegating context. Misses still write
    // directly to the transport; an oversize or flushed response simply stops recording.
    internal class OutputCacheCapture : IHttpContextImpl, IDisposable
    {
        internal readonly IHttpContext Inner;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The handler owns transport completion; capture disposal releases only the recording buffer via ReleaseCapture.")]
        private readonly CaptureStream _stream;
        private string? _sentHeaders;
        private string? _sentContentType;
        private Encoding? _sentEncoding;
        internal bool Disabled;
        internal bool CompletingText;
        private static readonly HashSet<string> StoredHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Type", "Content-Encoding", "Content-Language", "Content-Disposition", "ETag", "Last-Modified",
            "Vary", "Server"
        };
        private readonly Dictionary<string, string?> _baseline = new(StringComparer.OrdinalIgnoreCase);

        protected OutputCacheCapture(IHttpContext context, int limit)
        {
            Inner = context;
            _stream = new CaptureStream(this, context.Response.OutputStream, limit);
            Response = context.Response is IHttpResponseSections sections
                ? new SectionResponse(this, sections)
                : new CaptureResponse(this);

            foreach (var name in context.Response.Headers.AllKeys)
                if (name != null) _baseline[name] = context.Response.Headers[name];
        }

        internal static OutputCacheCapture Create(IHttpContext context, int limit) =>
            context is IHttpRequestAbortFeature abort
                ? new AbortCapture(context, limit, abort)
                : new OutputCacheCapture(context, limit);

        private sealed class AbortCapture : OutputCacheCapture, IHttpRequestAbortFeature
        {
            private readonly IHttpRequestAbortFeature _abort;

            internal AbortCapture(IHttpContext context, int limit, IHttpRequestAbortFeature abort)
                : base(context, limit) => _abort = abort;

            public CancellationToken GetRequestAbortedToken(bool cancelOnHttp1InputEnd = false) =>
                _abort.GetRequestAbortedToken(cancelOnHttp1InputEnd);
        }

        private void Snapshot()
        {
            if (_sentHeaders != null) return;

            _sentHeaders = Inner.Response.Headers.ToString();
            _sentContentType = Inner.Response.ContentType;
            _sentEncoding = Inner.Response.ContentEncoding;
        }

        internal OutputCache.Entry? Complete(string key, OutputCachePolicy policy, long started)
        {
            var response = Inner.Response;

            if (
                Disabled
                || !OutputCache.Eligible(Inner)
                || _sentHeaders != null
                    && (_sentHeaders != response.Headers.ToString()
                        || _sentContentType != response.ContentType
                        || _sentEncoding != response.ContentEncoding)
            ) return null;

            // Unknown fields added by the action could carry request-specific values. Never
            // silently drop them on a hit. Upstream/hook fields are recomputed on every request.
            foreach (var name in response.Headers.AllKeys)
            {
                if (
                    name == null
                    || StoredHeaders.Contains(name)
                    || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Date", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
                ) continue;

                if (!_baseline.TryGetValue(name, out var before) || before != response.Headers[name]) return null;
            }

            foreach (var name in _baseline.Keys)
                if (!StoredHeaders.Contains(name) && _baseline[name] != response.Headers[name]) return null;

            var vary = response.Headers["Vary"];

            if (
                vary != null
                && vary
                    .Split(',')
                    .Any(name => !policy.Headers.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            ) return null;

            if (response.Headers["Content-Range"] != null || response.Headers["Trailer"] != null) return null;

            var headers = response.Headers.AllKeys
                .Where(
                    name => name != null
                        && StoredHeaders.Contains(name)
                        && !name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                )
                .Select(name => new KeyValuePair<string, string[]>(name, response.Headers.GetValues(name) ?? []))
                .ToArray();
            var body = _stream.Bytes();

            // A mismatched fixed-length body is never a reusable completed response. HEAD
            // outputs are keyed separately and the transport still suppresses their bytes.
            if (
                !response.SendChunked
                && Inner.Request.HttpVerb != HttpVerbs.Head
                && response.ContentLength64 > 0
                && response.ContentLength64 != body.Length
            ) return null;

            return new OutputCache.Entry(
                key,
                policy,
                started,
                body,
                headers,
                response.ContentType,
                response.ContentEncoding,
                Inner.Request.HttpVerb == HttpVerbs.Head ? response.ContentLength64 : body.LongLength,
                response.StatusDescription
            );
        }

        public void Dispose() => _stream.ReleaseCapture();

        public string Id => Inner.Id;

        public CancellationToken CancellationToken
        {
            get => Inner.CancellationToken;
            set => Inner.GetImplementation().CancellationToken = value;
        }

        public IPEndPoint LocalEndPoint => Inner.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => Inner.RemoteEndPoint;
        public IHttpRequest Request => Inner.Request;

        public RouteMatch Route
        {
            get => Inner.Route;
            set => Inner.GetImplementation().Route = value;
        }

        public string RequestedPath => Inner.RequestedPath;
        public IHttpResponse Response { get; }

        public IPrincipal User
        {
            get => Inner.User;
            set => Inner.GetImplementation().User = value;
        }

        public ISessionProxy Session
        {
            get
            {
                Disabled = true;
                return Inner.Session;
            }
            set
            {
                Disabled = true;
                Inner.GetImplementation().Session = value;
            }
        }

        public bool SupportCompressedRequests
        {
            get => Inner.SupportCompressedRequests;
            set => Inner.GetImplementation().SupportCompressedRequests = value;
        }

        public IDictionary<object, object> Items => Inner.Items;
        public long Age => Inner.Age;
        public bool IsHandled => Inner.IsHandled;
        public MimeTypeProviderStack MimeTypeProviders => Inner.GetImplementation().MimeTypeProviders;

        public void SetHandled() => Inner.SetHandled();

        public void OnClose(Action<IHttpContext> callback)
        {
            Disabled = true;
            Inner.OnClose(callback);
        }

        public void Close()
        {
            Disabled = true;
            Inner
                .GetImplementation()
                .Close();
        }

        public string? GetMimeType(string extension) => Inner.GetMimeType(extension);

        public bool TryDetermineCompression(string mimeType, out bool preferCompression) =>
            Inner.TryDetermineCompression(mimeType, out preferCompression);

        public Task<IWebSocketContext> AcceptWebSocketAsync(
            IEnumerable<string> requestedProtocols,
            string acceptedProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval,
            CancellationToken cancellationToken
        )
        {
            Disabled = true;
            return Inner
                .GetImplementation()
                .AcceptWebSocketAsync(
                    requestedProtocols,
                    acceptedProtocol,
                    receiveBufferSize,
                    keepAliveInterval,
                    cancellationToken
                );
        }

        internal class CaptureResponse : IHttpResponse
        {
            protected readonly OutputCacheCapture Owner;

            internal IHttpResponse Inner => Owner.Inner.Response;

            internal CaptureResponse(OutputCacheCapture owner) => Owner = owner;

            internal void DisableCapture() => Owner.Disabled = true;

            public WebHeaderCollection Headers => Inner.Headers;

            public int StatusCode
            {
                get => Inner.StatusCode;
                set => Inner.StatusCode = value;
            }

            public long ContentLength64
            {
                get => Inner.ContentLength64;
                set => Inner.ContentLength64 = value;
            }

            [System.Diagnostics.CodeAnalysis.MaybeNull]
            public string ContentType
            {
                get => Inner.ContentType;
                set => Inner.ContentType = value;
            }

            public Encoding? ContentEncoding
            {
                get => Inner.ContentEncoding;
                set => Inner.ContentEncoding = value;
            }

            public bool KeepAlive
            {
                get => Inner.KeepAlive;
                set => Inner.KeepAlive = value;
            }

            public bool SendChunked
            {
                get => Inner.SendChunked;
                set => Inner.SendChunked = value;
            }

            [System.Diagnostics.CodeAnalysis.MaybeNull]
            public string StatusDescription
            {
                get => Inner.StatusDescription;
                set => Inner.StatusDescription = value;
            }

            public ICookieCollection Cookies
            {
                get
                {
                    Owner.Disabled = true;
                    return Inner.Cookies;
                }
            }

            public Version ProtocolVersion => Inner.ProtocolVersion;
            public Stream OutputStream => Owner._stream;

            public void SetCookie(Cookie cookie)
            {
                Owner.Disabled = true;
                Inner.SetCookie(cookie);
            }

            public void Close()
            {
                Owner.Disabled = true;
                Inner.Close();
            }
        }

        private sealed class SectionResponse : CaptureResponse, IHttpResponseSections
        {
            private readonly IHttpResponseSections _sections;

            internal SectionResponse(OutputCacheCapture owner, IHttpResponseSections sections) : base(owner) =>
                _sections = sections;

            public Task SendInformationalAsync(
                int statusCode,
                WebHeaderCollection headers,
                CancellationToken cancellationToken = default
            )
            {
                Owner.Disabled = true;
                return _sections.SendInformationalAsync(statusCode, headers, cancellationToken);
            }

            public void DeclareTrailers(params string[] fieldNames)
            {
                Owner.Disabled = true;
                _sections.DeclareTrailers(fieldNames);
            }

            public void SetTrailers(WebHeaderCollection trailers)
            {
                Owner.Disabled = true;
                _sections.SetTrailers(trailers);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1844:Override memory-based WriteAsync",
            Justification = "The shared implementation targets netstandard2.0, which has no memory-based Stream async overloads."
        )]
        private sealed class CaptureStream : Stream, IEndingBodyOutput
        {
            [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Borrowed parent reference; disposing it here would discard the buffer before cache publication.")]
            private readonly OutputCacheCapture _owner;
            private readonly Stream _inner;
            private readonly int _limit;
            private readonly MemoryStream _body = new();

            internal CaptureStream(OutputCacheCapture owner, Stream inner, int limit)
            {
                _owner = owner;
                _inner = inner;
                _limit = limit;
            }

            internal byte[] Bytes() => _body.ToArray();

            internal void ReleaseCapture() => _body.Dispose();

            private void Record(byte[] buffer, int offset, int count)
            {
                if (_owner.Disabled) return;

                if (count > _limit - _body.Length)
                {
                    _owner.Disabled = true;
                    _body.SetLength(0);
                    return;
                }

                var needed = (int)_body.Length + count;

                if (needed > _body.Capacity)
                    _body.Capacity = (int)Math.Min(_limit, Math.Max((long)needed, Math.Max(256L, 2L * _body.Capacity)));

                _body.Write(buffer, offset, count);
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Flush()
            {
                if (!_owner.CompletingText) _owner.Disabled = true;

                _inner.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                if (!_owner.CompletingText) _owner.Disabled = true;

                return _inner.FlushAsync(cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
                _owner.Snapshot();
                Record(buffer, offset, count);
            }

            public override async Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            )
            {
                await _inner
                    .WriteAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false);
                _owner.Snapshot();
                Record(buffer, offset, count);
            }

            public void WriteEnding(byte[] buffer, int offset, int count)
            {
                if (_inner is IEndingBodyOutput ending) ending.WriteEnding(buffer, offset, count);

                else
                {
                    _inner.Write(buffer, offset, count);
                    _inner.Dispose();
                }

                _owner.Snapshot();
                Record(buffer, offset, count);
            }

            public async Task WriteEndingAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            )
            {
                if (_inner is IEndingBodyOutput ending)
                    await ending
                        .WriteEndingAsync(buffer, offset, count, cancellationToken)
                        .ConfigureAwait(false);

                else
                {
                    await _inner
                        .WriteAsync(buffer, offset, count, cancellationToken)
                        .ConfigureAwait(false);
                    _inner.Dispose();
                }

                _owner.Snapshot();
                Record(buffer, offset, count);
            }
#if NET10_0_OR_GREATER
            // Await the transport's completion and any final write failure, as an
            // unwrapped response stream does. The recording buffer stays alive until Complete.
            public override ValueTask DisposeAsync() => _inner.DisposeAsync();
#endif
            protected override void Dispose(bool disposing)
            {
                if (disposing) _inner.Dispose();

                base.Dispose(disposing);
            }
        }
    }
}
