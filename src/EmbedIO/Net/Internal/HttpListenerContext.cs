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
using EmbedIO.WebSockets.Internal;

namespace EmbedIO.Net.Internal
{
    // Independently authored request context. One cold transition claims
    // completion and detaches callbacks; transport and callback work is outside it.
    internal sealed class HttpListenerContext : IHttpContextImpl, IHttpTunnelContext
    {
        private readonly object _lifetime = new();
        private readonly TimeKeeper _age = new();
        private Dictionary<object, object>? _items;
        private Stack<Action<IHttpContext>>? _callbacks;
        private bool _closing;
        private bool _protocolClaimed;
        private TaskCompletionSource<bool>? _handshake;
        private HttpTunnel? _tunnel;
        private Task? _tunnelCompletion;
        private int _closeObserved;

        internal HttpListenerContext(HttpConnection connection)
        {
            Connection = connection;
            Id = UniqueIdGenerator.GetNext();
            HttpListenerRequest = new HttpListenerRequest(this);
            HttpListenerResponse = new HttpListenerResponse(this);
            LocalEndPoint = connection.LocalEndPoint;
            RemoteEndPoint = connection.RemoteEndPoint;
        }
        internal HttpConnection Connection { get; }
        internal HttpListener? Listener { get; set; }
        internal HttpListenerRequest HttpListenerRequest { get; }
        internal HttpListenerResponse HttpListenerResponse { get; }
        public string Id { get; }
        public CancellationToken CancellationToken { get; set; }
        public long Age => _age.ElapsedTime;
        public IPEndPoint LocalEndPoint { get; }
        public IPEndPoint RemoteEndPoint { get; }
        public IHttpRequest Request => HttpListenerRequest;
        public IHttpResponse Response => HttpListenerResponse;
        public RouteMatch Route { get; set; } = RouteMatch.None;
        public string RequestedPath => Route.SubPath ?? string.Empty;
        public IPrincipal User { get; set; } = Auth.NoUser;
        public ISessionProxy Session { get; set; } = SessionProxy.None;
        public bool SupportCompressedRequests { get; set; }
        public bool IsHandled { get; private set; }
        public MimeTypeProviderStack MimeTypeProviders { get; } = new();
        public IDictionary<object, object> Items
        {
            get
            {
                var current = Volatile.Read(ref _items);
                if (current != null) return current;
                var created = new Dictionary<object, object>();
                return Interlocked.CompareExchange(ref _items, created, null) ?? created;
            }
        }
        public void SetHandled() => IsHandled = true;
        public void OnClose(Action<IHttpContext> callback)
        {
            lock (_lifetime)
            {
                if (_closing) throw new InvalidOperationException("HTTP context has already been closed.");
                (_callbacks ??= new Stack<Action<IHttpContext>>()).Push(Validate.NotNull(nameof(callback), callback));
            }
        }
        internal bool HasAcceptedTunnel => Volatile.Read(ref _handshake) != null;
        public void Close()
        {
            Stack<Action<IHttpContext>>? callbacks;
            lock (_lifetime)
            {
                if (_handshake == null)
                {
                    if (_closing) return;
                    _closing = true;
                    callbacks = DetachCallbacks();
                }
                else callbacks = null;
            }
            if (!HasAcceptedTunnel) { FinishResponse(callbacks); return; }
            var closing = CloseTunnelAsync();
            if (Interlocked.Exchange(ref _closeObserved, 1) == 0)
                _ = closing.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private Stack<Action<IHttpContext>>? DetachCallbacks()
        {
            var callbacks = _callbacks;
            _callbacks = null;
            return callbacks;
        }
        private void FinishResponse(Stack<Action<IHttpContext>>? callbacks)
        {
            try
            {
                if (CancellationToken.IsCancellationRequested) HttpListenerResponse.Abort();
                else HttpListenerResponse.Close();
            }
            finally
            {
                if (callbacks != null)
                    foreach (var callback in callbacks)
                        try { callback(this); }
                        catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                        {
                            error.Log("HTTP context", $"[{Id}] Exception thrown by a HTTP context close callback.");
                        }
            }
        }
        internal Task CloseTunnelAsync()
        {
            TaskCompletionSource<bool> start;
            Task completion;
            lock (_lifetime)
            {
                if (_tunnelCompletion != null) return _tunnelCompletion;
                _closing = true;
                start = new TaskCompletionSource<bool>();
                completion = FinishTunnelAsync(start.Task, DetachCallbacks());
                _tunnelCompletion = completion;
            }
            // The async worker starts outside the state gate, including when the
            // handshake and owned-stream cleanup have already completed.
            start.TrySetResult(true);
            return completion;
        }
        private async Task FinishTunnelAsync(Task start, Stack<Action<IHttpContext>>? callbacks)
        {
            await start.ConfigureAwait(false);
            try
            {
                var handshake = _handshake ?? throw new InvalidOperationException("No tunnel handshake.");
                await handshake.Task.ConfigureAwait(false);
                if (_tunnel != null) await _tunnel.DisposeOwnedStreamAsync(CancellationToken).ConfigureAwait(false);
            }
            finally { FinishResponse(callbacks); }
        }
        public IReadOnlyList<string> RequestedTunnelProtocols => Request.ProtocolVersion >= HttpVersion.Version11
            && Request.Headers.Contains(HttpHeaderNames.Connection, "Upgrade", StringComparison.OrdinalIgnoreCase)
                ? HttpUpgradeProtocols.Parse(Request.Headers[HttpHeaderNames.Upgrade]) : Array.Empty<string>();
        private void ClaimProtocol()
        {
            lock (_lifetime)
            {
                if (_protocolClaimed) throw new InvalidOperationException("A protocol handoff was already accepted.");
                _protocolClaimed = true;
                if (_closing) throw new ObjectDisposedException(nameof(HttpListenerContext));
            }
        }
        public async Task<HttpTunnel> AcceptTunnelAsync(string? protocol, bool useCapsules = false, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offered = protocol == null && Request.HttpMethod == "CONNECT";
            if (protocol != null)
                foreach (var candidate in RequestedTunnelProtocols)
                    if (HttpUpgradeProtocols.Matches(candidate, protocol)) { offered = true; break; }
            if (!offered || Request.ProtocolVersion < HttpVersion.Version11)
                throw new InvalidOperationException("Select an offered HTTP/1.1 Upgrade protocol or ordinary CONNECT.");
            if (string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Use AcceptWebSocketAsync for WebSocket negotiation.");
            if (useCapsules && protocol == null) throw new InvalidOperationException("Capsules require a negotiated extension protocol.");
            if (Request.HasEntityBody && HttpListenerRequest.RawInputStream is RequestStream body && !body.IsBodyConsumed)
                throw new InvalidOperationException("Consume the HTTP request body before handing off its connection.");
            TaskCompletionSource<bool> handshake;
            lock (_lifetime)
            {
                ClaimProtocol();
                handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _handshake = handshake;
            }
            Http1TunnelStream? owned = null;
            try
            {
                HttpListenerResponse.BeginTunnel(protocol, useCapsules);
                Connection.BeginTunnel();
                owned = new Http1TunnelStream(Connection, Connection.TakeUpgradeStream());
                using var head = HttpListenerResponse.SendHeaders(false, 0);
                await Connection.Stream.WriteAsync(head.GetBuffer(), (int)head.Position, (int)(head.Length - head.Position), cancellationToken).ConfigureAwait(false);
                var tunnel = new HttpTunnel(owned, owned.CompleteOutputAsync, protocol, useCapsules, _ =>
                {
                    Connection.ForceClose();
                    return Task.CompletedTask;
                });
                lock (_lifetime)
                {
                    if (_closing) throw new ObjectDisposedException(nameof(HttpListenerContext));
                    _tunnel = tunnel;
                }
                handshake.TrySetResult(true);
                return tunnel;
            }
            catch (Exception error)
            {
                try { if (owned != null) owned.Dispose(); else Connection.ForceClose(); }
                finally { handshake.TrySetException(error); _ = handshake.Task.Exception; }
                throw;
            }
        }
        public async Task<IWebSocketContext> AcceptWebSocketAsync(IEnumerable<string> requestedProtocols, string acceptedProtocol,
            int receiveBufferSize, TimeSpan keepAliveInterval, CancellationToken cancellationToken)
        {
            ClaimProtocol();
            var socket = await WebSocket.AcceptAsync(this, acceptedProtocol).ConfigureAwait(false);
            return new WebSocketContext(this, WebSocket.SupportedVersion, requestedProtocols, acceptedProtocol, socket, cancellationToken);
        }
        public string? GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);
        public bool TryDetermineCompression(string mimeType, out bool preferCompression)
            => MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);
    }
}
