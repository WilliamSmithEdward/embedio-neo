using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // A WebSocket owns this stream, not the connection or sibling HTTP/2 streams.
    // Context completion performs the HTTP/2 END_STREAM/reset transition.
    internal sealed class Http2DuplexStream : Stream
    {
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _input;
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _output;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _token;
        private int _disposed;
        internal Http2DuplexStream(Stream input, Stream output)
        { _input = new EmbedIO.Internal.BorrowedResource<Stream>(input); _output = new EmbedIO.Internal.BorrowedResource<Stream>(output); _token = _stop.Token; }
        public override bool CanRead => _disposed == 0;
        public override bool CanWrite => _disposed == 0;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            linked.Token.ThrowIfCancellationRequested();
            return await _input.Value.ReadAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            linked.Token.ThrowIfCancellationRequested();
            await _output.Value.WriteAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override async Task FlushAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            linked.Token.ThrowIfCancellationRequested();
            await _output.Value.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _stop.Cancel(); } finally { _stop.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }
}
