using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // An HTTP/1 body borrows the connection transport and its read-ahead. Its
    // declared length bounds every transport read; following request bytes are
    // handed back only after this body completes. Chunked framing specializes
    // this internal stream contract rather than using the fixed-length reader.
    internal class RequestStream : Stream
    {
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _connectionInput;
        private readonly byte[] _readAheadBytes;
        private readonly int _readEnd;
        private int _readCursor;
        private long _bytesLeft;
        private ExceptionDispatchInfo? _bodyFailure;

        internal RequestStream(Stream stream, byte[] buffer, int offset, int length, long contentLength = -1)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            ValidateDestination(buffer, offset, length);
            if (contentLength < -1) throw new ArgumentOutOfRangeException(nameof(contentLength));
            _connectionInput = new EmbedIO.Internal.BorrowedResource<Stream>(stream);
            _readAheadBytes = buffer;
            _readCursor = offset;
            _readEnd = offset + length;
            _bytesLeft = contentLength;
        }

        internal virtual bool IsBodyConsumed => _bytesLeft == 0;

        internal virtual ArraySegment<byte> BufferedRemainder => IsBodyConsumed
            ? new ArraySegment<byte>(_readAheadBytes, _readCursor, _readEnd - _readCursor) : default;
        internal bool HasBodyFramingFailure => _bodyFailure != null;
        protected bool HasFramingFailure => HasBodyFramingFailure;

        protected void RememberFramingError(Exception error) => _bodyFailure = ExceptionDispatchInfo.Capture(error);

        internal bool IsFramingError(Exception error)
        {
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
                if (ReferenceEquals(cause, _bodyFailure?.SourceException)) return true;
            return false;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateDestination(buffer, offset, count);
            var limit = ReadLimit(count);
            if (limit == 0) return 0;
            var buffered = CopyReadAhead(buffer, offset, limit);
            return buffered != 0 ? buffered
                : AccountTransportRead(_connectionInput.Value.Read(buffer, offset, limit));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateDestination(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
            var limit = ReadLimit(count);
            if (limit == 0) return Task.FromResult(0);
            var buffered = CopyReadAhead(buffer, offset, limit);
            return buffered != 0 ? Task.FromResult(buffered)
                : ReadConnectionAsync(buffer, offset, limit, cancellationToken);
        }

        private async Task<int> ReadConnectionAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => AccountTransportRead(await _connectionInput.Value.ReadAsync(buffer, offset, count, token).ConfigureAwait(false));

#if NET10_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            var limit = ReadLimit(buffer.Length);
            if (limit == 0) return new ValueTask<int>(0);
            var buffered = Math.Min(limit, _readEnd - _readCursor);
            if (buffered != 0)
            {
                _readAheadBytes.AsMemory(_readCursor, buffered).CopyTo(buffer);
                AccountReadAhead(buffered);
                return new ValueTask<int>(buffered);
            }
            return ReadConnectionAsync(buffer.Slice(0, limit), cancellationToken);
        }

        private async ValueTask<int> ReadConnectionAsync(Memory<byte> buffer, CancellationToken token)
            => AccountTransportRead(await _connectionInput.Value.ReadAsync(buffer, token).ConfigureAwait(false));
#endif

        private int ReadLimit(int requested)
        {
            if (requested == 0 || _bytesLeft == 0) return 0;
            _bodyFailure?.Throw();
            return _bytesLeft < 0 ? requested : (int)Math.Min(requested, _bytesLeft);
        }

        private int CopyReadAhead(byte[] destination, int offset, int limit)
        {
            var copied = Math.Min(limit, _readEnd - _readCursor);
            if (copied != 0)
            {
                Buffer.BlockCopy(_readAheadBytes, _readCursor, destination, offset, copied);
                AccountReadAhead(copied);
            }
            return copied;
        }

        private void AccountReadAhead(int count)
        {
            _readCursor += count;
            if (_bytesLeft >= 0) _bytesLeft -= count;
        }

        private int AccountTransportRead(int count)
        {
            if (count == 0 && _bytesLeft > 0)
            {
                try { throw new EndOfStreamException("Incomplete fixed-length request body."); }
                catch (EndOfStreamException failure)
                {
                    // Capture after the first throw has established its origin.
                    RememberFramingError(failure);
                    throw;
                }
            }
            if (_bytesLeft >= 0) _bytesLeft -= count;
            return count;
        }

        protected static void ValidateDestination(byte[] buffer, int off, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (off < 0) throw new ArgumentOutOfRangeException(nameof(off), "< 0");
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "< 0");
            if (off > buffer.Length) throw new ArgumentException("destination offset is beyond array size");
            if (count > buffer.Length - off) throw new ArgumentException("Reading would overrun buffer");
        }
    }
}
