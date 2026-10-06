using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal class ResponseStream : Stream
    {
        private static readonly byte[] CrLf = { 13, 10 };
        private readonly object _headersSyncRoot = new();
        private readonly SemaphoreSlim _asyncWriteLock = new(1, 1);

        private readonly Stream _stream;
        private readonly HttpListenerResponse _response;
        private readonly bool _ignoreErrors;
        private bool _disposed;
        private bool _trailerSent;

        internal ResponseStream(Stream stream, HttpListenerResponse response, bool ignoreErrors)
        {
            _response = response;
            _ignoreErrors = ignoreErrors;
            _stream = stream;
        }

        /// <inheritdoc />
        public override bool CanRead => false;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush()
        {
        }

        /// <inheritdoc />
        public override Task FlushAsync(CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested ? Task.FromCanceled(cancellationToken) : Task.CompletedTask;

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateWrite(buffer, offset, count);
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (_response.IsHeadResponse)
                return Task.CompletedTask;

            return WriteAsyncCore(buffer, offset, count, cancellationToken);
        }

        private async Task WriteAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Stream's inherited async fallback serialized writes. Preserve that ordering
            // without retaining a worker thread while waiting for transport backpressure.
            await _asyncWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ValidateWrite(buffer, offset, count);
                await WriteAsyncLocked(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _asyncWriteLock.Release();
            }
        }

        private async Task WriteAsyncLocked(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var headers = GetHeaders(false);
            var chunked = _response.SendChunked;
            var hasBody = count > 0;
            if (headers != null)
            {
                var start = headers.Position;
                headers.Position = headers.Length;
                if (chunked && hasBody)
                {
                    var size = GetChunkSizeBytes(count, false);
                    headers.Write(size, 0, size.Length);
                }

                var prefixCount = Math.Min(count, Math.Max(0, 16384 - (int)(headers.Length - start)));
                headers.Write(buffer, offset, prefixCount);
                await InternalWriteAsync(headers.GetBuffer(), (int)start, (int)(headers.Length - start), cancellationToken)
                    .ConfigureAwait(false);
                offset += prefixCount;
                count -= prefixCount;
            }
            else if (chunked && hasBody)
            {
                var size = GetChunkSizeBytes(count, false);
                await InternalWriteAsync(size, 0, size.Length, cancellationToken).ConfigureAwait(false);
            }

            if (count > 0)
                await InternalWriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (chunked && hasBody)
                await InternalWriteAsync(CrLf, 0, CrLf.Length, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateWrite(buffer, offset, count);
            if (_response.IsHeadResponse)
                return;

            byte[] bytes;
            var ms = GetHeaders(false);
            var chunked = _response.SendChunked;
            var hasBody = count > 0;

            if (ms != null)
            {
                var start = ms.Position; // After the possible preamble for the encoding
                ms.Position = ms.Length;
                if (chunked && hasBody)
                {
                    bytes = GetChunkSizeBytes(count, false);
                    ms.Write(bytes, 0, bytes.Length);
                }

                var newCount = Math.Min(count, Math.Max(0, 16384 - (int)ms.Position + (int)start));
                ms.Write(buffer, offset, newCount);
                count -= newCount;
                offset += newCount;
                InternalWrite(ms.GetBuffer(), (int)start, (int)(ms.Length - start));
                ms.SetLength(0);
                ms.Capacity = 0; // 'dispose' the buffer in ms.
            }
            else if (chunked && hasBody)
            {
                bytes = GetChunkSizeBytes(count, false);
                InternalWrite(bytes, 0, bytes.Length);
            }

            if (count > 0)
            {
                InternalWrite(buffer, offset, count);
            }

            if (chunked && hasBody)
            {
                InternalWrite(CrLf, 0, 2);
            }
        }

        /// <inheritdoc />
        public override int Read([In, Out] byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        internal void InternalWrite(byte[] buffer, int offset, int count)
        {
            if (_ignoreErrors)
            {
                try
                {
                    _stream.Write(buffer, offset, count);
                }
                catch
                {
                    // ignored
                }
            }
            else
            {
                _stream.Write(buffer, offset, count);
            }
        }

        private async Task InternalWriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try
            {
                await _stream.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch when (_ignoreErrors)
            {
                // Preserve IgnoreWriteExceptions, but never suppress caller cancellation.
            }
        }

        private void ValidateWrite(byte[] buffer, int offset, int count)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ResponseStream));
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!disposing)
            {
                return;
            }

            using var ms = GetHeaders(true);
            var chunked = _response.SendChunked;

            if (_stream.CanWrite)
            {
                try
                {
                    byte[] bytes;
                    if (ms != null)
                    {
                        var start = ms.Position;
                        if (chunked && !_trailerSent)
                        {
                            bytes = GetChunkSizeBytes(0, true);
                            ms.Position = ms.Length;
                            ms.Write(bytes, 0, bytes.Length);
                        }

                        InternalWrite(ms.GetBuffer(), (int)start, (int)(ms.Length - start));
                        _trailerSent = true;
                    }
                    else if (chunked && !_trailerSent)
                    {
                        bytes = GetChunkSizeBytes(0, true);
                        InternalWrite(bytes, 0, bytes.Length);
                        _trailerSent = true;
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Ignored
                }
                catch (IOException)
                {
                    // Ignore error due to connection reset by peer
                }
            }

            _response.Close();
        }

        private static byte[] GetChunkSizeBytes(int size, bool final) => WebServer.DefaultEncoding.GetBytes($"{size:x}\r\n{(final ? "\r\n" : string.Empty)}");

        private MemoryStream? GetHeaders(bool closing)
        {
            lock (_headersSyncRoot)
            {
                return _response.HeadersSent ? null : _response.SendHeaders(closing);
            }
        }
    }
}
