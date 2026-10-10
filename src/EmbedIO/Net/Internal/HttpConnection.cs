using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal
{
    internal sealed partial class HttpConnection : IDisposable
    {
        private const int BufferSize = 8192;
        private static readonly byte[] BadRequestResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] UriTooLongResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 414 URI Too Long\r\nCache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] HeaderFieldsTooLargeResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 431 Request Header Fields Too Large\r\nCache-Control: no-store\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        private static readonly byte[] ContinueResponse = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
        private Http1HeadReader _headReader;
        private bool _http2;
        private EmbedIO.Internal.BorrowedResource<CancellationTokenSource>? _http2Stop;
        private Dictionary<HttpListener, Dictionary<Http2Exchange, MultiplexedContext>>? _http2Listeners;
        private int _responseFinishing;
        private volatile bool _draining;
        private bool _closeFinished;
        private Exception? _closeError;
        private TaskCompletionSource<bool>? _closedSignal;
        private EmbedIO.Internal.BorrowedResource<Http2Dispatcher>? _http2Dispatcher;

        private readonly Timer _timer;
        private readonly object _connectionSync = new();
        private int _forceClosing;
        private int _resourcesDisposed;
        private readonly EndPointListener _epl;
        internal EndPointListener Endpoint => _epl;
        private Socket? _sock;
        private ArraySegment<byte> _pendingInput;
        private byte[]? _buffer;
        private HttpListenerContext _context;
        private RequestStream? _iStream;
        private ResponseStream? _oStream;
        private bool _contextBound;
        private bool _tunnel;
        private int _sTimeout = 90000; // 90k ms for first request, 15k ms from then on
        private HttpListener? _lastListener;
        private string? _errorMessage;

        public HttpConnection(Socket sock, EndPointListener epl) : this(sock, epl, 90000) { }
        internal HttpConnection(Socket sock, EndPointListener epl, int initialHeaderTimeout)
        {
            if (initialHeaderTimeout <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeaderTimeout));
            _sTimeout = initialHeaderTimeout;
            _sock = sock;
            _epl = epl;
            IsSecure = epl.Secure;
            LocalEndPoint = (IPEndPoint)(sock.LocalEndPoint ?? throw new ArgumentException("The socket has no local endpoint.", nameof(sock)));
            RemoteEndPoint = (IPEndPoint)(sock.RemoteEndPoint ?? throw new ArgumentException("The socket has no remote endpoint.", nameof(sock)));

            Stream = new NetworkStream(sock, false);
            if (IsSecure)
            {
                Stream = new SslStream(Stream, false);
            }

            _timer = new Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
            Init();
            // Admission can queue initialization. Bound that wait as well as head/TLS reads.
            _ = _timer.Change(_sTimeout, Timeout.Infinite);
        }

        public int Reuses { get; private set; }

        internal bool IsDraining => _draining;

        public Stream Stream { get; }

        public IPEndPoint LocalEndPoint { get; }

        public IPEndPoint RemoteEndPoint { get; }

        public bool IsSecure { get; }

        public ListenerPrefix? Prefix { get; set; }

        public void Dispose()
        {
            try { ForceClose(); }
            finally { DisposeTransportResources(); }
        }
        public async Task BeginReadRequest()
        {
            byte[] buffer;
            bool bufferedInput;
            try
            {
                lock (_connectionSync)
                {
                    if (_resourcesDisposed != 0) return;
                    buffer = _buffer ??= new byte[BufferSize];
                    bufferedInput = _pendingInput.Count > 0;
                    if (Reuses == 1) _sTimeout = 15000;
                    if (Reuses != 0) _ = _timer.Change(_sTimeout, Timeout.Infinite);
                }
                // Authenticate outside the socket accept callback. The request timer also
                // bounds a client that connects without completing its TLS handshake.
                if (Stream is SslStream sslStream && !sslStream.IsAuthenticated)
                {
#if NET10_0_OR_GREATER
                    await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _epl.Listener.Certificate ?? throw new InvalidOperationException("The HTTPS listener has no certificate."),
                        EnabledSslProtocols = SslProtocols.None,
                        ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                    }).ConfigureAwait(false);
                    if (sslStream.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                    {
                        if (sslStream.SslProtocol != SslProtocols.Tls12 && sslStream.SslProtocol != SslProtocols.Tls13)
                            throw new AuthenticationException("HTTP/2 requires TLS 1.2 or later.");
                        await RunHttp2Async(Stream).ConfigureAwait(false);
                        return;
                    }
#else
                    await sslStream.AuthenticateAsServerAsync(_epl.Listener.Certificate ?? throw new InvalidOperationException("The HTTPS listener has no certificate."),
                        false, SslProtocols.None, false).ConfigureAwait(false);
#endif
                }

                var data = bufferedInput ? 0 : await Stream.ReadAsync(buffer, 0, BufferSize).ConfigureAwait(false);
                if (!IsSecure && Reuses == 0 && !bufferedInput && data > 0 && buffer[0] == (byte)'P')
                {
                    var preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
                    while (true)
                    {
                        var matches = true;
                        for (var i = 0; i < Math.Min(data, preface.Length); i++)
                            if (buffer[i] != preface[i]) { matches = false; break; }
                        if (!matches) break;
                        if (data >= preface.Length)
                        {
                            using var replay = new PrefixReadStream(Stream, buffer, data);
                            await RunHttp2Async(replay).ConfigureAwait(false);
                            return;
                        }
                        var more = await Stream.ReadAsync(buffer, data, BufferSize - data).ConfigureAwait(false);
                        if (more == 0) throw new EndOfStreamException("Incomplete protocol preface.");
                        data += more;
                    }
                }
                await OnReadInternal(data, bufferedInput).ConfigureAwait(false);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                StopRequestTimer();
                CloseSocket();
            }
        }

        public RequestStream GetRequestStream(long contentLength, bool chunked = false)
        {
            if (_iStream == null)
            {
                if (Volatile.Read(ref _resourcesDisposed) != 0)
                    throw new ObjectDisposedException(nameof(HttpConnection));
                var pending = _pendingInput;
                _pendingInput = default;
                var buffer = pending.Array ?? Array.Empty<byte>();
                _iStream = chunked ? new ChunkedRequestStream(Stream, buffer, pending.Offset, pending.Count)
                    : new RequestStream(Stream, buffer, pending.Offset, pending.Count, contentLength);
            }

            return _iStream;
        }

