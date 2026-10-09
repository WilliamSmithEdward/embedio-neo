using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.WebSockets.Internal
{
    /// <summary>
    /// Implements the WebSocket interface.
    /// </summary>
    /// <remarks>
    /// The WebSocket class provides a set of methods and properties for two-way communication using
    /// the WebSocket protocol (<see href="http://tools.ietf.org/html/rfc6455">RFC 6455</see>).
    /// </remarks>
    internal sealed class WebSocket : IWebSocket
    {
        public const string SupportedVersion = "13";

        // Outgoing data messages are split into frames of at most this many payload
        // bytes. Each frame is one transport write, and control frames can be sent
        // between the frames of a long message.
        internal const int SendFragmentLength = 65536;

        // The accepting module's MaxMessageSize, flowed to the socket constructor so
        // the limit applies before the receive loop reads its first frame. The
        // public IHttpContextImpl.AcceptWebSocketAsync contract has no parameter for it.
        private static readonly AsyncLocal<int> AcceptedMaxMessageSize = new();

        private readonly object _stateSyncRoot = new();
        private readonly object _messageSyncRoot = new();
        private readonly ConcurrentQueue<MessageEventArgs> _messageEventQueue = new();
        private readonly Action _closeConnection;
        private readonly TimeSpan _waitTime = TimeSpan.FromSeconds(1);
        private readonly SemaphoreSlim _messageSendGate = new(1, 1);
        private readonly SemaphoreSlim _frameWriteGate = new(1, 1);
        private int _sendOperations;
        private int _resourcesReleased;
        private bool _sendGatesDisposed;
        private bool _closeCompleted;
        private TaskCompletionSource<bool>? _closedSignal;

        private volatile WebSocketState _readyState;
        private TaskCompletionSource<bool>? _exitReceiving;
        private FragmentBuffer? _fragmentsBuffer;
        private bool _inMessage;
        private bool _closeDeferred;
        private EventHandler<MessageEventArgs>? _onMessage;
        private AutoResetEvent? _receivePong;
        // The connection-close callback owns the underlying transport lifetime.
        private EmbedIO.Internal.BorrowedResource<Stream>? _stream;
        private readonly int _maxMessageSize;

        private WebSocket(HttpConnection connection) : this(connection.Stream, connection.ForceClose) { }

        private WebSocket(Stream stream, Action close)
        {
            _closeConnection = close;
            _stream = new EmbedIO.Internal.BorrowedResource<Stream>(stream);
            _readyState = WebSocketState.Open;
            _maxMessageSize = AcceptedMaxMessageSize.Value;
        }

        // Sets the incoming message size limit for sockets accepted later in the
        // current asynchronous flow. Zero disables the check.
        internal static void SetAcceptedMaxMessageSize(int value) => AcceptedMaxMessageSize.Value = value;

        internal static WebSocket FromStream(Stream stream, Action close)
        {
            var socket = new WebSocket(stream, close);
            socket.Open();
            return socket;
        }

        ~WebSocket()
        {
            Dispose(false);
        }

        /// <summary>
        /// Occurs when the <see cref="WebSocket"/> receives a message.
        /// </summary>
        public event EventHandler<MessageEventArgs>? OnMessage
        {
            add
            {
                lock (_messageSyncRoot) _onMessage += value;
                // Frames can arrive before the module finishes connection initialization.
                // Registering the consumer must also wake a previously idle queue.
                ScheduleMessages();
            }
            remove
            {
                bool wake;
                lock (_messageSyncRoot)
                {
                    _onMessage -= value;
                    wake = _closeDeferred && !_inMessage;
                }
                // A close deferred for this consumer must still complete.
                if (wake) _ = Task.Run(Message);
            }
        }

        /// <inheritdoc />
        public WebSocketState State => _readyState;

        internal Task WaitForCloseAsync(CancellationToken cancellationToken)
        {
            Task closed;
            lock (_stateSyncRoot)
            {
                if (_closeCompleted) return Task.CompletedTask;
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                closed = (_closedSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
            return cancellationToken.CanBeCanceled ? WaitForCloseOrCancellationAsync(closed, cancellationToken) : closed;
        }

        private static async Task WaitForCloseOrCancellationAsync(Task closed, CancellationToken token)
        {
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => canceled.TrySetCanceled(token)))
                await (await Task.WhenAny(closed, canceled.Task).ConfigureAwait(false)).ConfigureAwait(false);
        }

        internal bool EmitOnPing { get; set; }

        internal bool InContinuation { get; private set; }

        /// <inheritdoc />
        public Task SendAsync(byte[] buffer, bool isText, CancellationToken cancellationToken) => SendAsync(buffer, isText ? Opcode.Text : Opcode.Binary, cancellationToken);

        /// <inheritdoc />
        public Task CloseAsync(CancellationToken cancellationToken = default) => CloseAsync(CloseStatusCode.Normal, cancellationToken: cancellationToken);

        /// <inheritdoc />
        public Task CloseAsync(
            CloseStatusCode code = CloseStatusCode.Undefined,
            string? reason = null,
            CancellationToken cancellationToken = default)
        {
            bool CheckParametersForClose()
            {
                if (code == CloseStatusCode.NoStatus && !string.IsNullOrEmpty(reason))
                {
                    "'code' cannot have a reason.".Trace(nameof(WebSocket));
                    return false;
                }

                if (code == CloseStatusCode.MandatoryExtension)
                {
                    "'code' cannot be used by a server.".Trace(nameof(WebSocket));
                    return false;
                }

                if (!string.IsNullOrEmpty(reason) && Encoding.UTF8.GetBytes(reason).Length > 123)
                {
                    "The size of 'reason' is greater than the allowable max size.".Trace(nameof(WebSocket));
                    return false;
                }

                return true;
            }

            if (_readyState != WebSocketState.Open)
            {
                return Task.CompletedTask;
            }

            if (code != CloseStatusCode.Undefined && !CheckParametersForClose())
            {
                return Task.CompletedTask;
            }

            if (code == CloseStatusCode.NoStatus)
            {
                return InternalCloseAsync(cancellationToken: cancellationToken);
            }

            var send = !IsOpcodeReserved(code);
            return InternalCloseAsync(new PayloadData((ushort)code, reason), send, send, cancellationToken);
        }

        /// <summary>
        /// Sends a ping using the WebSocket connection.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the <see cref="WebSocket"/> receives a pong to this ping in a time;
        /// otherwise, <c>false</c>.
        /// </returns>
        public Task<bool> PingAsync() => PingAsync(WebSocketFrame.EmptyPingBytes, _waitTime);

        /// <summary>
        /// Sends a ping with the specified <paramref name="message"/> using the WebSocket connection.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the <see cref="WebSocket"/> receives a pong to this ping in a time;
        /// otherwise, <c>false</c>.
        /// </returns>
        /// <param name="message">
        /// A <see cref="string"/> that represents the message to send.
        /// </param>
        public Task<bool> PingAsync(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return PingAsync();
            }

            var data = Encoding.UTF8.GetBytes(message);

            if (data.Length <= 125)
            {
                return PingAsync(WebSocketFrame.CreatePingFrame(data).ToArray(), _waitTime);
            }

            "A message has greater than the allowable max size.".Error(nameof(PingAsync));

            return Task.FromResult(false);
        }

        /// <summary>
        /// Sends binary <paramref name="data" /> using the WebSocket connection.
        /// </summary>
        /// <param name="data">An array of <see cref="byte" /> that represents the binary data to send.</param>
        /// <param name="opcode">The opcode.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// A task that represents the asynchronous of send
        /// binary data using websocket.
        /// </returns>
        public async Task SendAsync(byte[] data, Opcode opcode, CancellationToken cancellationToken = default)
        {
            if (_readyState != WebSocketState.Open)
            {
                throw new WebSocketException(CloseStatusCode.Normal, $"This operation isn\'t available in: {_readyState}");
            }

            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!BeginSendOperation())
                throw new WebSocketException(CloseStatusCode.Normal, "The connection has been closed.");
            var entered = false;
            var started = false;
            try
            {
                await _messageSendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                var offset = 0;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = Math.Min(data.Length - offset, SendFragmentLength);
                    var final = offset + count == data.Length;
                    if (!await WriteDataFrameAsync(offset == 0 ? opcode : Opcode.Cont, final, data, offset, count, cancellationToken).ConfigureAwait(false))
                        return;
                    started = true;
                    offset += count;
                }
                while (offset < data.Length);
            }
            catch
            {
                // A partially sent fragmented message cannot be followed by another data message.
                if (started) AbortSend();
                throw;
            }
            finally
            {
                if (entered) _messageSendGate.Release();
                EndSendOperation();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private static bool IsHandshakeKey(string? value)
        {
            // Exactly 16 decoded bytes require 22 alphabet characters and two
            // padding characters. OWS has already been removed by HTTP parsing.
            if (value == null || value.Length != 24 || value[22] != '=' || value[23] != '=') return false;
            for (var i = 0; i < 22; ++i)
            {
                var c = value[i];
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z')
                    && !(c >= '0' && c <= '9') && c != '+' && c != '/') return false;
            }
            return true;
        }

        internal static async Task<WebSocket> AcceptAsync(HttpListenerContext httpContext, string acceptedProtocol)
        {
            static string CreateResponseKey(string clientKey)
            {
                const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

                var buff = new StringBuilder(clientKey, 64).Append(Guid);
                using var sha1 = SHA1.Create();
                return Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(buff.ToString())));
            }

            if (!httpContext.Request.IsWebSocketRequest)
                throw new HttpException(System.Net.HttpStatusCode.BadRequest, "A WebSocket upgrade requires HTTP/1.1 GET, Upgrade and Connection tokens.");
            var requestHeaders = httpContext.Request.Headers;
            var webSocketKey = requestHeaders[HttpHeaderNames.SecWebSocketKey];
            if (!IsHandshakeKey(webSocketKey))
                throw new HttpException(System.Net.HttpStatusCode.BadRequest, "Sec-WebSocket-Key must encode a 16-byte nonce.");
            if (requestHeaders[HttpHeaderNames.SecWebSocketVersion] != SupportedVersion)
            {
                httpContext.Response.Headers[HttpHeaderNames.SecWebSocketVersion] = SupportedVersion;
                throw new HttpException(System.Net.HttpStatusCode.BadRequest, "Unsupported WebSocket version.");
            }

            var handshakeResponse = new WebSocketHandshakeResponse(httpContext);

            handshakeResponse.Headers[HttpHeaderNames.SecWebSocketAccept] = CreateResponseKey(webSocketKey ?? throw new InvalidOperationException("Missing validated WebSocket key."));

            if (acceptedProtocol.Length > 0)
            {
                handshakeResponse.Headers[HttpHeaderNames.SecWebSocketProtocol] = acceptedProtocol;
            }

            var bytes = Encoding.UTF8.GetBytes(handshakeResponse.ToString());
            await httpContext.Connection.Stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);

            // Signal the original response that headers have been sent.
            httpContext.HttpListenerResponse.HeadersSent = true;

            var socket = new WebSocket(httpContext.Connection);
            socket.Open();
            return socket;
        }

        internal async Task<bool> PingAsync(byte[] frameAsBytes, TimeSpan timeout)
        {
            if (_readyState != WebSocketState.Open)
            {
                return false;
            }

            if (!await WriteFrameBytesAsync(frameAsBytes, CancellationToken.None).ConfigureAwait(false))
                return false;

            return _receivePong != null && _receivePong.WaitOne(timeout);
        }

        private static bool IsOpcodeReserved(CloseStatusCode code)
            => code == CloseStatusCode.Undefined
            || code == CloseStatusCode.NoStatus
            || code == CloseStatusCode.Abnormal
            || code == CloseStatusCode.TlsHandshakeFailure;

        private void Dispose(bool disposing)
        {
            try
            {
                InternalCloseAsync(new PayloadData((ushort)CloseStatusCode.Away)).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // Ignored
            }
        }

        private async Task InternalCloseAsync(
            PayloadData? payloadData = null,
            bool send = true,
            bool receive = true,
            CancellationToken cancellationToken = default)
        {
            lock (_stateSyncRoot)
            {
                if (_readyState == WebSocketState.CloseReceived || _readyState == WebSocketState.CloseSent)
                {
                    "The closing is already in progress.".Trace(nameof(InternalCloseAsync));
                    return;
                }

                if (_readyState == WebSocketState.Closed)
                {
                    "The connection has been closed.".Trace(nameof(InternalCloseAsync));
                    return;
                }

                send = send && _readyState == WebSocketState.Open;
                receive = receive && send;

                _readyState = WebSocketState.CloseSent;
            }

            "Begin closing the connection.".Trace(nameof(InternalCloseAsync));

            var bytes = send ? WebSocketFrame.CreateCloseFrame(payloadData).ToArray() : null;
            try
            {
                await CloseHandshakeAsync(bytes, receive, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_stateSyncRoot) _readyState = WebSocketState.Closed;
                ReleaseResources();
                "End closing the connection.".Trace(nameof(InternalCloseAsync));
            }
        }

        private async Task CloseHandshakeAsync(
            byte[]? frameAsBytes,
            bool receive,
            CancellationToken cancellationToken)
        {
            var sent = frameAsBytes != null;

            if (frameAsBytes != null)
            {
                sent = await WriteFrameBytesAsync(frameAsBytes, cancellationToken, allowClosing: true).ConfigureAwait(false);
            }

            var exitReceiving = _exitReceiving;
            if (receive && sent && exitReceiving != null)
            {
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CancellationToken.None))
                {
                    var timeout = Task.Delay(_waitTime, deadline.Token);
                    try { await Task.WhenAny(exitReceiving.Task, timeout).ConfigureAwait(false); }
                    finally { deadline.Cancel(); }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Sends the failure status and completes as a server-initiated close: wait for
        // the peer's close (or the close timeout) while the receive loop drains input.
        private async Task FailWithCloseHandshakeAsync(WebSocketException error)
        {
            var reason = Encoding.UTF8.GetByteCount(error.Message) <= 123 ? error.Message : null;
            try
            {
                await InternalCloseAsync(new PayloadData((ushort)error.Code, reason)).ConfigureAwait(false);
            }
            catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
            {
                ex.Log(nameof(WebSocket));
            }
        }

        private void Fatal(string message, Exception? exception = null)
            => Fatal(message, (exception as WebSocketException)?.Code ?? CloseStatusCode.Abnormal);

        private void Fatal(string message, CloseStatusCode code)
            => InternalCloseAsync(new PayloadData((ushort)code, message), !IsOpcodeReserved(code), false).ConfigureAwait(false).GetAwaiter().GetResult();

        // Starts a consumer only when one is needed. A frame that queued nothing, or
        // a queue already being drained, costs no thread-pool work item.
        private void ScheduleMessages()
        {
            lock (_messageSyncRoot)
            {
                if (_inMessage || _onMessage == null || _messageEventQueue.IsEmpty)
                    return;
            }
            _ = Task.Run(Message);
        }

        // Messages are queued only while the connection is open, so everything in the
        // queue was completed on the wire before any close. A subscribed consumer keeps
        // draining after the state changes; the close completes once it is idle.
        private void Message()
        {
            bool closed;
            lock (_messageSyncRoot)
            {
                if (_inMessage) return;
                if (_onMessage != null)
                {
                    _inMessage = true;
                    closed = false;
                }
                // Without a consumer only a deferred close remains to be completed.
                else if (TakeDeferredClose()) closed = true;
                else return;
            }

            while (!closed)
            {
                EventHandler<MessageEventArgs> handler;
                MessageEventArgs? message;
                lock (_messageSyncRoot)
                {
                    var current = _onMessage;
                    if (current == null || !_messageEventQueue.TryDequeue(out message))
                    {
                        // Publish the idle state atomically with the empty-queue check.
                        // An enqueue or subscription can then start the next consumer.
                        _inMessage = false;
                        if (!TakeDeferredClose()) return;
                        break;
                    }
                    handler = current;
                }
                try { handler(this, message); }
                catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex)) { ex.Log(nameof(WebSocket)); }
            }
            CompleteClose();
        }

        // Called under _messageSyncRoot. After a close nothing more can be queued, and
        // whatever an unsubscribed consumer left behind will never be delivered.
        private bool TakeDeferredClose()
        {
            if (!_closeDeferred) return false;
            _closeDeferred = false;
            while (_messageEventQueue.TryDequeue(out _)) { }
            return true;
        }

        private void Open() => StartReceiving();

        private Task ProcessCloseFrame(WebSocketFrame frame) => InternalCloseAsync(frame.PayloadData, !frame.PayloadData.HasReservedCode, false);

        private Task ProcessDataFrame(WebSocketFrame frame)
        {
            // The reader allocated this payload for the frame alone; the message takes
            // ownership instead of copying it.
            _messageEventQueue.Enqueue(new MessageEventArgs(frame.Opcode, frame.PayloadData.ToArray()));
            return Task.CompletedTask;
        }

        // After a local close, data is discarded but fragment state must still follow
        // the wire so the peer's continuation frames remain valid until its close.
        private void DiscardFragmentFrame(WebSocketFrame frame)
        {
            _fragmentsBuffer?.Dispose();
            _fragmentsBuffer = null;
            InContinuation = frame.Fin == Fin.More;
        }

        private Task ProcessFragmentFrame(WebSocketFrame frame)
        {
            if (!InContinuation)
            {
                // Must process first fragment.
                if (frame.Opcode == Opcode.Cont)
                {
                    return Task.CompletedTask;
                }

                _fragmentsBuffer = new FragmentBuffer(frame.Opcode);
                InContinuation = true;
            }

            var fragments = _fragmentsBuffer ?? throw new InvalidOperationException("A continuation frame has no fragment buffer.");
            fragments.AddPayload(frame.PayloadData.ToArray());

            if (frame.Fin == Fin.Final)
            {
                using (fragments)
                {
                    _messageEventQueue.Enqueue(fragments.GetMessage());
                }

                _fragmentsBuffer = null;
                InContinuation = false;
            }

            return Task.CompletedTask;
        }

        private Task ProcessPingFrame(WebSocketFrame frame)
        {
            if (EmitOnPing)
            {
                _messageEventQueue.Enqueue(new MessageEventArgs(frame));
            }

            return Send(new WebSocketFrame(Opcode.Pong, frame.PayloadData));
        }

        private void ProcessPongFrame()
        {
            _ = _receivePong?.Set();
            "Received a pong.".Trace(nameof(ProcessPongFrame));
        }

        private async Task<bool> ProcessReceivedFrame(WebSocketFrame frame)
        {
            if (frame.IsFragment)
            {
                await ProcessFragmentFrame(frame).ConfigureAwait(false);
            }
            else
            {
                switch (frame.Opcode)
                {
                    case Opcode.Text:
                    case Opcode.Binary:
                        await ProcessDataFrame(frame).ConfigureAwait(false);
                        break;
                    case Opcode.Ping:
                        await ProcessPingFrame(frame).ConfigureAwait(false);
                        break;
                    case Opcode.Pong:
                        ProcessPongFrame();
                        break;
                    case Opcode.Close:
                        await ProcessCloseFrame(frame).ConfigureAwait(false);
                        break;
                    default:
                        Fatal($"Unsupported frame received: {frame.PrintToString()}", CloseStatusCode.PolicyViolation);
                        return false;
                }
            }

            return true;
        }

        private void ReleaseResources()
        {
            if (Interlocked.Exchange(ref _resourcesReleased, 1) != 0)
                return;
            try
            {
                DisposeSendGatesIfIdle();
                try { _closeConnection(); }
                finally
                {
                    _stream = null;

                    if (_fragmentsBuffer != null)
                    {
                        _fragmentsBuffer.Dispose();
                        _fragmentsBuffer = null;
                        InContinuation = false;
                    }

                    if (_receivePong != null)
                    {
                        _receivePong.Dispose();
                        _receivePong = null;
                    }

                    _exitReceiving?.TrySetResult(true);
                    _exitReceiving = null;
                }
            }
            finally
            {
                // A subscribed consumer delivers what was received before the close,
                // then completes it; observers such as WebSocketModule therefore see
                // the close after the last message was handed to the application.
                // Without a consumer the queued data is discarded.
                bool drain;
                lock (_messageSyncRoot)
                {
                    drain = _onMessage != null && (_inMessage || !_messageEventQueue.IsEmpty);
                    _closeDeferred = drain;
                    if (!drain) while (_messageEventQueue.TryDequeue(out _)) { }
                }
                if (drain) _ = Task.Run(Message);
                else CompleteClose();
            }
        }

        private void CompleteClose()
        {
            TaskCompletionSource<bool>? closed;
            lock (_stateSyncRoot)
            {
                _closeCompleted = true;
                closed = _closedSignal;
                _closedSignal = null;
            }
            closed?.TrySetResult(true);
        }

        private Task Send(WebSocketFrame frame)
            => WriteFrameBytesAsync(frame.ToArray(), CancellationToken.None);

        // Serializes one unmasked data frame into a pooled buffer so header and
        // payload leave in a single transport write.
        private async Task<bool> WriteDataFrameAsync(Opcode opcode, bool final, byte[] data, int offset, int count, CancellationToken cancellationToken)
        {
            var header = count < 126 ? 2 : count <= ushort.MaxValue ? 4 : 10;
            var length = header + count;
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                buffer[0] = (byte)((final ? 0x80 : 0) | (int)opcode);
                if (header == 2) buffer[1] = (byte)count;
                else if (header == 4)
                {
                    buffer[1] = 126;
                    BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)count);
                }
                else
                {
                    buffer[1] = 127;
                    BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(2), (ulong)count);
                }
                Buffer.BlockCopy(data, offset, buffer, header, count);
                return await WriteFrameBytesAsync(buffer, length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Application data must not linger in a shared pool.
                Array.Clear(buffer, 0, length);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private bool BeginSendOperation()
        {
            lock (_stateSyncRoot)
            {
                if (_resourcesReleased != 0)
                    return false;
                _sendOperations++;
                return true;
            }
        }

        private void EndSendOperation()
        {
            lock (_stateSyncRoot) _sendOperations--;
            DisposeSendGatesIfIdle();
        }

        private void DisposeSendGatesIfIdle()
        {
            lock (_stateSyncRoot)
            {
                if (_resourcesReleased == 0 || _sendOperations != 0 || _sendGatesDisposed)
                    return;
                _sendGatesDisposed = true;
                _messageSendGate.Dispose();
                _frameWriteGate.Dispose();
            }
        }

        private void AbortSend()
        {
            lock (_stateSyncRoot) _readyState = WebSocketState.Closed;
            ReleaseResources();
        }

        private Task<bool> WriteFrameBytesAsync(byte[] bytes, CancellationToken cancellationToken, bool allowClosing = false)
            => WriteFrameBytesAsync(bytes, bytes.Length, cancellationToken, allowClosing);

        private async Task<bool> WriteFrameBytesAsync(byte[] bytes, int count, CancellationToken cancellationToken, bool allowClosing = false)
        {
            if (!BeginSendOperation())
                return false;
            var entered = false;
            var writing = false;
            try
            {
                await _frameWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
                Stream? transport;
                lock (_stateSyncRoot)
                {
                    if (_resourcesReleased != 0 || (_readyState != WebSocketState.Open &&
                        !(allowClosing && (_readyState == WebSocketState.CloseSent || _readyState == WebSocketState.CloseReceived))))
                        return false;
                    transport = _stream?.Value;
                }
                if (transport == null) return false;
                cancellationToken.ThrowIfCancellationRequested();
                writing = true;
                await transport.WriteAsync(bytes, 0, count, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch
            {
                // A failed transport write can have emitted only part of a frame; do not reuse it.
                if (writing) AbortSend();
                throw;
            }
            finally
            {
                if (entered) _frameWriteGate.Release();
                EndSendOperation();
            }
        }

        private void StartReceiving()
        {
            while (_messageEventQueue.TryDequeue(out _))
            {
                // do nothing
            }

            var receivingStopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _exitReceiving = receivingStopped;
            _receivePong = new AutoResetEvent(false);

            var frameStream = new WebSocketFrameStream(_stream?.Value) { MaxMessageSize = _maxMessageSize };

            _ = Task.Run(async () =>
            {
                try
                {
                    while (_readyState == WebSocketState.Open || _readyState == WebSocketState.CloseSent)
                    {
                        try
                        {
                            var frame = await frameStream.ReadFrameAsync(this).ConfigureAwait(false);

                            if (frame == null) return;

                            // Keep reading the close acknowledgement without dispatching
                            // new application messages once local closing has started.
                            if (_readyState != WebSocketState.Open && frame.Opcode != Opcode.Close)
                            {
                                if (frame.IsFragment) DiscardFragmentFrame(frame);
                                continue;
                            }

                            var result = await ProcessReceivedFrame(frame).ConfigureAwait(false);

                            if (!result || frame.Opcode == Opcode.Close || _readyState == WebSocketState.Closed)
                                return;

                            ScheduleMessages();
                        }
                        catch (WebSocketException ex) when (frameStream.Rejected is { } rejected
                            && (_readyState == WebSocketState.Open || _readyState == WebSocketState.CloseSent))
                        {
                            // Frame boundaries are intact, so keep reading (and discarding)
                            // until the peer acknowledges the close. Closing the transport
                            // with its bytes unread would reset the connection, and the
                            // peer could lose the close frame and its status code.
                            if (rejected.IsFragment) DiscardFragmentFrame(rejected);
                            if (_readyState == WebSocketState.Open) _ = FailWithCloseHandshakeAsync(ex);
                        }
                        catch (Exception ex) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(ex))
                        {
                            // Sending the failure status can itself fail on a broken
                            // transport. Resources are released either way; nothing
                            // observes this task, so the error must not escape it.
                            try { Fatal("An exception has occurred while receiving.", ex); }
                            catch (Exception closeError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(closeError))
                            {
                                closeError.Log(nameof(WebSocket));
                            }
                            return;
                        }
                    }
                }
                finally { receivingStopped.TrySetResult(true); }
            });
        }
    }
}
