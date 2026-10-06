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
        private readonly SemaphoreSlim _receiveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _closeGate = new SemaphoreSlim(1, 1);

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

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public Task SendAsync(byte[] buffer, bool isText, CancellationToken cancellationToken = default)
            => UnderlyingWebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                isText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true,
                cancellationToken);

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
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return UnderlyingWebSocket.CloseAsync(code, comment, cancellationToken);

            // Preserve native synchronous reason validation, including terminal sockets.
            if (Encoding.UTF8.GetByteCount(comment) > 123)
                throw new ArgumentException("The close description exceeds 123 UTF-8 bytes.", "statusDescription");

            return CloseWindowsAsync(code, comment, cancellationToken);
        }

        private async Task CloseWindowsAsync(WebSocketCloseStatus code, string comment, CancellationToken cancellationToken)
        {
            if (State == WebSocketState.Closed || State == WebSocketState.Aborted)
                return;

            BeginOperation();
            try
            {
                await _closeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (State == WebSocketState.Closed || State == WebSocketState.Aborted)
                        return;

                    // Avoid WebSocketBase.CloseAsync's inverted locks (dotnet/runtime #115559).
                    // Recheck against released .NET 11 before considering removal of this path.
                    if (State != WebSocketState.CloseSent)
                        await UnderlyingWebSocket.CloseOutputAsync(code, comment, cancellationToken).ConfigureAwait(false);

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
                catch
                {
                    UnderlyingWebSocket.Abort();
                    throw;
                }
                finally
                {
                    _closeGate.Release();
                }
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

        }

        private WebSocketCloseStatus MapCloseStatus(CloseStatusCode code) => code switch
        {
            CloseStatusCode.Normal => WebSocketCloseStatus.NormalClosure,
            CloseStatusCode.ProtocolError => WebSocketCloseStatus.ProtocolError,
            CloseStatusCode.InvalidData => WebSocketCloseStatus.InvalidPayloadData,
            CloseStatusCode.UnsupportedData => WebSocketCloseStatus.InvalidPayloadData,
            CloseStatusCode.PolicyViolation => WebSocketCloseStatus.PolicyViolation,
            CloseStatusCode.TooBig => WebSocketCloseStatus.MessageTooBig,
            CloseStatusCode.MandatoryExtension => WebSocketCloseStatus.MandatoryExtension,
            CloseStatusCode.ServerError => WebSocketCloseStatus.InternalServerError,
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
        };
    }
}
