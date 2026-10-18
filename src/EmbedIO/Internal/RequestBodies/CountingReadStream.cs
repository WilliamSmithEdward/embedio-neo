using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Counts request body bytes the application reads, for telemetry. Sits closest to the
    // transport body, so it sees the bytes received regardless of later policy wrappers.
    internal sealed class CountingReadStream : Stream
    {
        private readonly Stream _source;
        private readonly RequestTelemetry _scope;

        internal CountingReadStream(Stream source, RequestTelemetry scope)
        {
            _source = source;
            _scope = scope;
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private int Count(int read)
        {
            if (read > 0) _scope.AddRequestBodyBytes(read);

            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(_source.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) =>
            Count(
                await _source
                    .ReadAsync(buffer, offset, count, cancellationToken)
                    .ConfigureAwait(false)
            );
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer) => Count(_source.Read(buffer));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        ) =>
            Count(
                await _source
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false)
            );
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
