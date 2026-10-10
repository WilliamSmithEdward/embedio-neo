using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    // The connection-side owner of one request stream's field coding and errors.
    internal interface IHttp3RequestOwner
    {
        // Decodes a later field section (trailers) within the request lifetime.
        Task<HpackField[]> DecodeAsync(byte[] wire, CancellationToken token);
        // Applies the stream or connection error scope of a request failure.
        void Failed(Exception error);
    }

    internal interface IHttp3ExchangeOwner : IHttp3RequestOwner
    {
        byte[] Encode(HpackField[] fields);
    }

    // Pull directly from the request's QUIC stream. No independent body queue
    // releases transport flow-control credit ahead of application consumption.
    internal sealed class Http3RequestBody : Stream
    {
        private readonly Http3RequestStream _reader;
        private readonly long _streamId;
        private readonly IHttp3RequestOwner _owner;
        private bool _data;
        private bool _ended;
        private volatile bool _disposed;
        private volatile bool _inputAbandoned;
        private int _reading;
        internal Http3RequestBody(long streamId, Http3RequestStream reader, IHttp3RequestOwner owner)
        { _streamId = streamId; _reader = reader; _owner = owner; }
        public override bool CanRead => !_disposed && !_inputAbandoned;
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
            if (_inputAbandoned) throw new IOException("HTTP/3 request input was abandoned by a canceled read.");
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
                    var fields = await _owner.DecodeAsync(next.EncodedFields, cancellationToken).ConfigureAwait(false);
                    try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(0, true, fields, 0)); }
                    catch (Http2ProtocolException error) { throw new Http3StreamException(_streamId, 0x10e, error.Message); }
                    _reader.ConfirmTrailers();
                    Trailers = fields;
                }
            }
            // A canceled transport read may have consumed part of a frame and
            // System.Net.Quic aborts that direction. Preserve output, but never
            // resume the input parser from an uncertain boundary.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { _inputAbandoned = true; throw; }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _owner.Failed(error); throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
        // After the response, reports whether the input has ended, reading only
        // what the transport has already received. It never waits for the peer:
        // any DATA, trailer section, error or not-yet-arrived input returns false
        // and the owner abandons the remaining input as before. A read that could
        // not complete at once is returned as abandoned; the owner's read abort
        // completes it, and the body stays unreadable afterwards.
        internal bool TryEndWithoutWaiting(out Task? abandoned)
        {
            abandoned = null;
            if (_ended) return true;
            if (_disposed || _data) return false;
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) return false;
            var release = true;
            try
            {
                var next = _reader.TryReadEventWithoutWaiting(out abandoned);
                if (abandoned != null) { release = false; return false; }
                if (next?.Kind != Http3RequestEventKind.End) return false;
                _ended = true;
                return true;
            }
            catch (Exception error) when (error is IOException or InvalidOperationException) { return false; }
            finally { if (release) Volatile.Write(ref _reading, 0); }
        }
        protected override void Dispose(bool disposing) { _disposed = true; base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
