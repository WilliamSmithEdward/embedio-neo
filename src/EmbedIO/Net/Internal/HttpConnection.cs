using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
        private EmbedIO.Internal.BorrowedResource<Http2Dispatcher>? _http2Dispatcher;

        private readonly TcpSessionLifetime _lifetime;
        private readonly object _connectionSync = new();
        private int _forceClosing;
        private int _protocolEnded;
        private readonly EndPointListener _epl;
        internal EndPointListener Endpoint => _epl;
        private Socket? _protocolSocket;
        private ArraySegment<byte> _pendingInput;
        private byte[]? _buffer;
        private HttpListenerContext _context;
        private RequestStream? _iStream;
        private ResponseStream? _oStream;
        private bool _contextBound;
        private bool _tunnel;
        private int _sTimeout = 90000; // first request head, then the keep-alive deadline
        private readonly int _keepAliveTimeout;
        private readonly int? _http2IdleTimeout;
        // Connection slot claimed at admission, released once when the transport retires.
        private TcpConnectionPolicy? _slotOwner;
        private EmbedIO.Internal.ServerTelemetry.LifetimeTelemetry? _connectionTelemetry;
        private HttpListener? _lastListener;
        private string? _errorMessage;

        public HttpConnection(Socket sock, EndPointListener epl) : this(sock, epl, epl.Listener.ConnectionPolicy.HeaderTimeoutMilliseconds) { }
        internal HttpConnection(Socket sock, EndPointListener epl, TcpConnectionPolicy? slotOwner)
            : this(sock, epl, epl.Listener.ConnectionPolicy.HeaderTimeoutMilliseconds)
        {
            _slotOwner = slotOwner;
            // Admitted connections are counted until their transport retires.
            _connectionTelemetry = epl.Listener.Telemetry?.ConnectionStarted("tcp", LocalEndPoint.Port);
        }
        internal HttpConnection(Socket sock, EndPointListener epl, int initialHeaderTimeout)
        {
            if (initialHeaderTimeout <= 0) throw new ArgumentOutOfRangeException(nameof(initialHeaderTimeout));
            _sTimeout = initialHeaderTimeout;
            var policy = epl.Listener.ConnectionPolicy;
            _keepAliveTimeout = policy.Http1KeepAliveMilliseconds;
            _http2IdleTimeout = policy.ConfiguredKeepAliveMilliseconds;
            KeepAliveHeader = policy.Http1KeepAliveHeader;
            _protocolSocket = sock;
            _epl = epl;
            IsSecure = epl.Secure;
            LocalEndPoint = (IPEndPoint)(sock.LocalEndPoint ?? throw new ArgumentException("The socket has no local endpoint.", nameof(sock)));
            RemoteEndPoint = (IPEndPoint)(sock.RemoteEndPoint ?? throw new ArgumentException("The socket has no remote endpoint.", nameof(sock)));

            // Commit protocol writes promptly; batching belongs to the HTTP writer.
            sock.NoDelay = true;
            Stream = new NetworkStream(sock, false);
            if (IsSecure)
            {
                Stream = new SslStream(Stream, false);
            }

            _lifetime = new TcpSessionLifetime(_connectionSync, sock, Stream, () => CloseTransport(false, true));
            Init();
            // Admission can queue initialization. Bound that wait as well as head/TLS reads.
            _lifetime.ArmDeadline(_sTimeout);
        }

        public int Reuses { get; private set; }

        // Value of the HTTP/1 Keep-Alive response header.
        internal string KeepAliveHeader { get; }

        internal bool IsDraining => _draining;

        public Stream Stream { get; }

        public IPEndPoint LocalEndPoint { get; }

        public IPEndPoint RemoteEndPoint { get; }

        public bool IsSecure { get; }

        public ListenerPrefix? Prefix { get; set; }

        public void Dispose()
        {
            ForceClose();
        }
        public async Task BeginReadRequest()
        {
            byte[] buffer;
            bool bufferedInput;
            try
            {
                lock (_connectionSync)
                {
                    if (_protocolEnded != 0) return;
                    buffer = _buffer ??= new byte[BufferSize];
                    bufferedInput = _pendingInput.Count > 0;
                    if (Reuses == 1) _sTimeout = _keepAliveTimeout;
                    if (Reuses != 0) _lifetime.ArmDeadline(_sTimeout);
                }
                var selection = await TcpProtocolInput.ReadAsync(Stream, buffer, bufferedInput,
                    Reuses == 0, IsSecure,
                    Stream is SslStream { IsAuthenticated: false } ? _epl.Listener.Certificate : null).ConfigureAwait(false);
                if (selection.IsHttp2)
                {
                    if (selection.Count == 0) await RunHttp2Async(Stream).ConfigureAwait(false);
                    else
                    {
                        using var replay = new PrefixReadStream(Stream, buffer, selection.Count);
                        await RunHttp2Async(replay).ConfigureAwait(false);
                    }
                    return;
                }
                await RunHttp1Async(selection.Count, selection.Buffered).ConfigureAwait(false);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                StopRequestTimer();
                CloseSocket();
            }
        }

        public RequestStream GetRequestStream(long contentLength, bool chunked = false)
        {
            lock (_connectionSync)
            {
                if (_iStream != null) return _iStream;
                if (_protocolEnded != 0) throw new ObjectDisposedException(nameof(HttpConnection));
                var pending = _pendingInput;
                var buffer = pending.Array ?? Array.Empty<byte>();
                _iStream = chunked ? new ChunkedRequestStream(Stream, buffer, pending.Offset, pending.Count)
                    : new RequestStream(Stream, buffer, pending.Offset, pending.Count, contentLength);
                _pendingInput = default;
                return _iStream;
            }
        }

