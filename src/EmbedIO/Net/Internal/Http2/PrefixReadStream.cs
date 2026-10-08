using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Replays protocol-sniffing bytes once; the socket owner retains stream ownership.
    internal sealed class PrefixReadStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _prefix;
        private int _offset;
        private readonly int _length;
        internal PrefixReadStream(Stream inner, byte[] prefix, int length)
        { _inner = inner; _prefix = prefix; _length = length; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanWrite => _inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Copy(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var copied = Math.Min(count, _length - _offset);
            if (copied != 0)
            {
                Buffer.BlockCopy(_prefix!, _offset, bytes, offset, copied);
                _offset += copied;
                if (_offset == _length) _prefix = null;
            }
            return copied;
        }
        public override int Read(byte[] bytes, int offset, int count)
        { var copied = Copy(bytes, offset, count); return copied != 0 || count == 0 ? copied : _inner.Read(bytes, offset, count); }
        public override Task<int> ReadAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var copied = Copy(bytes, offset, count);
            return copied != 0 || count == 0 ? Task.FromResult(copied) : _inner.ReadAsync(bytes, offset, count, token);
        }
        public override void Write(byte[] bytes, int offset, int count) => _inner.Write(bytes, offset, count);
        public override Task WriteAsync(byte[] bytes, int offset, int count, CancellationToken token) => _inner.WriteAsync(bytes, offset, count, token);
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken token) => _inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
