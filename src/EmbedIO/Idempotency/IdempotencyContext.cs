using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;

namespace EmbedIO.Idempotency
{
    // Only explicitly opted-in operations allocate this finite staging owner. Its transport is borrowed.
    internal class IdempotencyContext : IHttpContextImpl, IDisposable
    {
        internal readonly IHttpContext Inner;
        private readonly MemoryStream _input;
        private readonly BoundedStream _output;
        private readonly StagedResponse _response;
        private IdempotencyContext(IHttpContext inner, byte[] body, int limit)
        {
            Inner = inner; _input = new MemoryStream(body, false); _output = new BoundedStream(limit);
            Request = new StagedRequest(inner.Request, _input); _response = new StagedResponse(inner.Response, _output); Response = _response;
        }
        internal static IdempotencyContext Create(IHttpContext inner, byte[] body, int limit)
            => inner is IHttpRequestAbortFeature feature ? new AbortContext(inner, body, limit, feature) : new IdempotencyContext(inner, body, limit);
        private sealed class AbortContext : IdempotencyContext, IHttpRequestAbortFeature
        {
            private readonly IHttpRequestAbortFeature _abort;
            internal AbortContext(IHttpContext inner, byte[] body, int limit, IHttpRequestAbortFeature abort) : base(inner, body, limit) => _abort = abort;
            public CancellationToken GetRequestAbortedToken(bool cancelOnHttp1InputEnd = false) => _abort.GetRequestAbortedToken(cancelOnHttp1InputEnd);
        }
        internal IdempotencyResponse Complete(IdempotencyPolicy policy)
        {
            var body = _output.ToArray();
            if (_response.ContentLength64 > 0 && !_response.SendChunked && _response.ContentLength64 != body.LongLength)
                throw new InvalidOperationException("Staged response length does not match its declared length.");
            if (Response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("Streaming responses cannot be recorded.");
            if (!IdempotencyCoordinator.VaryCovered(Response, policy)) throw new InvalidOperationException("Response Vary requires undeclared request headers.");
            var headers = new List<KeyValuePair<string, string>>();
            foreach (var name in _response.Headers.AllKeys)
            {
                if (name == null) continue;
                if (IdempotencyResponse.Forbidden(name)) throw new InvalidOperationException("Unsafe staged response header: " + name);
                var values = _response.Headers.GetValues(name) ?? Array.Empty<string>();
                if (!name.Equals("Vary", StringComparison.OrdinalIgnoreCase) && values.SequenceEqual(_response.InitialHeaders.GetValues(name) ?? Array.Empty<string>())) continue;
                foreach (var value in values) headers.Add(new KeyValuePair<string, string>(name, value));
            }
            foreach (var name in _response.InitialHeaders.AllKeys)
                if (name != null && _response.Headers.GetValues(name) == null) throw new InvalidOperationException("Staged operations cannot remove request-specific guard headers.");
            return new IdempotencyResponse(Response.StatusCode, body, Response.ContentType, headers, Response.ContentEncoding?.WebName);
        }
        public void Dispose() { _input.Dispose(); _output.Dispose(); }
        public string Id => Inner.Id;
        public CancellationToken CancellationToken { get => Inner.CancellationToken; set => Inner.GetImplementation().CancellationToken = value; }
        public IPEndPoint LocalEndPoint => Inner.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => Inner.RemoteEndPoint;
        public IHttpRequest Request { get; }
        public RouteMatch Route { get => Inner.Route; set => Inner.GetImplementation().Route = value; }
        public string RequestedPath => Inner.RequestedPath;
        public IHttpResponse Response { get; }
        public IPrincipal User { get => Inner.User; set => Inner.GetImplementation().User = value; }
        public ISessionProxy Session { get => throw new NotSupportedException("Session mutation is not a finite idempotent result."); set => throw new NotSupportedException("Sessions are not supported in staged operations."); }
        public bool SupportCompressedRequests { get => Inner.SupportCompressedRequests; set => Inner.GetImplementation().SupportCompressedRequests = value; }
        public IDictionary<object, object> Items => Inner.Items;
        public long Age => Inner.Age;
        public bool IsHandled => Inner.IsHandled;
        public MimeTypeProviderStack MimeTypeProviders => Inner.GetImplementation().MimeTypeProviders;
        public void SetHandled() => Inner.SetHandled();
        public void OnClose(Action<IHttpContext> callback) => throw new NotSupportedException("Use awaited operation cleanup.");
        public void Close() => throw new NotSupportedException("Staged operations cannot close their transport.");
        public string? GetMimeType(string extension) => Inner.GetMimeType(extension);
        public bool TryDetermineCompression(string mimeType, out bool preferCompression) => Inner.TryDetermineCompression(mimeType, out preferCompression);
        public Task<IWebSocketContext> AcceptWebSocketAsync(IEnumerable<string> requestedProtocols, string acceptedProtocol, int receiveBufferSize, TimeSpan keepAliveInterval, CancellationToken cancellationToken)
            => throw new NotSupportedException("Protocol handoffs cannot be recorded.");
        private sealed class StagedRequest : IHttpRequest
        {
            internal readonly IHttpRequest Inner;
            internal readonly Stream Body;
            internal StagedRequest(IHttpRequest inner, Stream body) { Inner = inner; Body = body; }
            public NameValueCollection Headers => Inner.Headers;
            public bool KeepAlive => Inner.KeepAlive;
            public string RawTarget => Inner.RawTarget;
            public NameValueCollection QueryString => Inner.QueryString;
            public string HttpMethod => Inner.HttpMethod;
            public HttpVerbs HttpVerb => Inner.HttpVerb;
            public Uri Url => Inner.Url;
            public bool HasEntityBody => Inner.HasEntityBody;
            public Stream InputStream => Body;
            public Encoding ContentEncoding => Inner.ContentEncoding;
            public IPEndPoint RemoteEndPoint => Inner.RemoteEndPoint;
            public bool IsLocal => Inner.IsLocal;
            public bool IsSecureConnection => Inner.IsSecureConnection;
            public string? UserAgent => Inner.UserAgent;
            public bool IsWebSocketRequest => Inner.IsWebSocketRequest;
            public IPEndPoint LocalEndPoint => Inner.LocalEndPoint;
            public string? ContentType => Inner.ContentType;
            public long ContentLength64 => Inner.ContentLength64;
            public bool IsAuthenticated => Inner.IsAuthenticated;
            public Uri? UrlReferrer => Inner.UrlReferrer;
            public ICookieCollection Cookies => Inner.Cookies;
            public Version ProtocolVersion => Inner.ProtocolVersion;
        }
        private sealed class StagedResponse : IHttpResponse
        {
            private readonly IHttpResponse _inner;
            internal StagedResponse(IHttpResponse inner, Stream output)
            {
                _inner = inner; OutputStream = output; _contentType = inner.ContentType; ContentEncoding = inner.ContentEncoding;
                StatusCode = inner.StatusCode; KeepAlive = inner.KeepAlive;
                foreach (var name in inner.Headers.AllKeys)
                    if (name != null && !IdempotencyResponse.Forbidden(name))
                        foreach (var value in inner.Headers.GetValues(name) ?? Array.Empty<string>()) { Headers.Add(name, value); InitialHeaders.Add(name, value); }
            }
            internal WebHeaderCollection InitialHeaders { get; } = new WebHeaderCollection();
            private string? _contentType;
            public WebHeaderCollection Headers { get; } = new WebHeaderCollection();
            public int StatusCode { get; set; }
            public long ContentLength64 { get; set; }
            [System.Diagnostics.CodeAnalysis.MaybeNull]
            public string ContentType { get => _contentType; set => _contentType = value; }
            public Encoding? ContentEncoding { get; set; }
            public bool KeepAlive { get; set; }
            public bool SendChunked { get; set; }
            [System.Diagnostics.CodeAnalysis.MaybeNull]
            public string StatusDescription { get; set; }
            public ICookieCollection Cookies => throw new NotSupportedException("Cookie changes cannot be recorded.");
            public Version ProtocolVersion => _inner.ProtocolVersion;
            public Stream OutputStream { get; }
            public void SetCookie(Cookie cookie) => throw new NotSupportedException("Cookie changes cannot be recorded.");
            public void Close() => throw new NotSupportedException("Staged operations cannot close the transport.");
        }
        private sealed class BoundedStream : MemoryStream
        {
            private readonly int _limit;
            internal BoundedStream(int limit) : base(limit) => _limit = limit;
            private void Check(long count) { if (count < 0 || Position > _limit - count) throw new InvalidOperationException("Idempotency response body exceeds its limit."); }
            public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
            public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
            public override void SetLength(long value) { if (value < 0 || value > _limit) throw new InvalidOperationException("Idempotency response body exceeds its limit."); base.SetLength(value); }
#if NET10_0_OR_GREATER
            public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return default; }
#endif
        }
    }
}
