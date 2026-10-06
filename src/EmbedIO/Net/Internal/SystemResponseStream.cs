using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    // Commit response cookies at the first operation that can send native headers,
    // rather than when a handler merely acquires the output stream.
    internal sealed class SystemResponseStream : Stream
    {
        private readonly Stream _stream;
        private readonly Action _prepareHeaders;
        private readonly bool _suppressBody;
        private bool _disposed;

        public SystemResponseStream(Stream stream, Action prepareHeaders, bool suppressBody = false)
        {
            _stream = stream;
            _prepareHeaders = prepareHeaders;
            _suppressBody = suppressBody;
            if (_suppressBody)
                UnixHeadResponseCompatibility.SuppressClosingChunk(_stream);
        }

        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateWrite(buffer, offset, count);
            if (_suppressBody)
                return;
            // Unix ignores synchronous empty writes without computing response headers.
            if (count != 0 || RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                _prepareHeaders();
            _stream.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateWrite(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (_suppressBody)
                return Task.CompletedTask;
            _prepareHeaders();
            return _stream.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
        {
            ValidateWrite(buffer, offset, count);
            if (_suppressBody)
                return base.BeginWrite(buffer, offset, count, callback, state);
            _prepareHeaders();
            return _stream.BeginWrite(buffer, offset, count, callback, state);
        }

        public override void EndWrite(IAsyncResult asyncResult)
        {
            if (_suppressBody) base.EndWrite(asyncResult);
            else _stream.EndWrite(asyncResult);
        }

        public override void Flush()
        {
            if (!_suppressBody) _stream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => _suppressBody
                ? cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask
                : _stream.FlushAsync(cancellationToken);

        private void ValidateWrite(byte[] buffer, int offset, int count)
        {
            if (_disposed || (_suppressBody && !_stream.CanWrite))
                throw new ObjectDisposedException(nameof(SystemResponseStream));
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (offset > buffer.Length - count)
                throw new ArgumentException("The offset and count exceed the buffer length.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _prepareHeaders();
                _stream.Dispose();
                _disposed = true;
            }
            base.Dispose(disposing);
        }
    }
}
