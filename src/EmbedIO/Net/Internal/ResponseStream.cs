using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal
{
    internal class ResponseStream : Stream
    {
        // An application write of at most this many body bytes is committed with one
        // transport write that also carries its chunk framing; the first write of a
        // response carries the response head as well. Larger writes send the body
        // directly from the caller's buffer after a merged head segment that copies
        // only FirstSegmentPrefix body bytes, so the pooled copy never exceeds this
        // bound plus the head and framing.
        private const int BatchedBodyBound = 65536;
        private const int FirstSegmentPrefix = 16384;
        private static readonly byte[] CrLf = { 13, 10 };
        private readonly object _headersSyncRoot = new();
        private readonly EmbedIO.Internal.AsyncWriteGate _asyncWriteLock = new();

        private readonly EmbedIO.Internal.BorrowedResource<Stream> _transport;
        private Stream _stream => _transport.Value;
        private readonly HttpListenerResponse _response;
        private readonly bool _ignoreErrors;
        private bool _disposed;
        private bool _trailerSent;

        internal ResponseStream(Stream stream, HttpListenerResponse response, bool ignoreErrors)
        {
            _response = response;
            _ignoreErrors = ignoreErrors;
            _transport = new EmbedIO.Internal.BorrowedResource<Stream>(stream);
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

        internal async Task WriteInformationalAsync(byte[] bytes, CancellationToken token)
        {
            using var scope = await _asyncWriteLock.EnterAsync(token).ConfigureAwait(false);
            ValidateWrite(bytes, 0, bytes.Length);
            if (_response.HeadersSent) throw new InvalidOperationException("Final response headers already sent.");
            await InternalWriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
        }

        private async Task WriteAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Stream's inherited async fallback serialized writes. Preserve that ordering
            // without retaining a worker thread while waiting for transport backpressure.
            using var scope = await _asyncWriteLock.EnterAsync(cancellationToken).ConfigureAwait(false);
            ValidateWrite(buffer, offset, count);
            await WriteAsyncLocked(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }

        private async Task WriteAsyncLocked(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_response.SuppressesBody)
            {
                buffer = Array.Empty<byte>(); offset = 0; count = 0;
            }
            // Framing is decided while the head is prepared; read it afterwards.
            using var headers = GetHeaders(false, 0);
            var chunked = _response.SendChunked && !_response.SuppressesBody;
            var hasBody = count > 0;
            if (headers != null)
            {
                var head = headers.GetBuffer();
                var headStart = (int)headers.Position;
                var headLength = (int)(headers.Length - headStart);
                if (!hasBody)
                {
                    await InternalWriteAsync(head, headStart, headLength, cancellationToken).ConfigureAwait(false);
                    return;
                }
                if (headLength <= BatchedBodyBound)
                {
                    var segment = RentFirstSegment(head, headStart, headLength, buffer, offset, count, chunked, out var length, out var prefix);
                    try
                    {
                        await InternalWriteAsync(segment, 0, length, cancellationToken).ConfigureAwait(false);
                    }
                    finally { ReturnCleared(segment, length); }
                    if (prefix == count) return;
                    await InternalWriteAsync(buffer, offset + prefix, count - prefix, cancellationToken).ConfigureAwait(false);
                    if (chunked)
                        await InternalWriteAsync(CrLf, 0, CrLf.Length, cancellationToken).ConfigureAwait(false);
                    return;
                }
                // An oversized head is sent alone; the body follows as an ordinary write.
                await InternalWriteAsync(head, headStart, headLength, cancellationToken).ConfigureAwait(false);
            }
            if (!hasBody) return;
            if (!chunked)
            {
                await InternalWriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (count <= BatchedBodyBound)
            {
                // Bound the copy while committing this application write immediately.
                // The local lease remains owned until transport completion or failure.
                var segment = RentChunkSegment(buffer, offset, count, out var length);
                try
                {
                    await InternalWriteAsync(segment, 0, length, cancellationToken).ConfigureAwait(false);
                }
                finally { ReturnCleared(segment, length); }
                return;
            }
            var size = GetChunkSizeBytes(count, false);
            await InternalWriteAsync(size, 0, size.Length, cancellationToken).ConfigureAwait(false);
            await InternalWriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            await InternalWriteAsync(CrLf, 0, CrLf.Length, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
        {
            ValidateWrite(buffer, offset, count);
            if (_response.IsHeadResponse)
                return;
            if (_response.SuppressesBody)
            {
                // Bodyless writes still commit the response head, including an
                // empty write used before transport shutdown. HEAD keeps its
                // existing deferred-metadata behavior.
                buffer = Array.Empty<byte>(); offset = 0; count = 0;
            }

            using var headers = GetHeaders(false, 0);
            var chunked = _response.SendChunked && !_response.SuppressesBody;
            var hasBody = count > 0;
            if (headers != null)
            {
                var head = headers.GetBuffer();
                var headStart = (int)headers.Position;
                var headLength = (int)(headers.Length - headStart);
                if (!hasBody)
                {
                    InternalWrite(head, headStart, headLength);
                    return;
                }
                if (headLength <= BatchedBodyBound)
                {
                    var segment = RentFirstSegment(head, headStart, headLength, buffer, offset, count, chunked, out var length, out var prefix);
                    try
                    {
                        InternalWrite(segment, 0, length);
                    }
                    finally { ReturnCleared(segment, length); }
                    if (prefix == count) return;
                    InternalWrite(buffer, offset + prefix, count - prefix);
                    if (chunked) InternalWrite(CrLf, 0, CrLf.Length);
                    return;
                }
                InternalWrite(head, headStart, headLength);
            }
            if (!hasBody) return;
            if (!chunked)
            {
                InternalWrite(buffer, offset, count);
                return;
            }
            if (count <= BatchedBodyBound)
            {
                var segment = RentChunkSegment(buffer, offset, count, out var length);
                try
                {
                    InternalWrite(segment, 0, length);
                }
                finally { ReturnCleared(segment, length); }
                return;
            }
            var size = GetChunkSizeBytes(count, false);
            InternalWrite(size, 0, size.Length);
            InternalWrite(buffer, offset, count);
            InternalWrite(CrLf, 0, CrLf.Length);
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
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
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
            catch (Exception error) when (_ignoreErrors && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
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

            _asyncWriteLock.Dispose();
            using var ms = GetHeaders(true, 0);
            var chunked = _response.SendChunked && !_response.SuppressesBody;

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
                            bytes = _response.EndingChunk ?? GetChunkSizeBytes(0, true);
                            ms.Position = ms.Length;
                            ms.Write(bytes, 0, bytes.Length);
                        }

                        InternalWrite(ms.GetBuffer(), (int)start, (int)(ms.Length - start));
                        _trailerSent = true;
                    }
                    else if (chunked && !_trailerSent)
                    {
                        bytes = _response.EndingChunk ?? GetChunkSizeBytes(0, true);
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

        // One transport segment for the first write: head, chunk-size line, the whole
        // body with its chunk CRLF when it fits the bound, otherwise a 16 KiB prefix.
        // The caller sends any remaining body bytes directly and ends the chunk.
        private static byte[] RentFirstSegment(byte[] head, int headStart, int headLength,
            byte[] buffer, int offset, int count, bool chunked, out int length, out int prefix)
        {
            var complete = count <= BatchedBodyBound;
            prefix = complete ? count : FirstSegmentPrefix;
            length = headLength + prefix + (chunked ? GetChunkPrefixLength(count) + (complete ? CrLf.Length : 0) : 0);
            var segment = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(head, headStart, segment, 0, headLength);
            var position = headLength;
            if (chunked) position += WriteChunkPrefix(segment, position, count);
            Buffer.BlockCopy(buffer, offset, segment, position, prefix);
            position += prefix;
            if (chunked && complete)
            {
                segment[position] = 13;
                segment[position + 1] = 10;
            }
            return segment;
        }

        // One transport segment for a subsequent chunk of at most BatchedBodyBound bytes.
        private static byte[] RentChunkSegment(byte[] buffer, int offset, int count, out int length)
        {
            length = GetChunkPrefixLength(count) + count + CrLf.Length;
            var segment = ArrayPool<byte>.Shared.Rent(length);
            var position = WriteChunkPrefix(segment, 0, count);
            Buffer.BlockCopy(buffer, offset, segment, position, count);
            position += count;
            segment[position] = 13;
            segment[position + 1] = 10;
            return segment;
        }

        // Only the bytes this stream wrote are cleared before the lease returns.
        private static void ReturnCleared(byte[] segment, int length)
        {
            Array.Clear(segment, 0, length);
            ArrayPool<byte>.Shared.Return(segment);
        }

        private static int GetHexDigitCount(int size)
        {
            var digits = 1;
            for (var remaining = unchecked((uint)size) >> 4; remaining != 0; remaining >>= 4) digits++;
            return digits;
        }

        private static int GetChunkPrefixLength(int size) => GetHexDigitCount(size) + CrLf.Length;

        // Writes the lowercase hexadecimal size line, returning the bytes written.
        private static int WriteChunkPrefix(byte[] target, int offset, int size)
        {
            var value = unchecked((uint)size);
            var digits = GetHexDigitCount(size);
            for (var index = digits - 1; index >= 0; index--)
            {
                var digit = (int)(value & 15);
                target[offset + index] = (byte)(digit < 10 ? '0' + digit : 'a' + digit - 10);
                value >>= 4;
            }
            target[offset + digits] = 13;
            target[offset + digits + 1] = 10;
            return digits + CrLf.Length;
        }

        private static byte[] GetChunkSizeBytes(int size, bool final)
        {
            var bytes = new byte[GetChunkPrefixLength(size) + (final ? CrLf.Length : 0)];
            var written = WriteChunkPrefix(bytes, 0, size);
            if (final) { bytes[written] = 13; bytes[written + 1] = 10; }
            return bytes;
        }

        private MemoryStream? GetHeaders(bool closing, int bodyCount)
        {
            lock (_headersSyncRoot)
            {
                return _response.HeadersSent ? null : _response.SendHeaders(closing, bodyCount);
            }
        }
    }
}
