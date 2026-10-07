using System;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.WebSockets.Internal
{
    internal sealed class SystemWebSocket : IWebSocket
    {
        private readonly object _lifetimeSync = new object();
        private int _activeOperations;
        private bool _disposed;
        private bool _gatesDisposed;
        private int _closeRequested;
        private readonly SemaphoreSlim _receiveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _closeGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        public SystemWebSocket(System.Net.WebSockets.WebSocket webSocket)
        {
            UnderlyingWebSocket = webSocket;
        }

        ~SystemWebSocket()
        {
            Dispose(false);
        }

        public System.Net.WebSockets.WebSocket UnderlyingWebSocket { get; }

        public WebSocketState State => UnderlyingWebSocket.State;

        internal bool IsCloseRequested => Volatile.Read(ref _closeRequested) != 0;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public Task SendAsync(byte[] buffer, bool isText, CancellationToken cancellationToken = default)
            => SendCoreAsync(new ArraySegment<byte>(buffer), isText, cancellationToken);

        private async Task SendCoreAsync(ArraySegment<byte> buffer, bool isText, CancellationToken cancellationToken)
        {
            BeginOperation();
            var entered = false;
            try
            {
                ThrowIfCloseRequested();
                await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                ThrowIfCloseRequested();
                await UnderlyingWebSocket.SendAsync(buffer,
                    isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                    true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (entered) _sendGate.Release();
                EndOperation();
            }
        }

        /// <inheritdoc />
        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken);

        /// <inheritdoc />
        public Task CloseAsync(CloseStatusCode code, string? comment = null, CancellationToken cancellationToken = default) =>
            CloseAsync(MapCloseStatus(code), comment ?? string.Empty, cancellationToken);

        internal async Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return await UnderlyingWebSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

            BeginOperation();
            try
            {
                await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // A full close may have finished while this receiver waited for its turn.
                    if (State == WebSocketState.Closed)
                        return new System.Net.WebSockets.WebSocketReceiveResult(0, WebSocketMessageType.Close, true);

                    return await UnderlyingWebSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _receiveGate.Release();
                }
            }
            finally
            {
                EndOperation();
            }
        }

        private Task CloseAsync(WebSocketCloseStatus code, string comment, CancellationToken cancellationToken)
        {
            // Preserve native synchronous reason validation, including terminal sockets.
            if (Encoding.UTF8.GetByteCount(comment) > 123)
                throw new ArgumentException("The close description exceeds 123 UTF-8 bytes.", "statusDescription");

            var firstRequest = Interlocked.Exchange(ref _closeRequested, 1) == 0;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Keep synchronous native validation; observe asynchronous close failure separately.
                Task closing;
                try { closing = UnderlyingWebSocket.CloseAsync(code, comment, cancellationToken); }
                catch { if (firstRequest) UnderlyingWebSocket.Abort(); throw; }
                return CompleteUnixCloseAsync(closing, firstRequest);
            }

            return CloseWindowsAsync(code, comment, cancellationToken, firstRequest);
        }

        private void ThrowIfCloseRequested()
        {
            if (IsCloseRequested)
                throw new System.Net.WebSockets.WebSocketException(WebSocketError.InvalidState);
        }

        private async Task CompleteUnixCloseAsync(Task closing, bool firstRequest)
        {
            try { await closing.ConfigureAwait(false); }
            catch { if (firstRequest) UnderlyingWebSocket.Abort(); throw; }
        }

        private async Task CloseWindowsAsync(WebSocketCloseStatus code, string comment, CancellationToken cancellationToken, bool firstRequest)
        {
            if (State == WebSocketState.Closed || State == WebSocketState.Aborted)
                return;

            BeginOperation();
            var entered = false;
            try
            {
                await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                try
                {
                    if (State == WebSocketState.Closed || State == WebSocketState.Aborted)
                        return;

                    // Avoid WebSocketBase.CloseAsync's inverted locks (dotnet/runtime #115559).
                    // Recheck against released .NET 11 before considering removal of this path.
                    if (State != WebSocketState.CloseSent)
                    {
                        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            if (State != WebSocketState.Closed && State != WebSocketState.Aborted && State != WebSocketState.CloseSent)
                                await UnderlyingWebSocket.CloseOutputAsync(code, comment, cancellationToken).ConfigureAwait(false);
                        }
                        finally { _sendGate.Release(); }
                    }

                    // An existing module receive can finish the handshake. Otherwise this
                    // caller takes over, including when invoked inside a module callback.
                    await _receiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (State == WebSocketState.CloseSent)
                        {
                            var buffer = new byte[2048];
                            var result = await UnderlyingWebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                            if (result.MessageType != WebSocketMessageType.Close)
                                throw new System.Net.WebSockets.WebSocketException(WebSocketError.InvalidMessageType);
                        }
                    }
                    finally
                    {
                        _receiveGate.Release();
                    }
                }
                finally
                {
                    _closeGate.Release();
                }
            }
            catch
            {
                // Cancelling a secondary waiter must not abort the close already in progress.
                if (entered || firstRequest) UnderlyingWebSocket.Abort();
                throw;
            }
            finally
            {
                EndOperation();
            }
        }

        private void Dispose(bool disposing)
        {
            if (!disposing)
                return;

            lock (_lifetimeSync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            try
            {
                UnderlyingWebSocket.Dispose();
            }
            finally
            {
                lock (_lifetimeSync)
                    DisposeGatesIfIdle();
            }
        }

        private void BeginOperation()
        {
            lock (_lifetimeSync)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(SystemWebSocket));
                _activeOperations++;
            }
        }

        private void EndOperation()
        {
            lock (_lifetimeSync)
            {
                _activeOperations--;
                DisposeGatesIfIdle();
            }
        }

        private void DisposeGatesIfIdle()
        {
            // Waiters must finish and release their gates before the gates are disposed.
            if (!_disposed || _activeOperations != 0 || _gatesDisposed)
                return;
            _gatesDisposed = true;
            _receiveGate.Dispose();
            _closeGate.Dispose();
            _sendGate.Dispose();

        }

        private WebSocketCloseStatus MapCloseStatus(CloseStatusCode code) => code switch
        {
            CloseStatusCode.Normal => WebSocketCloseStatus.NormalClosure,
            CloseStatusCode.Away => WebSocketCloseStatus.EndpointUnavailable,
            CloseStatusCode.ProtocolError => WebSocketCloseStatus.ProtocolError,
            CloseStatusCode.InvalidData => WebSocketCloseStatus.InvalidPayloadData,
            CloseStatusCode.UnsupportedData => WebSocketCloseStatus.InvalidMessageType,
            CloseStatusCode.PolicyViolation => WebSocketCloseStatus.PolicyViolation,
            CloseStatusCode.TooBig => WebSocketCloseStatus.MessageTooBig,
            CloseStatusCode.MandatoryExtension => WebSocketCloseStatus.MandatoryExtension,
            CloseStatusCode.ServerError => WebSocketCloseStatus.InternalServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };
    }
}
