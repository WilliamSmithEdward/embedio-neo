using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal class RequestStream : Stream
    {
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _transport;
        private Stream Transport => _transport.Value;
        private readonly byte[] _buffer;
        private int _offset;
        private int _length;
        private long _remainingBody;

        internal RequestStream(Stream stream, byte[] buffer, int offset, int length, long contentLength = -1)
        {
            _transport = new EmbedIO.Internal.BorrowedResource<Stream>(stream);
            _buffer = buffer;
            _offset = offset;
            _length = length;
            _remainingBody = contentLength;
        }

        internal virtual bool IsBodyConsumed => _remainingBody == 0;

        // Only bytes beyond the completed body belong to the next request.
        internal virtual ArraySegment<byte> BufferedRemainder
            => IsBodyConsumed ? new ArraySegment<byte>(_buffer, _offset, _length) : default;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read([In, Out] byte[] buffer, int offset, int count)
        {
            // Call FillFromBuffer to check for buffer boundaries even when remaining_body is 0
            var nread = FillFromBuffer(buffer, offset, count);

            if (nread == -1 || count == 0)
            {
                // No bytes requested or Content-Length reached.
                return 0;
            }

            if (nread > 0)
            {
                return nread;
            }

            if (_remainingBody > 0)
            {
                count = (int)Math.Min(count, _remainingBody);
            }

            nread = Transport.Read(buffer, offset, count);

            if (nread > 0 && _remainingBody > 0)
            {
                _remainingBody -= nread;
            }

            return nread;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Validate even when canceled, but do not consume buffered input on cancellation.
            ValidateDestination(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<int>(cancellationToken);
            var read = FillFromBuffer(buffer, offset, count);
            if (read != 0 || count == 0) return Task.FromResult(Math.Max(read, 0));
            return ReadTransportAsync(buffer, offset, count, cancellationToken);
        }

        private async Task<int> ReadTransportAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (_remainingBody > 0) count = (int)Math.Min(count, _remainingBody);
            var read = await Transport.ReadAsync(buffer, offset, count, token).ConfigureAwait(false);
            if (read > 0 && _remainingBody > 0) _remainingBody -= read;
            return read;
        }

#if NET10_0_OR_GREATER
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<int>(cancellationToken);
            if (_remainingBody == 0 || buffer.Length == 0) return new ValueTask<int>(0);
            if (_length > 0)
            {
                var count = Math.Min(buffer.Length, _length);
                if (_remainingBody > 0) count = (int)Math.Min(count, _remainingBody);
                _buffer.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                _length -= count;
                if (_remainingBody > 0) _remainingBody -= count;
                return new ValueTask<int>(count);
            }
            return ReadTransportAsync(buffer, cancellationToken);
        }

        private async ValueTask<int> ReadTransportAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (_remainingBody > 0 && buffer.Length > _remainingBody) buffer = buffer.Slice(0, (int)_remainingBody);
            var read = await Transport.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read > 0 && _remainingBody > 0) _remainingBody -= read;
            return read;
        }
#endif

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // Returns 0 if we can keep reading from the base stream,
        // > 0 if we read something from the buffer.
        // -1 if we had a content length set and we finished reading that many bytes.
        protected static void ValidateDestination(byte[] buffer, int off, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (off < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(off), "< 0");
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "< 0");
            }

            var len = buffer.Length;

            if (off > len)
            {
                throw new ArgumentException("destination offset is beyond array size");
            }

            if (off > len - count)
            {
                throw new ArgumentException("Reading would overrun buffer");
            }

        }

        private int FillFromBuffer(byte[] buffer, int off, int count)
        {
            ValidateDestination(buffer, off, count);
            if (_remainingBody == 0)
            {
                return -1;
            }

            if (_length == 0)
            {
                return 0;
            }

            var size = Math.Min(_length, count);
            if (_remainingBody > 0)
            {
                size = (int)Math.Min(size, _remainingBody);
            }

            if (_offset > _buffer.Length - size)
            {
                size = Math.Min(size, _buffer.Length - _offset);
            }

            if (size == 0)
            {
                return 0;
            }

            Buffer.BlockCopy(_buffer, _offset, buffer, off, size);
            _offset += size;
            _length -= size;
            if (_remainingBody > 0)
            {
                _remainingBody -= size;
            }

            return size;
        }
    }
}
