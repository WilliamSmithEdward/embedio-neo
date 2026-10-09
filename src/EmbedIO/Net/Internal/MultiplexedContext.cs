using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Authentication;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;
using EmbedIO.WebSockets.Internal;

namespace EmbedIO.Net.Internal
{
    internal sealed class MultiplexedContext : IHttpContextImpl, IDisposable
    {
        private readonly object _sync = new();
        private readonly Lazy<IDictionary<object, object>> _items = new(() => new Dictionary<object, object>(), true);
        private readonly Stack<Action<IHttpContext>> _callbacks = new();
        private readonly TimeKeeper _age = new();
        private readonly IMultiplexedExchange _exchange;
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _linked;
        private CancellationToken _cancellation;
        private CancellationToken _serverCancellation;
        private bool _closed;
        private int _webSocketAccepted;
        internal MultiplexedContext(IMultiplexedExchange exchange, IPEndPoint local, IPEndPoint remote, bool secure)
        {
            _exchange = exchange; _cancellation = exchange.CancellationToken;
            Request = new MultiplexedRequest(exchange, local, remote, secure);
            Response = new MultiplexedResponse(exchange);
        }
        public string Id { get; } = UniqueIdGenerator.GetNext();
        public CancellationToken CancellationToken
        {
            get => _cancellation;
            set
            {
                lock (_sync)
                {
                    // A peer reset can close a dequeued context before WebServer
                    // assigns its shutdown token. Keep that request canceled without
                    // throwing into the server-wide accept loop or allocating a link.
                    if (_closed) { _cancellation = new CancellationToken(true); return; }
                    _linked?.Dispose();
                    _serverCancellation = value;
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
        private static readonly HpackField[] ContinueFields = { new(":status", "100") };
        internal Task SendContinueAsync()
            => Request.HttpMethod != "CONNECT" && Request.HasEntityBody && Request.ContentLength64 != 0
                && HttpExpectations.ContainsContinue(Request.Headers["Expect"])
                ? _exchange.SendHeadersAsync(ContinueFields, false, _exchange.CancellationToken)
                : Task.CompletedTask;

        public void SetHandled() => IsHandled = true;
        public string? GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);
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
            if (BeginClose()) CloseCoreAsync(CancellationToken).GetAwaiter().GetResult();
        }
        internal Task CloseAsync() => BeginClose() ? CloseCoreAsync(CancellationToken) : _completion.Task;
        internal Task AbortAsync() => BeginClose() ? CloseCoreAsync(new CancellationToken(true)) : _completion.Task;
        private bool BeginClose()
        {
            lock (_sync)
            {
                if (_closed) return false;
                _closed = true;
                return true;
            }
        }
        private async Task CloseCoreAsync(CancellationToken token)
        {
            Exception? failure = null;
            try
            {
                PropagateCancellation();
                await ((MultiplexedResponse)Response).CloseAsync(token).ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                PropagateCancellation();
                foreach (var callback in _callbacks)
                    try { callback(this); } catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { error.Log("HTTP context", $"[{Id}] Exception thrown by a HTTP context close callback."); }
                _linked?.Dispose();
                if (failure == null) _completion.TrySetResult(true); else _completion.TrySetException(failure);
            }
        }
        private void PropagateCancellation()
        {
            // Parent callbacks run in reverse registration order. Closing from a later
            // callback must not dispose the link before it conveys cancellation to
            // application code, including tokens captured before this context closed.
            if (_linked == null || (!_serverCancellation.IsCancellationRequested && !_exchange.CancellationToken.IsCancellationRequested)) return;
            try { _linked.Cancel(); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                error.Log("HTTP context", $"[{Id}] Exception thrown by a HTTP context cancellation callback.");
            }
        }
        public async Task<IWebSocketContext> AcceptWebSocketAsync(IEnumerable<string> requestedProtocols, string acceptedProtocol, int receiveBufferSize, TimeSpan keepAliveInterval, CancellationToken cancellationToken)
        {
            if (!Request.IsWebSocketRequest || Request.Headers[HttpHeaderNames.SecWebSocketVersion] != "13")
                throw new InvalidOperationException("A WebSocket tunnel requires extended CONNECT and version 13.");
            if (Interlocked.Exchange(ref _webSocketAccepted, 1) != 0)
                throw new InvalidOperationException("WebSocket already accepted.");
            Response.StatusCode = 200;
            if (!string.IsNullOrEmpty(acceptedProtocol)) Response.Headers[HttpHeaderNames.SecWebSocketProtocol] = acceptedProtocol;
            // RFC 8441 / RFC 9220 replace the key/accept exchange with :protocol. Headers
            // and cookies still flow through the ordinary response serializer.
            Response.Headers.Remove(HttpHeaderNames.SecWebSocketAccept);
            var transport = new Http2DuplexStream(Request.InputStream, Response.OutputStream);
            try
            {
                await Response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                var socket = WebSockets.Internal.WebSocket.FromStream(transport, () =>
                {
                    transport.Dispose();
                    // WebServer still flushes and completes this context after the module returns.
                });
                return new WebSocketContext(this, "13", requestedProtocols, acceptedProtocol,
                    socket, cancellationToken);
            }
            catch { transport.Dispose(); throw; }
        }
    }
}
