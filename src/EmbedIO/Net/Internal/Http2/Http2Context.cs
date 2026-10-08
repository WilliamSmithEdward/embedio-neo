using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2Context : IHttpContextImpl, IDisposable
    {
        private readonly object _sync = new();
        private readonly Lazy<IDictionary<object, object>> _items = new(() => new Dictionary<object, object>(), true);
        private readonly Stack<Action<IHttpContext>> _callbacks = new();
        private readonly TimeKeeper _age = new();
        private readonly Http2Exchange _exchange;
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _linked;
        private CancellationToken _cancellation;
        private bool _closed;
        internal Http2Context(Http2Exchange exchange, IPEndPoint local, IPEndPoint remote, bool secure)
        {
            _exchange = exchange; _cancellation = exchange.CancellationToken;
            Request = new Http2Request(exchange, local, remote, secure);
            Response = new Http2Response(exchange);
        }
        public string Id { get; } = UniqueIdGenerator.GetNext();
        public CancellationToken CancellationToken
        {
            get => _cancellation;
            set
            {
                lock (_sync)
                {
                    if (_closed) throw new ObjectDisposedException(nameof(Http2Context));
                    _linked?.Dispose();
                    _linked = CancellationTokenSource.CreateLinkedTokenSource(value, _exchange.CancellationToken);
                    _cancellation = _linked.Token;
                }
            }
        }
        public long Age => _age.ElapsedTime;
        public IPEndPoint LocalEndPoint => Request.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => Request.RemoteEndPoint;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public RouteMatch Route { get; set; } = RouteMatch.None;
        public string RequestedPath => Route.SubPath ?? string.Empty;
        public IPrincipal User { get; set; } = Auth.NoUser;
        public ISessionProxy Session { get; set; } = SessionProxy.None;
        public bool SupportCompressedRequests { get; set; }
        public IDictionary<object, object> Items => _items.Value;
        public bool IsHandled { get; private set; }
        public MimeTypeProviderStack MimeTypeProviders { get; } = new();
        internal Task Completion => _completion.Task;
        public void SetHandled() => IsHandled = true;
        public string GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);
        public bool TryDetermineCompression(string mimeType, out bool preferCompression) => MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);
        public void OnClose(Action<IHttpContext> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (_sync)
            {
                if (_closed) throw new InvalidOperationException("HTTP context has already been closed.");
                _callbacks.Push(callback);
            }
        }
        public void Dispose() => Close();
        public void Close()
        {
            lock (_sync) { if (_closed) return; _closed = true; }
            Exception? failure = null;
            try { Response.Close(); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                foreach (var callback in _callbacks)
                    try { callback(this); } catch (Exception error) { error.Log("HTTP context", $"[{Id}] Exception thrown by a HTTP context close callback."); }
                _linked?.Dispose();
                if (failure == null) _completion.TrySetResult(true); else _completion.TrySetException(failure);
            }
        }
        public Task<IWebSocketContext> AcceptWebSocketAsync(IEnumerable<string> requestedProtocols, string acceptedProtocol, int receiveBufferSize, TimeSpan keepAliveInterval, CancellationToken cancellationToken)
            => throw new NotSupportedException("HTTP/2 extended CONNECT is not advertised until WebSocket stream integration is available.");
    }
}
