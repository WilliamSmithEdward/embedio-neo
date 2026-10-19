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
    internal sealed class ResponseStream : Stream, IEndingBodyOutput
    {
        private const int BatchedBodyBound = 65536;
        private const int FirstSegmentPrefix = 16384;
        private static readonly byte[] CrLf = [13, 10];
        private static readonly byte[] EndOfChunks =
        [
            (byte)'0',
            13,
            10,
            13,
            10
        ];
        private static readonly byte[] CrLfEndOfChunks =
        [
            13,
            10,
            (byte)'0',
            13,
            10,
            13,
            10
        ];
        private readonly AsyncWriteGate _writers = new();
        private readonly BorrowedResource<Stream> _transport;
        private readonly HttpListenerResponse _response;
        private readonly bool _ignoreErrors;
        private int _closed;
        private int _completionFinished;
        private int _failed;

        internal bool HasOutputFailure => Volatile.Read(ref _failed) != 0;

        private bool _headCaptured;

        // A complete fixed-length/suppressed body or chunked terminator has left the writer.
        private bool _wireEndSent;

        internal bool HasWireEnd => Volatile.Read(ref _wireEndSent);

        private long? _bodyLimit;
        private long _committedBody;
        private long _sentBody;

        // Body bytes handed to the transport without a write failure, for telemetry.
        internal long SentBodyBytes => Volatile.Read(ref _sentBody);

        internal ResponseStream(Stream transport, HttpListenerResponse response, bool ignoreErrors)
        {
            _transport = new BorrowedResource<Stream>(transport);
            _response = response;
            _ignoreErrors = ignoreErrors;
        }

        internal bool OutputCompleted =>
            Volatile.Read(ref _completionFinished) != 0
                && Volatile.Read(ref _failed) == 0
                && (_bodyLimit == null || _committedBody == _bodyLimit.Value);
        public override bool CanRead => false;
        public override bool CanWrite => Volatile.Read(ref _closed) == 0;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long length) => throw new NotSupportedException();

        public override void Flush()
        {
            CheckWrite([], 0, 0);
            using var writer = _writers.Enter();
            CheckWrite([], 0, 0);

            if (!CanSubmit()) return;

            FlushCoreAsync(true, default)
                .GetAwaiter()
                .GetResult();
        }

        public override Task FlushAsync(CancellationToken token)
        {
            CheckWrite([], 0, 0);
            return token.IsCancellationRequested ? Task.FromCanceled(token) : FlushOrderedAsync(token);
        }

        private async Task FlushOrderedAsync(CancellationToken token)
        {
            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);
            CheckWrite([], 0, 0);
            token.ThrowIfCancellationRequested();

            if (!CanSubmit()) return;

            await FlushCoreAsync(false, token).ConfigureAwait(false);
        }

        private async Task FlushCoreAsync(bool synchronous, CancellationToken token)
        {
            using var head = PrepareHead(false, 0);

            if (!head.IsEmpty)
                await SubmitAsync(head.Buffer, 0, head.Length, synchronous, token).ConfigureAwait(false);

            if (Volatile.Read(ref _failed) != 0) return;

            try
            {
                if (synchronous) _transport.Value.Flush();

                else
                    await _transport.Value
                        .FlushAsync(token)
                        .ConfigureAwait(false);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                RequestAbortRegistry
                    .Find(this)
                    ?.Abort();
                Volatile.Write(ref _failed, 1);

                if (!_ignoreErrors || error is OperationCanceledException && token.IsCancellationRequested) throw;
            }

            ObserveWireEnd();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckWrite(buffer, offset, count);

            if (_response.IsHeadResponse) return;

            using var writer = _writers.Enter();
            CheckWrite(buffer, offset, count);

            if (!CanSubmit()) return;

            WriteBodyAsync(buffer, offset, count, true, false, default)
                .GetAwaiter()
                .GetResult();
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            CheckWrite(buffer, offset, count);

            if (token.IsCancellationRequested) return Task.FromCanceled(token);

            return _response.IsHeadResponse ? Task.CompletedTask : WriteBodyCoreAsync(buffer, offset, count, token);
        }

        private async Task WriteBodyCoreAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);
            CheckWrite(buffer, offset, count);

            if (!CanSubmit()) return;

            await WriteBodyAsync(buffer, offset, count, false, false, token).ConfigureAwait(false);
        }

        // The body's last bytes with its ending in the same transport write: for a chunked
        // response the terminator, or the trailer section once set, follows the last chunk in
        // one segment and disposal then only closes the response. With a declared length the
        // write is ordinary. Equivalent on the wire to the write followed by disposal.
        public void WriteEnding(byte[] buffer, int offset, int count)
        {
            CheckWrite(buffer, offset, count);

            if (_response.IsHeadResponse) return;

            using var writer = _writers.Enter();
            CheckWrite(buffer, offset, count);

            if (!CanSubmit()) return;

            WriteBodyAsync(buffer, offset, count, true, true, default)
                .GetAwaiter()
                .GetResult();
        }

        public Task WriteEndingAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            CheckWrite(buffer, offset, count);

            if (token.IsCancellationRequested) return Task.FromCanceled(token);

            return _response.IsHeadResponse ? Task.CompletedTask : WriteEndingCoreAsync(buffer, offset, count, token);
        }

        private async Task WriteEndingCoreAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);
            CheckWrite(buffer, offset, count);

            if (!CanSubmit()) return;

            await WriteBodyAsync(buffer, offset, count, false, true, token).ConfigureAwait(false);
        }

        internal async Task WriteInformationalAsync(byte[] bytes, CancellationToken token)
        {
            using var writer = await _writers
                .EnterAsync(token)
                .ConfigureAwait(false);
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

            if (offset > buffer.Length - count)
                throw new ArgumentException("The offset and count exceed the buffer length.");
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
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                && length >= 0
                ? length
                : null;
        }

        // The head is encoded with room for the first body segment of bodyCount bytes, so
        // the first write appends behind it instead of copying the head into a new segment.
        private Http1Head PrepareHead(bool completing, int bodyCount, bool ending = false)
        {
            var headers = _response.HeadersSent ? default : _response.SendHeaders(completing, bodyCount, ending);

            if (!_headCaptured)
            {
                _bodyLimit = DeclaredBodyLimit();
                _headCaptured = true;
            }

            return headers;
        }

        private async ValueTask WriteBodyAsync(
            byte[] buffer,
            int offset,
            int count,
            bool synchronous,
            bool ending,
            CancellationToken token
        )
        {
            if (_response.SuppressesBody) count = 0;

            var limit = _headCaptured ? _bodyLimit : DeclaredBodyLimit();

            if (limit.HasValue && count > limit.Value - _committedBody)
                throw new ProtocolViolationException("The response body exceeds its declared Content-Length.");

            using var head = PrepareHead(false, count, ending);

            // Framing is final only once the head is prepared: HTTP/1.0 drops chunking but keeps a
            // declared Content-Length. Nothing has been submitted yet, so excess bytes never leave.
            if (_bodyLimit.HasValue && count > _bodyLimit.Value - _committedBody)
                throw new ProtocolViolationException("The response body exceeds its declared Content-Length.");

            var chunked = _response.SendChunked && !_response.SuppressesBody;
            // An ending write carries the terminator behind its last chunk; a declared length
            // needs no ending and an empty ending write leaves the terminator to disposal.
            var tail = ending && chunked && count != 0 ? _response.EndingChunk ?? EndOfChunks : null;

            if (!head.IsEmpty)
            {
                var length = head.Length;

                if (count == 0)
                {
                    await SubmitAsync(head.Buffer, 0, length, synchronous, token).ConfigureAwait(false);
                    ObserveWireEnd();
                    return;
                }

                if (length <= BatchedBodyBound)
                {
                    var segment = head.Buffer;
                    var segmentLength = AppendFirstSegment(
                        segment,
                        length,
                        buffer,
                        offset,
                        count,
                        chunked,
                        tail,
                        out var prefix
                    );

                    try
                    {
                        await SubmitAsync(segment, 0, segmentLength, synchronous, token).ConfigureAwait(false);
                    }

                    finally
                    {
                        Array.Clear(segment, length, segmentLength - length);
                    }

                    if (prefix != count)
                    {
                        await SubmitAsync(buffer, offset + prefix, count - prefix, synchronous, token).ConfigureAwait(
                            false
                        );

                        if (chunked) await SubmitChunkEndAsync(tail, synchronous, token).ConfigureAwait(false);
                    }

                    CommitBody(count, tail);
                    return;
                }

                await SubmitAsync(head.Buffer, 0, length, synchronous, token).ConfigureAwait(false);
            }

            if (count == 0) return;

            if (!chunked)
                await SubmitAsync(buffer, offset, count, synchronous, token).ConfigureAwait(false);

            else if (count <= BatchedBodyBound)
            {
                var segment = RentChunkSegment(buffer, offset, count, tail, out var length);

                try
                {
                    await SubmitAsync(segment, 0, length, synchronous, token).ConfigureAwait(false);
                }

                finally
                {
                    ReturnCleared(segment, length);
                }
            }

            else
            {
                var size = GetChunkSizeBytes(count, false);
                await SubmitAsync(size, 0, size.Length, synchronous, token).ConfigureAwait(false);
                await SubmitAsync(buffer, offset, count, synchronous, token).ConfigureAwait(false);
                await SubmitChunkEndAsync(tail, synchronous, token).ConfigureAwait(false);
            }

            CommitBody(count, tail);
        }

        // The CRLF that ends a chunk whose bytes went out directly, with the terminator
        // behind it when the write ends the body.
        private ValueTask SubmitChunkEndAsync(byte[]? tail, bool synchronous, CancellationToken token)
        {
            if (tail == null) return SubmitAsync(CrLf, 0, CrLf.Length, synchronous, token);

            if (ReferenceEquals(tail, EndOfChunks))
                return SubmitAsync(CrLfEndOfChunks, 0, CrLfEndOfChunks.Length, synchronous, token);

            var combined = new byte[CrLf.Length + tail.Length];
            combined[0] = 13;
            combined[1] = 10;
            Buffer.BlockCopy(tail, 0, combined, CrLf.Length, tail.Length);
            return SubmitAsync(combined, 0, combined.Length, synchronous, token);
        }

        private void CommitBody(int count, byte[]? tail = null)
        {
            if (Volatile.Read(ref _failed) != 0) return;

            if (_bodyLimit.HasValue) _committedBody += count;

            Volatile.Write(ref _sentBody, _sentBody + count);

            if (tail != null) _wireEndSent = true;

            ObserveWireEnd();
        }

        private void ObserveWireEnd()
        {
            if (HasOutputFailure || !_response.HeadersSent || _response.IsTunnelResponse) return;

            if (
                !_wireEndSent
                && !_response.SuppressesBody
                && (!_bodyLimit.HasValue || _committedBody != _bodyLimit.Value)
            ) return;

            Volatile.Write(ref _wireEndSent, true);
            _response.CompleteRequestAbortObservation();
        }

        private async ValueTask SubmitAsync(
            byte[] bytes,
            int offset,
            int count,
            bool synchronous,
            CancellationToken token
        )
        {
            // The first ignored error still poisons the wire. No remainder or later
            // footer is submitted after a potentially partial transport write.
            if (Volatile.Read(ref _failed) != 0) return;

            try
            {
                if (synchronous) _transport.Value.Write(bytes, offset, count);

                else
                    await _transport.Value
                        .WriteAsync(bytes, offset, count, token)
                        .ConfigureAwait(false);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                RequestAbortRegistry
                    .Find(this)
                    ?.Abort();
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
                        if (!_transport.Value.CanWrite)
                        {
                            RequestAbortRegistry
                                .Find(this)
                                ?.Abort();
                            Volatile.Write(ref _failed, 1);
                        }

                        else
                        {
                            using var head = PrepareHead(true, 0);

                            if (!head.IsEmpty)
                                SubmitAsync(head.Buffer, 0, head.Length, true, default)
                                    .GetAwaiter()
                                    .GetResult();

                            if (_response.SendChunked && !_response.SuppressesBody && !_wireEndSent)
                            {
                                var ending = _response.EndingChunk ?? EndOfChunks;
                                SubmitAsync(ending, 0, ending.Length, true, default)
                                    .GetAwaiter()
                                    .GetResult();

                                if (!HasOutputFailure) _wireEndSent = true;
                            }

                            ObserveWireEnd();
                        }
                    }
                }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    RequestAbortRegistry
                        .Find(this)
                        ?.Abort();
                    Volatile.Write(ref _failed, 1);

                    if (error is not IOException and not ObjectDisposedException) throw;
                }

                finally
                {
                    Volatile.Write(ref _completionFinished, 1);
                }
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

        // Room the head encoder leaves behind a head of headLength bytes for the first
        // write of count body bytes: the chunk-size line, the whole body with its chunk
        // CRLF and, for an ending write, the terminator of endingLength bytes when it fits
        // the bound, otherwise a 16 KiB prefix. Zero when the head alone exceeds the bound,
        // which keeps its own transport write.
        internal static int FirstSegmentReserve(int headLength, int count, bool chunked, int endingLength)
        {
            if (count == 0 || headLength > BatchedBodyBound) return 0;

            var complete = count <= BatchedBodyBound;
            var prefix = complete ? count : FirstSegmentPrefix;
            return prefix + (chunked ? GetChunkPrefixLength(count) + (complete ? CrLf.Length + endingLength : 0) : 0);
        }

        // The bytes an ending write appends behind a chunked body's last chunk.
        internal static int EndingLength(HttpListenerResponse response) => (response.EndingChunk ?? EndOfChunks).Length;

        // One transport segment for the first write, appended behind the encoded head in
        // its own array: chunk-size line, the whole body with its chunk CRLF and the tail
        // when it fits the bound, otherwise a 16 KiB prefix. The caller sends any remaining
        // body bytes directly and ends the chunk. Returns the segment length.
        private static int AppendFirstSegment(
            byte[] segment,
            int headLength,
            byte[] buffer,
            int offset,
            int count,
            bool chunked,
            byte[]? tail,
            out int prefix
        )
        {
            var complete = count <= BatchedBodyBound;
            prefix = complete ? count : FirstSegmentPrefix;
            var position = headLength;

            if (chunked) position += WriteChunkPrefix(segment, position, count);

            Buffer.BlockCopy(buffer, offset, segment, position, prefix);
            position += prefix;

            if (chunked && complete)
            {
                segment[position] = 13;
                segment[position + 1] = 10;
                position += CrLf.Length;

                if (tail != null)
                {
                    Buffer.BlockCopy(tail, 0, segment, position, tail.Length);
                    position += tail.Length;
                }
            }

            return position;
        }

        // One transport segment for a subsequent chunk of at most BatchedBodyBound bytes,
        // with the tail behind it when the write ends the body.
        private static byte[] RentChunkSegment(byte[] buffer, int offset, int count, byte[]? tail, out int length)
        {
            length = GetChunkPrefixLength(count) + count + CrLf.Length + (tail?.Length ?? 0);
            var segment = ArrayPool<byte>.Shared.Rent(length);
            var position = WriteChunkPrefix(segment, 0, count);
            Buffer.BlockCopy(buffer, offset, segment, position, count);
            position += count;
            segment[position] = 13;
            segment[position + 1] = 10;

            if (tail != null) Buffer.BlockCopy(tail, 0, segment, position + CrLf.Length, tail.Length);

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

            if (final)
            {
                bytes[written] = 13;
                bytes[written + 1] = 10;
            }

            return bytes;
        }
    }
}
