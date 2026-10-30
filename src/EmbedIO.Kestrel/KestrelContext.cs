using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;
using Microsoft.AspNetCore.Http.Features;
using OwnedWebSocket = EmbedIO.WebSockets.Internal.WebSocket;

namespace EmbedIO.Kestrel
{
    internal sealed class KestrelContext :
        IHttpContextImpl,
        IHttpBackendContext,
        IHttpRequestAbortFeature,
        IRequestAbortState,
        ITelemetryContext,
        ILazyItems,
        IDisposable
    {
        private readonly object _sync = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TaskCompletionSource<bool> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private Dictionary<object, object>? _items;
        private Stack<Action<IHttpContext>>? _callbacks;
        private RequestAbortToken? _abort;
        private CancellationTokenRegistration _peerRegistration;
        private Task? _closing;
        private Task? _upgrading;
        private OwnedWebSocket? _webSocket;
        private bool _retired;
        private bool _upgraded;

        internal KestrelContext(IFeatureCollection features, bool ignoreWriteExceptions, TimeSpan keepAliveTimeout)
        {
            Features = features;
            PeerAborted = features.Get<IHttpRequestLifetimeFeature>()?.RequestAborted ?? CancellationToken.None;
            RequestImpl = new KestrelRequest(this, features);
            ResponseImpl = new KestrelResponse(this, ignoreWriteExceptions, keepAliveTimeout);
        }

        internal IFeatureCollection? Features { get; private set; }
        internal KestrelRequest RequestImpl { get; }
        internal KestrelResponse ResponseImpl { get; }
        internal CancellationToken PeerAborted { get; }
        internal Task Completion => _completion.Task;

        internal void ResponseEnded() => _completion.TrySetResult(true);

        internal bool IsUpgradable => Features?.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true;
        internal bool IsExtendedWebSocket =>
            Features?.Get<IHttpExtendedConnectFeature>() is { IsExtendedConnect: true, Protocol: "websocket" };
        public string Id { get; } = UniqueIdGenerator.GetNext();
        public CancellationToken CancellationToken { get; set; }
        public long Age => (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        public IPEndPoint LocalEndPoint => Request.LocalEndPoint;
        public IPEndPoint RemoteEndPoint => Request.RemoteEndPoint;
        public IHttpRequest Request => RequestImpl;
        public IHttpResponse Response => ResponseImpl;
        public RouteMatch Route { get; set; } = RouteMatch.None;
        public string RequestedPath => Route.SubPath ?? string.Empty;
        public IPrincipal User { get; set; } = Auth.NoUser;
        public ISessionProxy Session { get; set; } = SessionProxy.None;
        public bool SupportCompressedRequests { get; set; }
        public RequestTelemetry? Telemetry { get; set; }
        public IDictionary<object, object> Items => LazyInitializer.EnsureInitialized(ref _items);
        bool ILazyItems.HasItems => _items != null;
        public bool IsHandled { get; private set; }
        public MimeTypeProviderStack MimeTypeProviders { get; } = new();
        public bool ResponseHeadersSent => ResponseImpl.HeadersSent;
        bool IRequestAbortState.IsRequestAborted => PeerAborted.IsCancellationRequested;

        public void SetHandled() => IsHandled = true;

        public string? GetMimeType(string extension) => MimeTypeProviders.GetMimeType(extension);

        public bool TryDetermineCompression(string mimeType, out bool preferCompression) =>
            MimeTypeProviders.TryDetermineCompression(mimeType, out preferCompression);

        public CancellationToken GetRequestAbortedToken(bool cancelOnHttp1InputEnd = false)
        {
            if (cancelOnHttp1InputEnd && Request.ProtocolVersion < HttpVersion.Version20)
                throw new NotSupportedException("Kestrel does not expose HTTP/1 input half-close notifications.");

            lock (_sync)
            {
                if (_abort != null) return _abort.Token;

                if (_closing != null || _retired || _upgraded)
                    throw new InvalidOperationException("The request completed or handed off its protocol.");

                _abort = new RequestAbortToken(
                    error => error.Log("Kestrel", "A request-abort callback failed."),
                    CancellationToken
                );
                _peerRegistration = PeerAborted.UnsafeRegister(
                    static state => (state as RequestAbortToken)?.Abort(),
                    _abort
                );
                return _abort.Token;
            }
        }

        public void OnClose(Action<IHttpContext> callback)
        {
            ArgumentNullException.ThrowIfNull(callback);

            lock (_sync)
            {
                if (_closing != null || _retired)
                    throw new InvalidOperationException("The context has already closed.");

                (_callbacks ??= new()).Push(callback);
            }
        }

        public void Close()
        {
            lock (_sync) if (_closing != null) return;

            var closing = CloseAsync();

            if (!_upgraded)
                closing
                    .GetAwaiter()
                    .GetResult();
        }

        public void Dispose()
        {
            Close();
            _webSocket?.Dispose();
        }

        public Task CloseAsync()
        {
            TaskCompletionSource<bool> completion;

            lock (_sync)
            {
                if (_closing != null) return _closing;

                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _closing = completion.Task;
            }

            _ = FinishCloseAsync(completion);
            return completion.Task;
        }

        private async Task FinishCloseAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                await CloseCoreAsync().ConfigureAwait(false);
                completion.TrySetResult(true);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                completion.TrySetException(error);
            }
        }