#if NETSTANDARD2_0
        // Only public runtime API is queried: the older reference assembly does
        // not expose this method. No private TLS state or native interop is used.
        private static readonly Func<SslStream, Task>? ShutdownTls = GetTlsShutdown();
        private static Func<SslStream, Task>? GetTlsShutdown()
        {
            try
            {
                var method = typeof(SslStream).GetMethod("ShutdownAsync", Type.EmptyTypes);
                return method == null || method.ReturnType != typeof(Task) ? null
                    : (Func<SslStream, Task>)method.CreateDelegate(typeof(Func<SslStream, Task>));
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                // Unsupported public delegate binding disables only the additive
                // TLS handoff, never initialization of ordinary legacy listeners.
                return null;
            }
        }
#endif
        internal void BeginTunnel()
        {
#if NETSTANDARD2_0
            if (IsSecure && ShutdownTls == null)
                throw new NotSupportedException("This runtime has no public TLS send-shutdown API for HTTP/1 tunnels.");
#endif
            lock (_connectionSync)
            {
                if (_protocolEnded != 0 || _protocolSocket == null) throw new ObjectDisposedException(nameof(HttpConnection));
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
                if (_protocolSocket == null || _protocolEnded != 0) throw new ObjectDisposedException(nameof(HttpConnection));
                _protocolSocket.Shutdown(SocketShutdown.Send);
            }
        }

        internal Stream TakeUpgradeStream()
        {
            lock (_connectionSync)
            {
                if (_protocolEnded != 0 || _protocolSocket == null)
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

        public ResponseStream GetResponseStream()
        {
            lock (_connectionSync)
            {
                if (_oStream != null) return _oStream;
                // Request cleanup can acquire this no-op flush view after an
                // upgraded peer has retired the transport. Preserve that view;
                // actual output writes still use the output owner's guards.
                var owner = new ResponseStream(Stream, _context.HttpListenerResponse,
                    _context.Listener?.IgnoreWriteExceptions ?? true);
                _oStream = owner;
                return owner;
            }
        }

        internal void SetError(string message) => _errorMessage = message;

        internal void ForceClose()
        {
            Volatile.Write(ref _forceClosing, 1);
            try { CloseTransport(true); }
            finally
            {
                ResponseStream? output;
                lock (_connectionSync) { output = _oStream; _oStream = null; }
                // Retire the transport before completing the output owner so an
                // abort cannot manufacture headers or a final chunk.
                output?.Dispose();
            }
        }

        // Called under the session gate. A completed body-free request can be
        // between response completion and the actor's next-head transition.
        // It is no longer work owned by the retiring logical listener.
        private bool ReleaseIdleOwner(HttpListener owner)
        {
            if (_protocolEnded != 0 || _tunnel || _draining || _forceClosing != 0
                || (_contextBound && !(_responseEnded && _responseCanReuse && !_context.Request.HasEntityBody)))
                return false;
            // Admission and final-endpoint disposal arbitrate the transfer. If
            // disposal already sealed the endpoint, normal forced cleanup wins.
            if (!_epl.RetainIdleConnection(this)) return false;
            owner.RemoveConnection(this);
            _lastListener = null;
            return true;
        }

        internal Task DrainAsync() => DrainCoreAsync(null);

        private Task DrainCoreAsync(HttpListener? owner)
        {
            Http2Dispatcher? dispatcher;
            Task completion;
            bool close;
            lock (_connectionSync)
            {
                if (owner != null && !_epl.AdmissionStopped && !_http2)
                {
                    if (_lastListener != owner || ReleaseIdleOwner(owner)) return Task.CompletedTask;
                }
                completion = _lifetime.Completion;
                if (_lifetime.CloseFinished) return completion;
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
            if (Interlocked.Exchange(ref _responseFinishing, 1) != 0)
            {
                if (forceClose) ForceClose();
                return;
            }
            ResponseStream? output;
            try
            {
                lock (_connectionSync)
                {
                    if (_protocolEnded != 0) return;
                    // An unwritten ordinary response still owes its final head.
                    // Handoffs and forced termination owe no synthetic response.
                    if (_oStream == null && !forceClose && _forceClosing == 0 && !_tunnel
                        && _context.Response.StatusCode >= 200)
                        GetResponseStream();
                    output = _oStream;
                }
                // Output completion can call Response.Close again. Its published
                // claim above rejects recursion, and no application callback or
                // transport write runs while the connection gate is held.
                output?.Dispose();
            }
            catch
            {
                // A failed final head/output cannot leave the actor waiting for
                // completion or allow another request onto incomplete framing.
                CloseTransport(true);
                throw;
            }
            lock (_connectionSync)
            {
                if (ReferenceEquals(_oStream, output)) _oStream = null;
                if (_protocolEnded != 0) return;
                if (output != null && !output.OutputCompleted) _forceClosing = 1;
                var reusable = !_tunnel && !_draining && _forceClosing == 0
                    && _context.Request.KeepAlive && _context.Response.KeepAlive
                    && _context.Response.Headers["connection"] != "close";
                if (reusable) { EndResponse(true); return; }
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
            ResetResponseCompletion();
            _errorMessage = null;
            _context = new HttpListenerContext(this);
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
            HttpListenerContext context;
            lock (_connectionSync)
            {
                if (!_contextBound) return;
                context = _context;
                _contextBound = false;
            }
            _epl.UnbindContext(context);
        }

        private void CloseSocket() => CloseTransport(false);

        private void CloseTransport(bool shutdown, bool deadlineOnly = false)
        {
            CancellationTokenSource? protocolStop;
            HttpListener[] protocolListeners;
            RequestStream? input;
            TcpConnectionPolicy? slotOwner;
            EmbedIO.Internal.ServerTelemetry.LifetimeTelemetry? connectionTelemetry;
            string protocolVersion;
            lock (_connectionSync)
            {
                if (!_lifetime.TryRetire(deadlineOnly)) return;
                slotOwner = _slotOwner;
                _slotOwner = null;
                connectionTelemetry = _connectionTelemetry;
                _connectionTelemetry = null;
                protocolVersion = _http2 ? "2" : "1.1";
                _protocolSocket = null;
                _protocolEnded = 1;
                EndResponse(false);
                protocolStop = _http2Stop?.Value;
                protocolListeners = _http2Listeners == null ? Array.Empty<HttpListener>() : new List<HttpListener>(_http2Listeners.Keys).ToArray();
                _http2Listeners?.Clear();
                input = _iStream;
                _pendingInput = default;
                _iStream = null;
                _buffer = null;
                _headReader.Reset();
            }
            try { FinishRetirement(); }
            finally
            {
                slotOwner?.Release();
                connectionTelemetry?.Stop(protocolVersion);
            }

            void FinishRetirement() => _lifetime.FinishRetirement(shutdown, input, () =>
            {
                try
                {
                    try { protocolStop?.Cancel(); }
                    catch (Exception error) when (error is ObjectDisposedException or AggregateException) { }
                    foreach (var listener in protocolListeners) listener.RemoveConnection(this);
                }
                finally
                {
                    try { Unbind(); }
                    finally { RemoveConnection(); }
                }
            });
        }

        private void StopRequestTimer() => _lifetime.StopDeadline();
    }
}
