#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    // Replays bytes already read from a request stream, then reads the stream itself.
    // A connection serving WebTransport reads the first integer of each client
    // bidirectional stream to tell sessions' streams from requests.
    internal sealed class Http3PrefixedStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Func<Memory<byte>, CancellationToken, ValueTask<int>> _read;
        private int _offset;

        // The stream stays owned by its caller; this view only reads from it.
        internal Http3PrefixedStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _read = inner.ReadAsync;
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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var remaining = _prefix.Length - _offset;

            if (remaining == 0 || buffer.IsEmpty) return _read(buffer, cancellationToken);

            var count = Math.Min(remaining, buffer.Length);
            _prefix
                .AsSpan(_offset, count)
                .CopyTo(buffer.Span);
            _offset += count;
            return new ValueTask<int>(count);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#endif