#if !NET10_0_OR_GREATER
        // Only public runtime API is queried: the older reference assembly does
        // not expose this method. No private TLS state or native interop is used.
        private static readonly Func<SslStream, Task>? ShutdownTls = GetTlsShutdown();
        private static Func<SslStream, Task>? GetTlsShutdown()
        {
            var method = typeof(SslStream).GetMethod("ShutdownAsync", Type.EmptyTypes);
            return method == null ? null : (Func<SslStream, Task>)method.CreateDelegate(typeof(Func<SslStream, Task>));
        }
#endif
        internal void BeginTunnel()
        {
#if !NET10_0_OR_GREATER
            if (IsSecure && ShutdownTls == null)
                throw new NotSupportedException("This runtime has no public TLS send-shutdown API for HTTP/1 tunnels.");
#endif
            lock (_connectionSync)
            {
                if (_resourcesDisposed != 0 || _sock == null) throw new ObjectDisposedException(nameof(HttpConnection));
                if (_tunnel) throw new InvalidOperationException("The connection was already handed off.");
                _tunnel = true;
            }
        }
        internal async Task CompleteTunnelOutputAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var cancellation = token.Register(ForceClose);
            await Stream.FlushAsync(token).ConfigureAwait(false);
            if (Stream is SslStream ssl)
            {
#if NET10_0_OR_GREATER
                await ssl.ShutdownAsync().ConfigureAwait(false);
#else
                await (ShutdownTls ?? throw new NotSupportedException("TLS send-shutdown is unavailable."))(ssl).ConfigureAwait(false);
#endif
            }
            token.ThrowIfCancellationRequested();
            lock (_connectionSync)
            {
                if (_sock == null || _resourcesDisposed != 0) throw new ObjectDisposedException(nameof(HttpConnection));
                _sock.Shutdown(SocketShutdown.Send);
            }
        }

        internal Stream TakeUpgradeStream()
        {
            lock (_connectionSync)
            {
                if (_resourcesDisposed != 0 || _sock == null)
                    throw new ObjectDisposedException(nameof(HttpConnection));
                var pending = _iStream != null ? _iStream.BufferedRemainder : _pendingInput;
                _pendingInput = default;
                if (pending.Count == 0) return Stream;
                // Isolate the unread protocol tail from HTTP's reusable read buffer.
                // The connection continues to own the underlying transport.
                var prefix = new byte[pending.Count];
                Buffer.BlockCopy(pending.Array ?? throw new InvalidOperationException("Missing upgrade bytes."),
                    pending.Offset, prefix, 0, prefix.Length);
                return new Http2.PrefixReadStream(Stream, prefix, prefix.Length);
            }
        }

        public ResponseStream GetResponseStream() => _oStream ??= new ResponseStream(Stream, _context.HttpListenerResponse, _context.Listener?.IgnoreWriteExceptions ?? true);

        internal void SetError(string message) => _errorMessage = message;

        internal void ForceClose()
        {
            Volatile.Write(ref _forceClosing, 1);
            // Abort before response disposal can synthesize headers or a final chunk.
            try { CloseTransport(true); }
            finally { _oStream?.Dispose(); _oStream = null; }
        }

        internal Task DrainAsync() => DrainCoreAsync(null);

        private Task DrainCoreAsync(HttpListener? owner)
        {
            Http2Dispatcher? dispatcher;
            Task completion;
            bool close;
            lock (_connectionSync)
            {
                if (owner != null && !_epl.AdmissionStopped && !_http2 && _lastListener != owner)
                    return Task.CompletedTask;
                if (_closeFinished) return _closeError == null ? Task.CompletedTask : Task.FromException(_closeError);
                _closedSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = _closedSignal.Task;
                if (_draining) return completion;
                _draining = true;
                dispatcher = _http2Dispatcher?.Value;
                close = _http2 ? dispatcher == null : !_contextBound;
            }
            if (dispatcher != null) _ = DrainHttp2Async(dispatcher);
            else if (close) CloseTransport(true);
            return completion;
        }

        private async Task DrainHttp2Async(Http2Dispatcher dispatcher)
        {
            try { await dispatcher.DrainAsync().ConfigureAwait(false); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // A concurrent peer close can dispose the borrowed dispatcher.
                // A failed GOAWAY cannot leave this connection waiting forever.
                CloseTransport(true);
            }
        }


        internal void Close(bool forceClose = false)
        {
            if (forceClose) Volatile.Write(ref _forceClosing, 1);
            if (_http2) { CloseTransport(true); return; }
            if (!forceClose && Interlocked.Exchange(ref _responseFinishing, 1) != 0) return;
            if (_sock != null)
            {
                // Dispose may call Response.Close recursively. A forced close is
                // recorded first so that callback cannot restart the request reader.
                _oStream?.Dispose();
                _oStream = null;
            }
            if (_sock == null) return;

            if (!_tunnel && !_draining && Volatile.Read(ref _forceClosing) == 0 && _context.Request.KeepAlive
                && _context.Response.KeepAlive && _context.Response.Headers["connection"] != "close")
            {
                _ = CompleteResponseAsync();
                return;
            }
            CloseTransport(true);
        }

        private async Task CompleteResponseAsync()
        {
            try
            {
                if (!await _context.HttpListenerRequest.FlushInputAsync().ConfigureAwait(false))
                {
                    CloseTransport(true);
                    return;
                }
                RestartRequest();
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { CloseTransport(true); }
        }

        private void RestartRequest()
        {
            var restart = false;
            lock (_connectionSync)
            {
                if (_sock != null && _resourcesDisposed == 0 && _forceClosing == 0 && !_draining)
                {
                    var pending = _iStream != null ? _iStream.BufferedRemainder : _pendingInput;
                    // Keep the initial-request marker distinct even on long-lived connections.
                    if (Reuses < int.MaxValue) Reuses++;
                    Unbind();
                    InitWithPendingInput(pending);
                    restart = true;
                }
            }
            // RegisterContext acquires the listener lock; do not enter the
            // request reader while holding a connection lock.
            if (restart)
            {
                _ = BeginReadRequest();
                return;
            }

            CloseTransport(true);
        }
        [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_context))]
        private void Init() => InitWithPendingInput(default);

        [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_context))]
        private void InitWithPendingInput(ArraySegment<byte> pending)
        {
            _contextBound = false;
            _iStream = null;
            _oStream = null;
            Prefix = null;
            // The completed body relinquishes its unread tail before this reader
            // may reuse the connection buffer. Partial head lines are owned by the parser.
            _pendingInput = pending;
            _headReader.Reset();
            _responseFinishing = 0;
            _errorMessage = null;
            _context = new HttpListenerContext(this);
        }

        private void OnTimeout(object? unused)
        {
            CloseSocket();
        }

        private async Task OnReadInternal(int offset, bool bufferedInput = false)
        {
            // Keep the header deadline active through every fragmented read.
            // Continue reading until full header is received.
            // Especially important for multipart requests when the second part of the header arrives after a tiny delay
            // because the web browser has to measure the content length first.
            while (true)
            {
                var buffer = _buffer;
                if (buffer == null) { CloseSocket(); return; }
                var input = bufferedInput ? _pendingInput : new ArraySegment<byte>(buffer, 0, offset);

                if (offset == 0 && !bufferedInput)
                {
                    CloseSocket();
                    return;
                }

                bufferedInput = false;
                if (ProcessInput(input))
                {
                    if (_errorMessage is null)
                    {
                        _context.HttpListenerRequest.FinishInitialization();
                    }

                    if (_errorMessage != null)
                    {
                        // Keep the existing request deadline active through this bounded write.
                        // Never reflect untrusted parser diagnostics or restart this connection.
                        try
                        {
                            var response = _headReader.ErrorStatusCode switch
                            {
                                414 => UriTooLongResponse,
                                431 => HeaderFieldsTooLargeResponse,
                                _ => BadRequestResponse,
                            };
                            await Stream.WriteAsync(response, 0, response.Length).ConfigureAwait(false);
                        }
                        finally { Close(true); }
                        return;
                    }

                    if (_context.HttpListenerRequest.RequiresContinue)
                        await Stream.WriteAsync(ContinueResponse, 0, ContinueResponse.Length).ConfigureAwait(false);

                    StopRequestTimer();
                    if (!_epl.BindContext(_context))
                    {
                        Close(true);
                        return;
                    }

                    var listener = _context.Listener ?? throw new InvalidOperationException("The request has not been bound to a listener.");
                    lock (_connectionSync)
                    {
                        if (_sock == null || _resourcesDisposed != 0 || _draining || _forceClosing != 0)
                            throw new IOException("Connection stopped before request admission.");
                        if (_lastListener != listener)
                        {
                            RemoveConnection();
                            listener.AddConnection(this);
                            _lastListener = listener;
                        }
                        _contextBound = true;
                    }
                    listener.RegisterContext(_context);
                    return;
                }

                offset = await Stream.ReadAsync(_buffer ?? throw new InvalidOperationException("The read buffer has been released."), 0, BufferSize).ConfigureAwait(false);
            }
        }

        private void RemoveConnection()
        {
            if (_lastListener != null)
            {
                _lastListener.RemoveConnection(this);
            }
            else
            {
                _epl.RemoveConnection(this);
            }
        }

        // true -> done processing
        // false -> need more input
        private bool ProcessInput(ArraySegment<byte> input)
        {
            var buffer = input.Array ?? Array.Empty<byte>();
            var position = input.Offset;
            var end = position + input.Count;
            while (position < end)
            {
                if (_errorMessage != null) return true;
                try
                {
                    var result = _headReader.Read(buffer, position, end - position, out var used, out var line);
                    position += used;
                    if (result == Http1HeadReadResult.Complete)
                    {
                        _pendingInput = new ArraySegment<byte>(buffer, position, end - position);
                        return true;
                    }
                    if (result == Http1HeadReadResult.NeedMoreData) break;
                    var value = line ?? throw new InvalidDataException("Missing request head line.");
                    if (result == Http1HeadReadResult.RequestLine) _context.HttpListenerRequest.SetRequestLine(value);
                    else _context.HttpListenerRequest.AddHeader(value);
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    _errorMessage = error.Message;
                    return true;
                }
            }
            if (_errorMessage != null) return true;
            // The head reader owns partial-line bytes; the next socket read can
            // overwrite this consumed segment without copying or compaction.
            _pendingInput = default;
            return false;
        }

        private void Unbind()
        {
            if (!_contextBound)
            {
                return;
            }

            _epl.UnbindContext(_context);
            _contextBound = false;
        }

        private void CloseSocket() => CloseTransport(false);

        private void CloseTransport(bool shutdown)
        {
            Socket? socket;
            CancellationTokenSource? protocolStop;
            HttpListener[] protocolListeners;
            lock (_connectionSync)
            {
                socket = _sock;
                _sock = null;
                protocolStop = _http2Stop?.Value;
                protocolListeners = _http2Listeners == null ? Array.Empty<HttpListener>() : new List<HttpListener>(_http2Listeners.Keys).ToArray();
                _http2Listeners?.Clear();
            }
            if (socket == null) return;
            try
            {
                if (shutdown)
                {
                    try { socket.Shutdown(SocketShutdown.Both); }
                    catch (Exception error) when (error is SocketException or ObjectDisposedException)
                    { /* A disconnected socket has nothing left to shut down. */ }
                }
            }
            finally
            {
                socket.Dispose();
                try { protocolStop?.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { /* Complete transport cleanup even if a cancellation callback fails. */ }
                foreach (var listener in protocolListeners) listener.RemoveConnection(this);
                try
                {
                    Unbind();
                    RemoveConnection();
                }
                finally { DisposeTransportResources(); }
            }
        }

        private void DisposeTransportResources()
        {
            RequestStream? input;
            lock (_connectionSync)
            {
                if (_resourcesDisposed != 0) return;
                _resourcesDisposed = 1;
                input = _iStream;
                _pendingInput = default;
                _iStream = null;
                _buffer = null;
                _headReader.Reset();
            }
            try
            {
                _timer.Dispose();
                input?.Dispose();
                Stream.Dispose();
            }
            catch (Exception error)
            {
                lock (_connectionSync) _closeError = error;
                throw;
            }
            finally
            {
                lock (_connectionSync)
                {
                    _closeFinished = true;
                    if (_closeError == null) _closedSignal?.TrySetResult(true);
                    else _closedSignal?.TrySetException(_closeError);
                }
            }
        }

        private void StopRequestTimer()
        {
            try { _ = _timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) when (Volatile.Read(ref _resourcesDisposed) != 0)
            {
                // Terminal cleanup can race a reader completing or failing.
            }
        }
    }
}
