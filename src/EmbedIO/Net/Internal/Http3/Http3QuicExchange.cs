#if NET10_0_OR_GREATER
using System;
using System.Globalization;
using System.IO;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3QuicExchange : IDisposable
    {
        private readonly BorrowedResource<QuicStream> _stream;
        private readonly Http3RequestStream _reader;
        private readonly Func<int> _peerFieldLimit;
        private readonly Action<Exception> _failed;
        private readonly SemaphoreSlim _output = new(1, 1);
        private readonly byte[] _frameHeader = new byte[16];
        private bool _headers;
        private bool _tunnel;
        private bool _ended;
        private bool _bodyAllowed = true;
        private bool _outputFailed;
        private long? _length;
        private long _sent;
        private int _disposed;
        internal Http3QuicExchange(QuicStream stream, Http3RequestStream reader, Http2RequestHeaders request,
            Func<byte[], CancellationToken, Task<HpackField[]>> decode, Func<int> peerFieldLimit, Action<Exception> failed, CancellationToken token)
        {
            _stream = new BorrowedResource<QuicStream>(stream); _reader = reader; Request = request;
            _peerFieldLimit = peerFieldLimit; _failed = failed; CancellationToken = token;
            Body = new Http3RequestBody(stream.Id, reader, decode, failed);
        }
        public long Id => _stream.Value.Id;
        public Http2RequestHeaders Request { get; }
        public Stream InputStream => Body;
        public CancellationToken CancellationToken { get; }
        internal Http3RequestBody Body { get; }
        internal bool Ended => _ended;

        internal async Task RespondAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var end = bytes.Length == 0 || Request.Method == "HEAD";
            await SendHeadersAsync(new[] { new HpackField(":status", "200"), new HpackField("content-length", bytes.Length.ToString(CultureInfo.InvariantCulture)) }, end, token).ConfigureAwait(false);
            if (!end) await WriteAsync(bytes, 0, bytes.Length, true, token).ConfigureAwait(false);
        }
        internal async Task SendHeadersAsync(HpackField[] fields, bool endStream, CancellationToken token)
        {
            await _output.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (_headers) throw new InvalidOperationException("Final response headers already sent.");
                var response = Http2ResponseHeaders.Validate(fields, Request.Method, endStream);
                var encoded = QpackEncoder.Encode(fields, 65536, _peerFieldLimit());
                await FrameAsync(1, encoded, endStream, token).ConfigureAwait(false);
                if (response.Status >= 200)
                {
                    _headers = true; _bodyAllowed = response.BodyAllowed; _length = response.ContentLength;
                    if (Request.Method == "CONNECT" && response.Status < 300) { _tunnel = true; _reader.EnterTunnel(); }
                }
                if (endStream) _ended = true;
            }
            finally { _output.Release(); }
        }
        internal async Task WriteAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await _output.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (!_headers || (!_bodyAllowed && count != 0)) throw new InvalidOperationException("Response cannot contain DATA.");
                if (count > long.MaxValue - _sent || (_bodyAllowed && _length.HasValue && count > _length.Value - _sent))
                    throw new InvalidDataException("Response exceeds Content-Length.");
                if (endStream && _bodyAllowed && _length.HasValue && _sent + count != _length.Value)
                    throw new InvalidDataException("Response does not match Content-Length.");
                if (count != 0) await FrameAsync(0, bytes.AsMemory(offset, count), endStream, token).ConfigureAwait(false);
                else if (endStream) _stream.Value.CompleteWrites();
                _sent += count;
                if (endStream) _ended = true;
            }
            catch (Exception error) when (error is QuicException or OperationCanceledException) { _outputFailed = true; _failed(error); throw; }
            finally { _output.Release(); }
        }
        internal async Task SendTrailersAsync(HpackField[] fields, CancellationToken token)
        {
            await _output.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (!_headers || !_bodyAllowed || _tunnel)
                    throw new InvalidOperationException("Response cannot contain trailers.");
                if (_length.HasValue && _sent != _length.Value) throw new InvalidDataException("Response does not match Content-Length.");
                try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(0, true, fields, 0)); }
                catch (Http2ProtocolException error) { throw new InvalidDataException(error.Message, error); }
                var encoded = QpackEncoder.Encode(fields, 65536, _peerFieldLimit());
                await FrameAsync(1, encoded, true, token).ConfigureAwait(false);
                _ended = true;
            }
            finally { _output.Release(); }
        }
        internal Task CompleteAsync(CancellationToken token) => _headers
            ? WriteAsync(Array.Empty<byte>(), 0, 0, true, token) : RespondAsync(Array.Empty<byte>(), token);
        private void CheckWritable()
        {
            if (_disposed != 0 || _outputFailed || _ended) throw new InvalidOperationException("HTTP/3 response is no longer writable.");
        }
        private async Task FrameAsync(long type, ReadOnlyMemory<byte> payload, bool endStream, CancellationToken token)
        {
            var size = QuicInteger.Write(_frameHeader, 0, type);
            size += QuicInteger.Write(_frameHeader, size, payload.Length);
            try
            {
                await _stream.Value.WriteAsync(_frameHeader.AsMemory(0, size), false, token).ConfigureAwait(false);
                await _stream.Value.WriteAsync(payload, endStream, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _outputFailed = true; _failed(error); throw; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Body.Dispose(); _output.Dispose();
        }
    }
}
#endif
