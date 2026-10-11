using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Counts decoded bytes and probes EOF at the exact configured boundary.
    internal sealed class LimitedRequestStream : Stream
    {
        private readonly Stream _source;
        private readonly long _maximum;
        private long _read;
        private int _reading;
        private int _disposed;
        private bool _exceeded;
        internal LimitedRequestStream(Stream source, long maximum)
        {
            _source = source;
            _maximum = maximum;
        }
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void Check()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(LimitedRequestStream));
            if (_exceeded) throw LimitError();
        }
        private void Enter()
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent request body reads.");
        }
        private HttpException LimitError() => new(413, "Decompressed request body exceeds the configured byte limit.");
        private int Requested(int count)
        {
            var remaining = _maximum - _read;
            return remaining >= count ? count : (int)remaining + 1;
        }
        private int Account(int count)
        {
            if (count > _maximum - _read) { _exceeded = true; throw LimitError(); }
            _read += count;
            return count;
        }
        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(offset));
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count);
            Enter();
            try { Check(); return count == 0 ? 0 : Account(_source.Read(buffer, offset, Requested(count))); }
            finally { Volatile.Write(ref _reading, 0); }
        }
        public override int ReadByte()
        {
            Enter();
            try
            {
                Check();
                var value = _source.ReadByte();
                if (value >= 0) Account(1);
                return value;
            }
            finally { Volatile.Write(ref _reading, 0); }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Validate(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();
            Enter();
            try { Check(); return count == 0 ? 0 : Account(await _source.ReadAsync(buffer, offset, Requested(count), cancellationToken).ConfigureAwait(false)); }
            finally { Volatile.Write(ref _reading, 0); }
        }
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            Enter();
            try { Check(); return buffer.IsEmpty ? 0 : Account(_source.Read(buffer[..Requested(buffer.Length)])); }
            finally { Volatile.Write(ref _reading, 0); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enter();
            try { Check(); return buffer.IsEmpty ? 0 : Account(await _source.ReadAsync(buffer[..Requested(buffer.Length)], cancellationToken).ConfigureAwait(false)); }
            finally { Volatile.Write(ref _reading, 0); }
        }
#endif
        public override void Flush() => Check();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
