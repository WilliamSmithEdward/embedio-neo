using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // Independently authored HTTP/1 output owner. The transport is borrowed: only
    // the connection closes it. One gate orders application writes and completion.
    // Framing counters describe committed body bytes, never prepared submissions.
    internal sealed class ResponseStream : Stream
    {
        private const int BatchedBodyBound = 65536;
        private const int FirstSegmentPrefix = 16384;
        private static readonly byte[] CrLf = { 13, 10 };
        private readonly AsyncWriteGate _writers = new();
        private readonly BorrowedResource<Stream> _transport;
        private readonly HttpListenerResponse _response;
        private readonly bool _ignoreErrors;
        private int _closed;
        private int _completionFinished;
        private int _failed;
        private bool _headCaptured;
        private long? _bodyLimit;
        private long _committedBody;

        internal ResponseStream(Stream transport, HttpListenerResponse response, bool ignoreErrors)
        {
            _transport = new BorrowedResource<Stream>(transport);
            _response = response;
            _ignoreErrors = ignoreErrors;
        }

        internal bool OutputCompleted => Volatile.Read(ref _completionFinished) != 0 && Volatile.Read(ref _failed) == 0
            && (_bodyLimit == null || _committedBody == _bodyLimit.Value);

        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token)
            => token.IsCancellationRequested ? Task.FromCanceled(token) : Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckWrite(buffer, offset, count);
            if (_response.IsHeadResponse) return;
            using var writer = _writers.Enter();
            CheckWrite(buffer, offset, count);
            if (!CanSubmit()) return;
            WriteBodyAsync(buffer, offset, count, true, default).GetAwaiter().GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            CheckWrite(buffer, offset, count);
            if (token.IsCancellationRequested) return Task.FromCanceled(token);
            return _response.IsHeadResponse ? Task.CompletedTask : WriteBodyCoreAsync(buffer, offset, count, token);
        }

        private async Task WriteBodyCoreAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var writer = await _writers.EnterAsync(token).ConfigureAwait(false);
            CheckWrite(buffer, offset, count);
            if (!CanSubmit()) return;
            await WriteBodyAsync(buffer, offset, count, false, token).ConfigureAwait(false);
        }

        internal async Task WriteInformationalAsync(byte[] bytes, CancellationToken token)
        {
            using var writer = await _writers.EnterAsync(token).ConfigureAwait(false);
            CheckWrite(bytes, 0, bytes.Length);
            if (!CanSubmit()) return;
            if (_response.HeadersSent) throw new InvalidOperationException("Final response headers already sent.");
            await SubmitAsync(bytes, 0, bytes.Length, false, token).ConfigureAwait(false);
        }

        private void CheckWrite(byte[] buffer, int offset, int count)
        {
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(ResponseStream));
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (offset > buffer.Length - count) throw new ArgumentException("The offset and count exceed the buffer length.");
        }

        private bool CanSubmit()
        {
            if (Volatile.Read(ref _failed) == 0) return true;
            if (!_ignoreErrors) throw new IOException("HTTP/1 response output is no longer usable.");
            return false;
        }

        private long? DeclaredBodyLimit()
        {
            if (_response.SuppressesBody || _response.IsTunnelResponse || _response.SendChunked) return null;
            var value = _response.Headers[HttpHeaderNames.ContentLength];
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length >= 0 ? length : null;
        }

        private MemoryStream? PrepareHead(bool completing)
        {
            var headers = _response.HeadersSent ? null : _response.SendHeaders(completing, 0);
            if (!_headCaptured)
            {
                _bodyLimit = DeclaredBodyLimit();
                _headCaptured = true;
            }
            return headers;
        }

        private async ValueTask WriteBodyAsync(byte[] buffer, int offset, int count, bool synchronous, CancellationToken token)
        {
            if (_response.SuppressesBody) count = 0;
            var limit = _headCaptured ? _bodyLimit : DeclaredBodyLimit();
            if (limit.HasValue && count > limit.Value - _committedBody)
                throw new ProtocolViolationException("The response body exceeds its declared Content-Length.");

            using var head = PrepareHead(false);
            var chunked = _response.SendChunked && !_response.SuppressesBody;
            if (head != null)
            {
                var start = checked((int)head.Position);
                var length = checked((int)(head.Length - head.Position));
                if (count == 0)
                {
                    await SubmitAsync(head.GetBuffer(), start, length, synchronous, token).ConfigureAwait(false);
                    return;
                }
                if (length <= BatchedBodyBound)
                {
                    var segment = RentFirstSegment(head.GetBuffer(), start, length, buffer, offset, count, chunked, out var segmentLength, out var prefix);
                    try { await SubmitAsync(segment, 0, segmentLength, synchronous, token).ConfigureAwait(false); }
                    finally { ReturnCleared(segment, segmentLength); }
                    if (prefix != count)
                    {
                        await SubmitAsync(buffer, offset + prefix, count - prefix, synchronous, token).ConfigureAwait(false);
                        if (chunked) await SubmitAsync(CrLf, 0, CrLf.Length, synchronous, token).ConfigureAwait(false);
                    }
                    CommitBody(count);
                    return;
                }
                await SubmitAsync(head.GetBuffer(), start, length, synchronous, token).ConfigureAwait(false);
            }
            if (count == 0) return;
            if (!chunked)
                await SubmitAsync(buffer, offset, count, synchronous, token).ConfigureAwait(false);
            else if (count <= BatchedBodyBound)
            {
                var segment = RentChunkSegment(buffer, offset, count, out var length);
                try { await SubmitAsync(segment, 0, length, synchronous, token).ConfigureAwait(false); }
                finally { ReturnCleared(segment, length); }
            }
            else
            {
                var size = GetChunkSizeBytes(count, false);
                await SubmitAsync(size, 0, size.Length, synchronous, token).ConfigureAwait(false);
                await SubmitAsync(buffer, offset, count, synchronous, token).ConfigureAwait(false);
                await SubmitAsync(CrLf, 0, CrLf.Length, synchronous, token).ConfigureAwait(false);
            }
            CommitBody(count);
        }

        private void CommitBody(int count)
        {
            if (Volatile.Read(ref _failed) == 0 && _bodyLimit.HasValue) _committedBody += count;
        }

        private async ValueTask SubmitAsync(byte[] bytes, int offset, int count, bool synchronous, CancellationToken token)
        {
            // The first ignored error still poisons the wire. No remainder or later
            // footer is submitted after a potentially partial transport write.
            if (Volatile.Read(ref _failed) != 0) return;
            try
            {
                if (synchronous) _transport.Value.Write(bytes, offset, count);
                else await _transport.Value.WriteAsync(bytes, offset, count, token).ConfigureAwait(false);
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                Volatile.Write(ref _failed, 1);
                if (!_ignoreErrors || error is OperationCanceledException && token.IsCancellationRequested) throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing || Volatile.Read(ref _completionFinished) != 0) return;
            var ownsCompletion = false;
            try
            {
                using var writer = _writers.Enter();
                if (Volatile.Read(ref _completionFinished) != 0) return;
                Volatile.Write(ref _closed, 1);
                ownsCompletion = true;
                try
                {
                    if (Volatile.Read(ref _failed) == 0)
                    {
                        if (!_transport.Value.CanWrite) Volatile.Write(ref _failed, 1);
                        else
                        {
                            using var head = PrepareHead(true);
                            if (head != null)
                                SubmitAsync(head.GetBuffer(), checked((int)head.Position), checked((int)(head.Length - head.Position)), true, default).GetAwaiter().GetResult();
                            if (_response.SendChunked && !_response.SuppressesBody)
                            {
                                var ending = _response.EndingChunk ?? GetChunkSizeBytes(0, true);
                                SubmitAsync(ending, 0, ending.Length, true, default).GetAwaiter().GetResult();
                            }
                        }
                    }
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    Volatile.Write(ref _failed, 1);
                    if (error is not IOException and not ObjectDisposedException) throw;
                }
                finally { Volatile.Write(ref _completionFinished, 1); }
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _completionFinished) != 0)
            {
                // Another completion already released and retired the write gate.
            }
            finally
            {
                if (ownsCompletion)
                {
                    _writers.Dispose();
                    if (OutputCompleted) _response.Close();
                    else _response.Abort();
                }
            }
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

    }
}
