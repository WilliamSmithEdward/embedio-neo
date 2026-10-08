using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal sealed partial class HttpConnection : IDisposable
    {
        private const int BufferSize = 8192;
        private static readonly Encoding HeaderEncoding = Encoding.GetEncoding(28591);
        private int _headerBytes;
        private int _responseFinishing;

        private readonly Timer _timer;
        private readonly object _connectionSync = new();
        private int _forceClosing;
        private int _resourcesDisposed;
        private readonly EndPointListener _epl;
        private Socket? _sock;
        private MemoryStream? _ms;
        private byte[]? _buffer;
        private HttpListenerContext _context;
        private StringBuilder? _currentLine;
        private RequestStream? _iStream;
        private ResponseStream? _oStream;
        private bool _contextBound;
        private int _sTimeout = 90000; // 90k ms for first request, 15k ms from then on
        private HttpListener? _lastListener;
        private InputState _inputState = InputState.RequestLine;
        private LineState _lineState = LineState.None;
        private int _position;
        private string? _errorMessage;

        public HttpConnection(Socket sock, EndPointListener epl)
        {
            _sock = sock;
            _epl = epl;
            IsSecure = epl.Secure;
            LocalEndPoint = (IPEndPoint)sock.LocalEndPoint;
            RemoteEndPoint = (IPEndPoint)sock.RemoteEndPoint;

            Stream = new NetworkStream(sock, false);
            if (IsSecure)
            {
                Stream = new SslStream(Stream, false);
            }

            _timer = new Timer(OnTimeout, null, Timeout.Infinite, Timeout.Infinite);
            _context = null!; // Silence warning about uninitialized field - _context will be initialized by the Init method
            Init();
        }

        public int Reuses { get; private set; }

        public Stream Stream { get; }

        public IPEndPoint LocalEndPoint { get; }

        public IPEndPoint RemoteEndPoint { get; }

        public bool IsSecure { get; }

        public ListenerPrefix? Prefix { get; set; }

        public void Dispose()
        {
            try { Close(true); }
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
                    bufferedInput = _ms != null && _ms.Length > 0;
                    if (Reuses == 1) _sTimeout = 15000;
                    _ = _timer.Change(_sTimeout, Timeout.Infinite);
                }
                // Authenticate outside the socket accept callback. The request timer also
                // bounds a client that connects without completing its TLS handshake.
                if (Stream is SslStream sslStream && !sslStream.IsAuthenticated)
                {
                    await sslStream.AuthenticateAsServerAsync(_epl.Listener.Certificate,
                        false, SslProtocols.None, false).ConfigureAwait(false);
                }

                var data = bufferedInput ? 0 : await Stream.ReadAsync(buffer, 0, BufferSize).ConfigureAwait(false);
                await OnReadInternal(data, bufferedInput).ConfigureAwait(false);
            }
            catch
            {
                StopRequestTimer();
                CloseSocket();
            }
        }

        public RequestStream GetRequestStream(long contentLength, bool chunked = false)
        {
            if (_iStream == null)
            {
                var buffer = _ms.GetBuffer();
                var length = (int)_ms.Length;
                _ms = null;

                _iStream = chunked ? new ChunkedRequestStream(Stream, buffer, _position, length - _position)
                    : new RequestStream(Stream, buffer, _position, length - _position, contentLength);
            }

            return _iStream;
        }

        public ResponseStream GetResponseStream() => _oStream ??= new ResponseStream(Stream, _context.HttpListenerResponse, _context.Listener?.IgnoreWriteExceptions ?? true);

        internal void SetError(string message) => _errorMessage = message;

        internal void ForceClose() => Close(true);

        internal void Close(bool forceClose = false)
        {
            if (forceClose) Volatile.Write(ref _forceClosing, 1);
            if (!forceClose && Interlocked.Exchange(ref _responseFinishing, 1) != 0) return;
            if (_sock != null)
            {
                // Dispose may call Response.Close recursively. A forced close is
                // recorded first so that callback cannot restart the request reader.
                _oStream?.Dispose();
                _oStream = null;
            }
            if (_sock == null) return;

            if (Volatile.Read(ref _forceClosing) == 0 && _context.Request.KeepAlive
                && _context.Response.Headers["connection"] != "close")
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
            catch { CloseTransport(true); }
        }

        private void RestartRequest()
        {
            var restart = false;
            lock (_connectionSync)
            {
                if (_sock != null && _resourcesDisposed == 0 && _forceClosing == 0)
                {
                    var pending = _iStream != null ? _iStream.BufferedRemainder
                        : _ms != null ? new ArraySegment<byte>(_ms.GetBuffer(), _position, (int)_ms.Length - _position)
                        : default;
                    var previousBuffer = _ms;
                    Reuses++;
                    Unbind();
                    InitWithPendingInput(pending);
                    previousBuffer?.Dispose();
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
        private void Init() => InitWithPendingInput(default);

        private void InitWithPendingInput(ArraySegment<byte> pending)
        {
            _contextBound = false;
            _iStream = null;
            _oStream = null;
            Prefix = null;
            // Adopt immutable unread bytes instead of copying the tail for each pipelined request.
            // The preceding body is complete and cannot expose this storage again.
            if (pending.Count > 0)
            {
                _ms = new MemoryStream(pending.Array!, 0, pending.Array!.Length, true, true);
                _ms.SetLength(pending.Offset + pending.Count);
                _ms.Position = _ms.Length;
                _position = pending.Offset;
            }
            else { _ms = new MemoryStream(); _position = 0; }
            _headerBytes = 0;
            _responseFinishing = 0;
            _errorMessage = null;
            _inputState = InputState.RequestLine;
            _lineState = LineState.None;
            _context = new HttpListenerContext(this);
        }

        private void OnTimeout(object unused)
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
                try
                {
                    if (offset > 0)
                    {
                        if (_ms.Capacity < offset)
                        {
                            _ms.Dispose();
                            _ms = new MemoryStream(Math.Max(offset, 256));
                        }
                        _ms.Write(_buffer, 0, offset);
                    }
                }
                catch
                {
                    CloseSocket();
                    return;
                }

                if (offset == 0 && !bufferedInput)
                {
                    CloseSocket();
                    return;
                }

                bufferedInput = false;
                if (ProcessInput(_ms))
                {
                    StopRequestTimer();
                    if (_errorMessage is null)
                    {
                        _context.HttpListenerRequest.FinishInitialization();
                    }

                    if (_errorMessage != null || !_epl.BindContext(_context))
                    {
                        Close(true);
                        return;
                    }

                    var listener = _context.Listener;
                    if (_lastListener != listener)
                    {
                        RemoveConnection();
                        listener.AddConnection(this);
                        _lastListener = listener;
                    }

                    _contextBound = true;
                    listener.RegisterContext(_context);
                    return;
                }

                offset = await Stream.ReadAsync(_buffer, 0, BufferSize).ConfigureAwait(false);
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
        private bool ProcessInput(MemoryStream ms)
        {
            var buffer = ms.GetBuffer();
            var len = (int)ms.Length;
            var used = 0;

            while (true)
            {
                if (_errorMessage != null)
                {
                    return true;
                }

                if (_position >= len)
                {
                    break;
                }

                string? line;
                try
                {
                    line = ReadLine(buffer, _position, len - _position, out used);
                    _position += used;
                    _headerBytes += used;
                    if (_headerBytes > 32768) throw new InvalidDataException("Request headers exceed 32768 bytes.");
                }
                catch
                {
                    _errorMessage = "Bad request";
                    return true;
                }

                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrEmpty(line))
                {
                    if (_inputState == InputState.RequestLine)
                    {
                        continue;
                    }

                    _currentLine = null;

                    return true;
                }

                if (_inputState == InputState.RequestLine)
                {
                    _context.HttpListenerRequest.SetRequestLine(line);
                    _inputState = InputState.Headers;
                }
                else
                {
                    try
                    {
                        _context.HttpListenerRequest.AddHeader(line);
                    }
                    catch (Exception e)
                    {
                        _errorMessage = e.Message;
                        return true;
                    }
                }
            }

            // Incomplete input has been fully consumed into the partial line.
            // Reuse its storage; the next read must not grow with fragmentation.
            ms.SetLength(0);
            ms.Position = 0;
            _position = 0;

            return false;
        }

        private string? ReadLine(byte[] buffer, int offset, int len, out int used)
        {
            used = 0;
            // A complete line in the receive buffer requires only its final string,
            // not an intermediate char buffer and a per-byte append loop.
            if (_currentLine == null && _lineState == LineState.None)
            {
                var end = Array.IndexOf(buffer, (byte)13, offset, len);
                var lf = Array.IndexOf(buffer, (byte)10, offset, len);
                if (lf >= 0 && (end < 0 || lf < end)) throw new InvalidDataException("Bare LF in request headers.");
                if (end >= 0 && end + 1 < offset + len)
                {
                    if (buffer[end + 1] != 10) throw new InvalidDataException("Invalid request line ending.");
                    used = end - offset + 2;
                    return HeaderEncoding.GetString(buffer, offset, end - offset);
                }
            }
            _currentLine ??= new StringBuilder(128);
            for (var i = offset; i < offset + len; i++)
            {
                used++;
                var value = buffer[i];
                if (_lineState == LineState.Cr)
                {
                    if (value != 10) throw new InvalidDataException("Invalid request line ending.");
                    _lineState = LineState.None;
                    var result = _currentLine.ToString();
                    _currentLine = null;
                    return result;
                }
                if (value == 10) throw new InvalidDataException("Bare LF in request headers.");
                if (value == 13) _lineState = LineState.Cr;
                else _currentLine.Append((char)value);
            }
            return null;
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
            lock (_connectionSync)
            {
                socket = _sock;
                _sock = null;
            }
            if (socket == null) return;

            try
            {
                if (shutdown)
                {
                    try { socket.Shutdown(SocketShutdown.Both); }
                    catch { /* A disconnected socket has nothing left to shut down. */ }
                }
            }
            finally
            {
                socket.Dispose();
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
            MemoryStream? buffered;
            RequestStream? input;
            lock (_connectionSync)
            {
                if (_resourcesDisposed != 0) return;
                _resourcesDisposed = 1;
                buffered = _ms;
                input = _iStream;
                _ms = null;
                _iStream = null;
                _buffer = null;
                _currentLine = null;
            }
            _timer.Dispose();
            buffered?.Dispose();
            input?.Dispose();
            Stream.Dispose();
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
