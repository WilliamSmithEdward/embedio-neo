using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // One reusable, bounded record. Serializer/converter allocations are outside this buffer's ownership.
    internal sealed class JsonRecordBuffer : Stream
    {
        private readonly int _limit;
        private byte[] _buffer = System.Array.Empty<byte>();
        internal JsonRecordBuffer(int limit) => _limit = limit;
        internal byte[] Buffer => _buffer;
        internal int Count { get; private set; }
        internal void Reset() => Count = 0;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Count;
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Reserve(count);
            System.Buffer.BlockCopy(buffer, offset, _buffer, Count, count);
            Count += count;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
#if NET10_0_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer)
        { Reserve(buffer.Length); buffer.CopyTo(_buffer.AsSpan(Count)); Count += buffer.Length; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return default; }
#endif
        private void Reserve(int count)
        {
            if (count > _limit - Count) throw new InvalidOperationException("The serialized JSON record exceeds MaxItemBytes.");
            var needed = Count + count;
            if (needed <= _buffer.Length) return;
            var capacity = Math.Min(_limit, Math.Max(needed, Math.Max(256, _buffer.Length * 2)));
            System.Array.Resize(ref _buffer, capacity);
        }
    }
}
