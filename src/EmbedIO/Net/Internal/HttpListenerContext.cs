using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;
using EmbedIO.WebSockets.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal
{
    // Provides access to the request and response objects used by the HttpListener class.
    internal sealed class HttpListenerContext : IHttpContextImpl, IHttpTunnelContext
    {
        private Dictionary<object, object>? _items;

        private readonly TimeKeeper _ageKeeper = new();

        private readonly Stack<Action<IHttpContext>> _closeCallbacks = new();

        private bool _closed;
        private int _handoff;
        private HttpTunnel? _acceptedTunnel;
        private TaskCompletionSource<bool>? _tunnelHandshake;
        private Task? _tunnelClose;

        internal HttpListenerContext(HttpConnection cnc)
        {
            Connection = cnc;
            HttpListenerRequest = new HttpListenerRequest(this);
            User = Auth.NoUser;
            HttpListenerResponse = new HttpListenerResponse(this);
            Id = UniqueIdGenerator.GetNext();
            LocalEndPoint = Request.LocalEndPoint;
            RemoteEndPoint = Request.RemoteEndPoint;
            Route = RouteMatch.None;
            Session = SessionProxy.None;
        }

        public string Id { get; }

        public CancellationToken CancellationToken { get; set; }

        public long Age => _ageKeeper.ElapsedTime;

        public IPEndPoint LocalEndPoint { get; }

        public IPEndPoint RemoteEndPoint { get; }

        public IHttpRequest Request => HttpListenerRequest;

        public RouteMatch Route { get; set; }

        public string RequestedPath => Route.SubPath ?? string.Empty; // It will never be empty, because modules are matched via base routes - this is just to silence a warning.

        public IHttpResponse Response => HttpListenerResponse;

        public IPrincipal User { get; set; }

        public ISessionProxy Session { get; set; }

        public bool SupportCompressedRequests { get; set; }

        public IDictionary<object, object> Items
        {
            get
            {
                var items = Volatile.Read(ref _items);
                if (items != null) return items;
                var created = new Dictionary<object, object>();
                return Interlocked.CompareExchange(ref _items, created, null) ?? created;
            }
        }

        public bool IsHandled { get; private set; }

        public MimeTypeProviderStack MimeTypeProviders { get; } = new MimeTypeProviderStack();

        internal HttpListenerRequest HttpListenerRequest { get; }

        internal HttpListenerResponse HttpListenerResponse { get; }

        internal HttpListener? Listener { get; set; }

        internal HttpConnection Connection { get; }

        public void SetHandled() => IsHandled = true;

        public void OnClose(Action<IHttpContext> callback)
        {
            if (_closed)
            {
                throw new InvalidOperationException("HTTP context has already been closed.");
            }

            _closeCallbacks.Push(Validate.NotNull(nameof(callback), callback));
        }

        internal bool HasAcceptedTunnel => Volatile.Read(ref _tunnelHandshake) != null;
        public void Close()
        {
            if (HasAcceptedTunnel)
            {
                var closing = CloseTunnelAsync();
                if (!closing.IsCompleted)
                    _ = closing.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                else if (closing.IsFaulted) _ = closing.Exception;
                return;
            }
            FinishClose();
        }
        private void FinishClose()
        {
            _closed = true;

            // Run completion callbacks even when the transport cannot close cleanly.
            try
            {
                if (CancellationToken.IsCancellationRequested) HttpListenerResponse.Abort();
                else Response.Close();
            }
            finally
            {
                foreach (var callback in _closeCallbacks)
                {
                    try
                    {
                        callback(this);
                    }
                    catch (Exception e) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(e))
                    {
                        e.Log("HTTP context", $"[{Id}] Exception thrown by a HTTP context close callback.");
                    }
                }
            }
        }

        internal Task CloseTunnelAsync()
        {
            TaskCompletionSource<bool> start;
            Task close;
            lock (_closeCallbacks)
            {
                if (_tunnelClose != null) return _tunnelClose;
                _closed = true;
                start = new TaskCompletionSource<bool>();
                close = CloseTunnelCoreAsync(start.Task);
                _tunnelClose = close;
            }
            start.TrySetResult(true);
            return close;
        }
        private async Task CloseTunnelCoreAsync(Task start)
        {
            await start.ConfigureAwait(false);
            try
            {
                var handshake = _tunnelHandshake ?? throw new InvalidOperationException("No tunnel handshake.");
                await handshake.Task.ConfigureAwait(false);
                if (_acceptedTunnel != null) await _acceptedTunnel.DisposeOwnedStreamAsync(CancellationToken).ConfigureAwait(false);
            }
            finally { FinishClose(); }
        }
        public IReadOnlyList<string> RequestedTunnelProtocols
            => Request.ProtocolVersion >= HttpVersion.Version11
                && Request.Headers.Contains(HttpHeaderNames.Connection, "Upgrade", StringComparison.OrdinalIgnoreCase)
                ? HttpUpgradeProtocols.Parse(Request.Headers[HttpHeaderNames.Upgrade]) : Array.Empty<string>();

        public async Task<HttpTunnel> AcceptTunnelAsync(string? protocol, bool useCapsules = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligible = protocol == null ? Request.HttpMethod == "CONNECT"
                : RequestedTunnelProtocols.Any(offered => HttpUpgradeProtocols.Matches(offered, protocol));
            if (!eligible || Request.ProtocolVersion < HttpVersion.Version11)
                throw new InvalidOperationException("Select an offered HTTP/1.1 Upgrade protocol or ordinary CONNECT.");
            if (string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use AcceptWebSocketAsync for WebSocket negotiation.");
            if (useCapsules && protocol == null) throw new InvalidOperationException("Capsules require a negotiated extension protocol.");
            if (Request.HasEntityBody && Request.InputStream is RequestStream body && !body.IsBodyConsumed)
                throw new InvalidOperationException("Consume the HTTP request body before handing off its connection.");
            if (Interlocked.CompareExchange(ref _handoff, 1, 0) != 0)
                throw new InvalidOperationException("A protocol handoff was already accepted.");
            TaskCompletionSource<bool> handshake;
            lock (_closeCallbacks)
            {
                if (_closed) throw new ObjectDisposedException(nameof(HttpListenerContext));
                handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tunnelHandshake = handshake;
            }
            Http1TunnelStream? stream = null;
            try
            {
                Response.StatusCode = protocol == null ? 200 : 101;
                HttpListenerResponse.BeginTunnel(protocol, useCapsules);
                Connection.BeginTunnel();
                stream = new Http1TunnelStream(Connection, Connection.TakeUpgradeStream());
                using var headers = HttpListenerResponse.SendHeaders(false, 0);
                await Connection.Stream.WriteAsync(headers.GetBuffer(), (int)headers.Position,
                    (int)(headers.Length - headers.Position), cancellationToken).ConfigureAwait(false);
                var tunnel = new HttpTunnel(stream, stream.CompleteOutputAsync, protocol, useCapsules, _ =>
                {
                    Connection.ForceClose();
                    return Task.CompletedTask;
                });
                lock (_closeCallbacks)
                {
                    if (_closed) throw new ObjectDisposedException(nameof(HttpListenerContext));
                    _acceptedTunnel = tunnel;
                }
                handshake.TrySetResult(true);
                return tunnel;
            }
            catch (Exception error)
            {
                try { if (stream != null) stream.Dispose(); else Connection.ForceClose(); }
                finally { handshake.TrySetException(error); _ = handshake.Task.Exception; }
                throw;
            }
        }

        public async Task<IWebSocketContext> AcceptWebSocketAsync(
            IEnumerable<string> requestedProtocols,
            string acceptedProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _handoff, 1, 0) != 0) throw new InvalidOperationException("A protocol handoff was already accepted.");
            var webSocket = await WebSocket.AcceptAsync(this, acceptedProtocol).ConfigureAwait(false);
            return new WebSocketContext(this, WebSocket.SupportedVersion, requestedProtocols, acceptedProtocol, webSocket, cancellationToken);
        }

        public string? GetMimeType(string extension)
            => MimeTypeProviders.GetMimeType(extension);

        public bool TryDetermineCompression(string mimeType, out bool preferCompression)
            => MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);
    }
}
