using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // The connection owns the socket; this handoff owns its lifetime. Prefix
    // replay remains borrowed so disposal must go through the connection owner.
    internal sealed class Http1TunnelStream : Stream
    {
        private readonly BorrowedResource<Stream> _stream;
        private readonly HttpConnection _connection;
        private readonly AsyncWriteGate _output = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _token;
        private int _disposed;
        private volatile bool _sendEnded;
        internal Http1TunnelStream(HttpConnection connection, Stream stream)
        { _connection = connection; _stream = new BorrowedResource<Stream>(stream); _token = _stop.Token; }
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanWrite => CanRead && !_sendEnded;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            linked.Token.ThrowIfCancellationRequested();
            return await _stream.Value.ReadAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            using var scope = await _output.EnterAsync(linked.Token).ConfigureAwait(false);
            if (!CanWrite) throw new ObjectDisposedException(nameof(Http1TunnelStream));
            await _stream.Value.WriteAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }
        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override async Task FlushAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            using var scope = await _output.EnterAsync(linked.Token).ConfigureAwait(false);
            if (!CanWrite) throw new ObjectDisposedException(nameof(Http1TunnelStream));
            await _stream.Value.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        internal async Task CompleteOutputAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            using var scope = await _output.EnterAsync(linked.Token).ConfigureAwait(false);
            if (_sendEnded) return;
            _sendEnded = true;
            await _connection.CompleteTunnelOutputAsync(linked.Token).ConfigureAwait(false);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try { _stop.Cancel(); }
                finally
                {
                    try { _connection.Dispose(); }
                    finally { _output.Dispose(); _stop.Dispose(); }
                }
            }
            base.Dispose(disposing);
        }
    }
}
