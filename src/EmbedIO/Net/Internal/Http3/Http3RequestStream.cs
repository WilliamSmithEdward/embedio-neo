using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    internal sealed class Http3StreamException : IOException
    {
        internal Http3StreamException(long streamId, long errorCode, string message) : base(message)
        { StreamId = streamId; ErrorCode = errorCode; }
        public long StreamId { get; }
        public long ErrorCode { get; }
    }

    internal enum Http3RequestEventKind { Headers, Data, Trailers, End }
    internal readonly struct Http3RequestEvent
    {
        internal Http3RequestEvent(Http3RequestEventKind kind, byte[]? encodedFields = null, long length = 0)
        { Kind = kind; EncodedFields = encodedFields; Length = length; }
        public Http3RequestEventKind Kind { get; }
        public byte[]? EncodedFields { get; }
        public long Length { get; }
    }

    // Owns request-stream framing, not the transport. The connection decodes and
    // validates each field section before confirming it, so a blocked QPACK
    // section prevents further reads. DATA remains in the transport until read.
    internal sealed class Http3RequestStream
    {
        private enum State { BeforeHeaders, PendingHeaders, Body, PendingTrailers, Trailers, Ended }
        private readonly Http3FrameReader _reader;
        private readonly long _streamId;
        private readonly int _maximumMetadata;
        private readonly long _maximumBody;
        private State _state;
        private long? _expectedLength;
        private long _bodyBytes;
        private bool _data;
        private bool _headersAccepted;
        private int _tunnel;
        private int _reading;
        private bool _failed;

        internal Http3RequestStream(long streamId, Stream source, int maximumMetadata, long maximumBody)
        {
            if (streamId < 0 || streamId > QuicInteger.Maximum || (streamId & 3) != 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            if (maximumMetadata < 0) throw new ArgumentOutOfRangeException(nameof(maximumMetadata));
            if (maximumBody < 0) throw new ArgumentOutOfRangeException(nameof(maximumBody));
            _reader = new Http3FrameReader(source);
            _streamId = streamId;
            _maximumMetadata = maximumMetadata;
            _maximumBody = maximumBody;
        }
        public long BodyBytes => Interlocked.Read(ref _bodyBytes);

        private void Enter(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent HTTP/3 request reads.");
            if (_failed) { Volatile.Write(ref _reading, 0); throw new IOException("HTTP/3 request input is no longer usable."); }
        }
        internal async Task<Http3RequestEvent> ReadEventAsync(CancellationToken token)
        {
            Enter(token);
            try
            {
                if (_state == State.PendingHeaders || _state == State.PendingTrailers)
                    throw new InvalidOperationException("Decode and validate the pending field section before reading further.");
                if (_reader.Remaining != 0) throw new InvalidOperationException("Consume the current DATA frame before reading further.");
                _data = false;
                if (_state == State.Ended) return new Http3RequestEvent(Http3RequestEventKind.End);
                while (true)
                {
                    var header = await _reader.ReadHeaderAsync(token).ConfigureAwait(false);
                    if (!header.HasValue)
                    {
                        if (_state == State.BeforeHeaders) throw StreamError(0x10d, "Request ended before its initial headers.");
                        ValidateLength();
                        _state = State.Ended;
                        return new Http3RequestEvent(Http3RequestEventKind.End);
                    }
                    var type = header.Value.Type;
                    if (type == 0)
                    {
                        if (_state != State.Body) throw Unexpected();
                        if (header.Value.Length > _maximumBody - _bodyBytes) throw StreamError(0x107, "Request DATA exceeds the configured body budget.");
                        if (Volatile.Read(ref _tunnel) == 0 && _expectedLength.HasValue && header.Value.Length > _expectedLength.Value - _bodyBytes)
                            throw StreamError(0x10e, "Request DATA exceeds Content-Length.");
                        _data = true;
                        return new Http3RequestEvent(Http3RequestEventKind.Data, length: header.Value.Length);
                    }
                    if (type == 1)
                    {
                        if (Volatile.Read(ref _tunnel) != 0 || (_state != State.BeforeHeaders && _state != State.Body)) throw Unexpected();
                        if (header.Value.Length > _maximumMetadata)
                            throw StreamError(0x107, "Request HEADERS exceeds the configured metadata budget.");
                        var initial = _state == State.BeforeHeaders;
                        var encoded = await _reader.ReadBufferedPayloadAsync(_maximumMetadata, token).ConfigureAwait(false);
                        _state = initial ? State.PendingHeaders : State.PendingTrailers;
                        return new Http3RequestEvent(initial ? Http3RequestEventKind.Headers : Http3RequestEventKind.Trailers, encoded);
                    }
                    // Core control frames, client PUSH_PROMISE, HTTP/2 reserved
                    // types and RFC 9218 control-stream priority updates.
                    if (type == 2 || type == 3 || type == 4 || type == 5 || type == 6 || type == 7 || type == 8 || type == 9 || type == 13
                        || type == 0xf0700 || type == 0xf0701) throw Unexpected();
                    await _reader.SkipPayloadAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        internal void ConfirmHeaders(long? contentLength)
        {
            if (contentLength < 0) throw new ArgumentOutOfRangeException(nameof(contentLength));
            Enter(CancellationToken.None);
            try
            {
                if (_state != State.PendingHeaders) throw new InvalidOperationException("No initial field section is pending.");
                if (contentLength > _maximumBody) throw StreamError(0x107, "Declared body exceeds the configured budget.");
                _expectedLength = contentLength;
                _state = State.Body;
                Volatile.Write(ref _headersAccepted, true);
            }
            catch (IOException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        internal void ConfirmTrailers()
        {
            Enter(CancellationToken.None);
            try
            {
                if (_state != State.PendingTrailers) throw new InvalidOperationException("No trailer section is pending.");
                ValidateLength();
                _state = State.Trailers;
            }
            catch (IOException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        // Called only when a CONNECT 2xx response commits the tunnel. The sender
        // may transition while the independent receive side awaits more data.
        internal void EnterTunnel()
        {
            if (!Volatile.Read(ref _headersAccepted)) throw new InvalidOperationException("Initial headers have not been accepted.");
            Volatile.Write(ref _tunnel, 1);
        }
        internal async Task<int> ReadDataAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Enter(token);
            try
            {
                if (!_data) throw new InvalidOperationException("No DATA frame is selected.");
                var read = await _reader.ReadPayloadAsync(bytes, offset, count, token).ConfigureAwait(false);
                Interlocked.Add(ref _bodyBytes, read);
                return read;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        private void ValidateLength()
        {
            if (Volatile.Read(ref _tunnel) == 0 && _expectedLength.HasValue && _bodyBytes != _expectedLength.Value)
                throw StreamError(0x10e, "Request DATA does not match Content-Length.");
        }
        private Http3StreamException StreamError(long code, string message) => new(_streamId, code, message);
        private static Http3ProtocolException Unexpected() => new(0x105, "Frame is forbidden at this request-stream position.");
    }
}
