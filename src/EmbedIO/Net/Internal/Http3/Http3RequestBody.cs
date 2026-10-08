using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // Pull directly from the request's QUIC stream. No independent body queue
    // releases transport flow-control credit ahead of application consumption.
    internal sealed class Http3RequestBody : Stream
    {
        private readonly Http3RequestStream _reader;
        private readonly long _streamId;
        private readonly Func<byte[], CancellationToken, Task<HpackField[]>> _decode;
        private readonly Action<Exception> _failed;
        private bool _data;
        private bool _ended;
        private bool _disposed;
        private int _reading;
        internal Http3RequestBody(long streamId, Http3RequestStream reader,
            Func<byte[], CancellationToken, Task<HpackField[]>> decode, Action<Exception> failed)
        { _streamId = streamId; _reader = reader; _decode = decode; _failed = failed; }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        internal bool Ended => _ended;
        internal HpackField[] Trailers { get; private set; } = Array.Empty<HpackField>();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed) throw new ObjectDisposedException(nameof(Http3RequestBody));
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent HTTP/3 body reads.");
            try
            {
                if (count == 0 || _ended) return 0;
                while (true)
                {
                    if (_data)
                    {
                        var read = await _reader.ReadDataAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                        if (read != 0) return read;
                        _data = false;
                    }
                    var next = await _reader.ReadEventAsync(cancellationToken).ConfigureAwait(false);
                    if (next.Kind == Http3RequestEventKind.End) { _ended = true; return 0; }
                    if (next.Kind == Http3RequestEventKind.Data) { _data = true; continue; }
                    if (next.Kind != Http3RequestEventKind.Trailers || next.EncodedFields == null)
                        throw new Http3ProtocolException(0x105, "Unexpected field section in request body.");
                    var fields = await _decode(next.EncodedFields, cancellationToken).ConfigureAwait(false);
                    try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(0, true, fields, 0)); }
                    catch (Http2ProtocolException error) { throw new Http3StreamException(_streamId, 0x10e, error.Message); }
                    _reader.ConfirmTrailers();
                    Trailers = fields;
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed(error); throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
