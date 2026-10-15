using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Sends the interim 100 Continue response owed to an Expect: 100-continue request
    // before the first body read, instead of before dispatch. A server that refuses the
    // request without reading its body therefore never invites the upload.
    internal sealed class DeferredContinueStream : Stream
    {
        private readonly Stream _source;
        private readonly Func<CancellationToken, Task> _send;
        private int _state; // 0 owed, 1 sending or sent

        internal DeferredContinueStream(Stream source, Func<CancellationToken, Task> send)
        {
            _source = source;
            _send = send;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        private Task SendOnceAsync(CancellationToken cancellationToken)
            => Interlocked.Exchange(ref _state, 1) == 0 ? _send(cancellationToken) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count)
        {
            SendOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            return _source.Read(buffer, offset, count);
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await SendOnceAsync(cancellationToken).ConfigureAwait(false);
            return await _source.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            SendOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
            return _source.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await SendOnceAsync(cancellationToken).ConfigureAwait(false);
            return await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
#endif
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _source.Dispose();
            base.Dispose(disposing);
        }
    }
}
