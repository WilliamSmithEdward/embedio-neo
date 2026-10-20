using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    partial class MultipartReader
    {
        private async Task<int> ReadBodyAsync(
            PartStream part,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (part.Disposed) throw new ObjectDisposedException(nameof(MultipartSection.Body));

            if (!ReferenceEquals(part, _current))
                throw new InvalidOperationException("The reader has advanced past this part.");

            Enter();
            using var linked = Link(cancellationToken);

            try
            {
                var token = linked?.Token ?? _token;
                token.ThrowIfCancellationRequested();
                return count == 0
                    ? 0
                    : await ReadBodyCoreAsync(part, buffer, offset, count, token).ConfigureAwait(false);
            }

            catch
            {
                _faulted = true;
                throw;
            }

            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        private async Task<int> ReadBodyCoreAsync(
            PartStream part,
            byte[]? destination,
            int offset,
            int count,
            CancellationToken token
        )
        {
            token.ThrowIfCancellationRequested();

            if (part.Ended) return 0;

            while (true)
            {
                var candidate = FindBoundary();

                if (candidate == _start)
                {
                    _start += _marker.Length;
                    var tail = await ReadLineAsync(_options.MaxHeaderBytes, true, token).ConfigureAwait(false);
                    var suffix = tail.TrimEnd(' ', '\t');

                    if (suffix == "--") _closing = true;

                    else if (suffix.Length != 0 || _eof) throw Malformed();

                    part.Ended = true;
                    return 0;
                }

                var available = candidate >= 0 ? candidate - _start : _end - _start - (_marker.Length + 1);

                if (available > 0)
                {
                    var length = Math.Min(count, available);

                    if (length > part.Limit - part.Bytes) throw Limit();

                    if (destination != null) Buffer.BlockCopy(_buffer, _start, destination, offset, length);

                    _start += length;
                    part.Bytes += length;
                    return length;
                }

                if (!await FillAsync(token).ConfigureAwait(false)) throw Malformed();
            }
        }

        private int FindBoundary()
        {
            for (var i = _start; i <= _end - _marker.Length - 2; i++)
            {
                var matched = true;

                for (var j = 0; j < _marker.Length; j++)
                {
                    if (_buffer[i + j] == _marker[j]) continue;

                    matched = false;
                    break;
                }

                if (!matched) continue;

                var suffix = i + _marker.Length;
                var first = _buffer[suffix];

                if (
                    (first == '-' && _buffer[suffix + 1] == '-')
                    || (first == '\r' && _buffer[suffix + 1] == '\n')
                    || first == ' '
                    || first == '\t'
                ) return i;
            }

            return -1;
        }

        private sealed class PartStream : Stream
        {
            private readonly MultipartReader _owner;

            internal PartStream(MultipartReader owner, long limit)
            {
                _owner = owner;
                Limit = limit;
            }

            internal long Limit { get; }
            internal long Bytes { get; set; }
            internal bool Ended { get; set; }
            internal bool Disposed { get; private set; }
            public override bool CanRead => !Disposed && Volatile.Read(ref _owner._disposed) == 0;
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

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            ) => _owner.ReadBodyAsync(this, buffer, offset, count, cancellationToken);

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