        private async Task CloseCoreAsync()
        {
            try
            {
                Task? upgrading;

                lock (_sync) upgrading = _upgrading;

                if (upgrading != null) await upgrading.ConfigureAwait(false);

                if (_webSocket != null) await _webSocket.Released.ConfigureAwait(false);

                else
                    await ResponseImpl
                        .CompleteAsync()
                        .ConfigureAwait(false);
            }

            finally
            {
                _peerRegistration.Dispose();
                _abort?.Dispose();

                if (_callbacks != null)
                    while (_callbacks.Count != 0)
                        try
                        {
                            _callbacks.Pop()(this);
                        }

                        catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                        {
                            try
                            {
                                error.Log("Kestrel", "A context-close callback failed.");
                            }

                            catch (Exception diagnostic) when (ExceptionPolicy.IsRecoverable(diagnostic)) { }
                        }

                _completion.TrySetResult(true);
            }
        }

        internal void AbortTransport()
        {
            lock (_sync)
                if (!_retired)
                    Features
                        ?.Get<IHttpRequestLifetimeFeature>()
                        ?.Abort();
        }

        public Task AbortAsync(Exception cause)
        {
            AbortTransport();
            return Task.CompletedTask;
        }

        internal async Task RetireAsync()
        {
            try
            {
                await RequestImpl
                    .RetireAsync()
                    .ConfigureAwait(false);
            }

            finally
            {
                _webSocket?.Dispose();
                ResponseImpl.Retire();
                Retire();
            }
        }

        internal void Retire()
        {
            lock (_sync)
            {
                _retired = true;
                Features = null;
                _completion.TrySetResult(true);
            }
        }

        public async Task<IWebSocketContext> AcceptWebSocketAsync(
            IEnumerable<string> requestedProtocols,
            string acceptedProtocol,
            int receiveBufferSize,
            TimeSpan keepAliveInterval,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Request.IsWebSocketRequest || Request.Headers["Sec-WebSocket-Version"] != "13")
                throw HttpException.BadRequest("A valid WebSocket opening handshake is required.");

            var extended = IsExtendedWebSocket;

            if (Request.HttpMethod != (extended ? "CONNECT" : "GET"))
                throw HttpException.BadRequest("Invalid WebSocket opening method.");

            var key = Request.Headers["Sec-WebSocket-Key"] ?? string.Empty;

            if (!extended)
            {
                byte[] decoded;

                try
                {
                    decoded = Convert.FromBase64String(key);
                }

                catch (FormatException)
                {
                    throw HttpException.BadRequest("Invalid WebSocket key.");
                }

                if (decoded.Length != 16) throw HttpException.BadRequest("Invalid WebSocket key.");
            }

            if (
                acceptedProtocol.Length != 0
                && !System.Linq.Enumerable.Contains(requestedProtocols, acceptedProtocol, StringComparer.Ordinal)
            )
                throw new ArgumentException("The accepted protocol was not offered.", nameof(acceptedProtocol));

            TaskCompletionSource<bool> upgrading;

            lock (_sync)
            {
                if (_closing != null || _upgraded || _retired)
                    throw new InvalidOperationException("The request is no longer upgradeable.");

                _upgraded = true;
                upgrading = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _upgrading = upgrading.Task;
            }

            try
            {
                ResponseImpl.PrepareWebSocket(key, acceptedProtocol);
                var stream = extended
                    ? await (Features?.Get<IHttpExtendedConnectFeature>()
                        ?? throw new InvalidOperationException("Missing extended CONNECT feature."))
                        .AcceptAsync()
                        .ConfigureAwait(false)
                    : await (Features?.Get<IHttpUpgradeFeature>()
                        ?? throw new InvalidOperationException("Missing upgrade feature."))
                        .UpgradeAsync()
                        .ConfigureAwait(false);
                _webSocket = OwnedWebSocket.FromStream(stream, stream.Dispose);
                bool closing;

                lock (_sync) closing = _closing != null;

                if (closing) _webSocket.Dispose();

                return new WebSockets.Internal.WebSocketContext(
                    this,
                    "13",
                    requestedProtocols,
                    acceptedProtocol,
                    _webSocket,
                    cancellationToken
                );
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                _webSocket?.Dispose();
                AbortTransport();
                throw;
            }

            finally
            {
                upgrading.TrySetResult(true);
            }
        }
    }
}
