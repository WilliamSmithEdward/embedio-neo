using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Stream lifetime belongs to the connection. One read may run concurrently
    // with output. Output is an ordered queue drained by one flusher at a time:
    // a write is committed when the flusher takes it, so commits (flow checks,
    // SETTINGS application, HPACK encoding) happen in wire order, and every
    // committed write is written whole. Writes taken together share one
    // transport write, bounded by OutputBatchBytes.
    internal sealed class Http2FrameTransport : IDisposable
    {
        // Large enough to coalesce a burst of small responses, small enough that
        // one connection's pending output stays bounded.
        internal const int OutputBatchBytes = 65536;

        private readonly EmbedIO.Internal.BorrowedResource<Stream> _stream;
        private readonly byte[] _header = new byte[9];
        private readonly int _receiveMaximum;
        private readonly ArrayPool<byte>? _dataPool;
        private readonly object _outputSync = new();
        private readonly Queue<Http2OutputWrite> _queue = new();
        // Owned by the current flusher only.
        private readonly List<Http2OutputWrite> _batch = new();
        private readonly Http2OutputBuffer _output = new();
        private bool _flushing;
        private bool _outputClosed;
        private int _reading;
        private bool _readFailed;
        private bool _writeFailed;

        internal Http2FrameTransport(Stream stream, int receiveMaximum = 16384)
            : this(stream, receiveMaximum, null) { }

        internal Http2FrameTransport(Stream stream, int receiveMaximum, ArrayPool<byte>? dataPool)
        {
            _dataPool = dataPool;
            _stream = new EmbedIO.Internal.BorrowedResource<Stream>(stream ?? throw new ArgumentNullException(nameof(stream)));
            ValidateMaximum(receiveMaximum);
            _receiveMaximum = receiveMaximum;
        }

        // Interrupts shared output once a write is committed. Request tokens
        // only cancel writes that are still queued.
        internal CancellationToken ConnectionToken { get; set; }

        internal async Task<Http2Frame?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Concurrent HTTP/2 reads.");
            byte[]? rented = null;
            try
            {
                if (_readFailed) throw new IOException("HTTP/2 input is no longer usable.");
                var count = await _stream.Value.ReadAsync(_header, 0, 9, token).ConfigureAwait(false);
                if (count == 0) return null;
                await ReadRemaining(_header, count, 9, token).ConfigureAwait(false);
                var length = (_header[0] << 16) | (_header[1] << 8) | _header[2];
                if (length > _receiveMaximum) throw new Http2ProtocolException(6, "HTTP/2 frame exceeds negotiated maximum.");
                var streamId = ((_header[5] & 127) << 24) | (_header[6] << 16) | (_header[7] << 8) | _header[8];
                byte[] payload;
                if (length == 0) payload = Array.Empty<byte>();
                else if (_header[3] == 0 && _dataPool != null) payload = rented = _dataPool.Rent(length);
                else payload = new byte[length];
                await ReadRemaining(payload, 0, length, token).ConfigureAwait(false);
                if (rented == null || _dataPool == null) return new Http2Frame(_header[3], _header[4], streamId, payload);
                var frame = Http2Frame.OwnData(_header[4], streamId, rented, length, _dataPool);
                rented = null;
                return frame;
            }
            catch (IOException error) when (token.IsCancellationRequested
                && error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.OperationAborted)
            {
                // Some socket cancellation races surface as wrapped error 995
                // instead of OCE. Preserve the token and original transport error.
                _readFailed = true;
                throw new OperationCanceledException("HTTP/2 transport read canceled.", error, token);
            }
            catch { _readFailed = true; throw; }
            finally
            {
                if (rented != null) _dataPool?.Return(rented, true);
                Volatile.Write(ref _reading, 0);
            }
        }

        private async Task ReadRemaining(byte[] buffer, int offset, int end, CancellationToken token)
        {
            while (offset < end)
            {
                var count = await _stream.Value.ReadAsync(buffer, offset, end - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated HTTP/2 frame.");
                offset += count;
            }
        }

        internal bool IsWriteFailed => Volatile.Read(ref _writeFailed);

        // The token cancels the write while queued and interrupts shared output once committed.
        internal Task WriteAsync(Http2Frame[] frames, int peerMaximum, CancellationToken token)
            => QueueWriteAsync(Http2FrameWrite.Create(frames, peerMaximum, token, token, null, null));

        internal Task<bool> WriteRequestAsync(Http2Frame[] frames, int peerMaximum, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl flow)
            => QueueWriteAsync(Http2FrameWrite.Create(frames, peerMaximum, requestToken, connectionToken, flow, null));

        // SETTINGS and its ACK form one wire-order transaction. DATA reserved
        // before that transaction must still fit at commitment.
        internal Task WriteSettingsAsync(Http2Frame[] frames, int peerMaximum, Action apply, CancellationToken token)
            => QueueWriteAsync(Http2FrameWrite.Create(frames, peerMaximum, token, token, null, apply));

        // Completes with false when the write committed nothing because it was
        // no longer admissible, for example DATA whose stream window closed.
        internal Task<bool> QueueWriteAsync(Http2OutputWrite write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            if (write.Invalid != null) return Task.FromException<bool>(write.Invalid);
            lock (_outputSync)
            {
                if (_writeFailed || _outputClosed) return Task.FromException<bool>(new IOException("HTTP/2 output is no longer usable."));
                if (write.Token.IsCancellationRequested) return Task.FromCanceled<bool>(write.Token);
                if (!_flushing)
                {
                    _flushing = true;
                    _batch.Add(write);
                }
                else
                {
                    write.Owner = this;
                    write.Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    // A cancellation racing this registration runs inline on this
                    // thread; the output lock is reentrant and the flusher skips it.
                    if (write.Token.CanBeCanceled)
                        write.Registration = write.Token.Register(static state =>
                        {
                            var queued = (Http2OutputWrite)(state ?? throw new InvalidOperationException("Missing queued write."));
                            queued.Owner?.CancelQueued(queued);
                        }, write);
                    _queue.Enqueue(write);
                    return write.Completion.Task;
                }
            }
            // The first writer flushes inline, so uncontended output takes no thread hop.
            return FlushAsync(write);
        }

        private void CancelQueued(Http2OutputWrite write)
        {
            lock (_outputSync)
            {
                if (write.Committed || write.Canceled) return;
                // Left in the queue; the flusher skips it.
                write.Canceled = true;
            }
            write.Completion?.TrySetCanceled(write.Token);
        }

        // Runs while _flushing is owned by this call. The leading write completes
        // through the returned task; queued writes through their completions.
        private async Task<bool> FlushAsync(Http2OutputWrite? leading)
        {
            var leadingResult = false;
            Exception? leadingFailure = null;
            while (true)
            {
                lock (_outputSync)
                {
                    // Take queued writes in order up to the batch bound. A write
                    // larger than the bound still goes alone.
                    var estimate = 0;
                    foreach (var write in _batch) estimate += write.EstimatedBytes;
                    while (_queue.Count != 0 && (estimate == 0 || estimate + _queue.Peek().EstimatedBytes <= OutputBatchBytes))
                    {
                        var write = _queue.Dequeue();
                        if (write.Canceled) continue;
                        write.Committed = true;
                        estimate += write.EstimatedBytes;
                        _batch.Add(write);
                    }
                    if (_batch.Count == 0)
                    {
                        _flushing = false;
                        _output.Release();
                        break;
                    }
                }
                // Committed writes ignore later request cancellation. Dispose
                // outside the output lock: a running callback waits for that lock.
                foreach (var write in _batch) write.Registration.Dispose();

                var writeToken = ConnectionToken;
                CancellationTokenSource? linked = null;
                var started = false;
                try
                {
                    foreach (var write in _batch)
                    {
                        var mark = _output.Length;
                        try
                        {
                            write.Result = write.Commit(_output);
                            if (write.WriteToken != writeToken && write.WriteToken.CanBeCanceled)
                            {
                                // Rare: a write with its own deadline interrupts the shared batch.
                                linked?.Dispose();
                                linked = CancellationTokenSource.CreateLinkedTokenSource(writeToken, write.WriteToken);
                                writeToken = linked.Token;
                            }
                        }
                        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                        {
                            // Nothing of a rejected write reaches the wire.
                            _output.Truncate(mark);
                            write.Failure = error;
                        }
                    }
                    if (_output.Length != 0)
                    {
                        started = true;
                        await _stream.Value.WriteAsync(_output.Buffer, 0, _output.Length, writeToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    FailOutput(error, started);
                    foreach (var write in _batch) write.Failure ??= error;
                }
                finally
                {
                    linked?.Dispose();
                    _output.Clear();
                }
                foreach (var write in _batch)
                {
                    if (write == leading)
                    {
                        leadingResult = write.Result;
                        leadingFailure = write.Failure;
                        continue;
                    }
                    if (write.Failure != null) write.Completion?.TrySetException(write.Failure);
                    else write.Completion?.TrySetResult(write.Result);
                }
                _batch.Clear();
                if (leading != null)
                {
                    leading = null;
                    // Hand the remaining queue to the thread pool so the leading
                    // caller is not delayed by its siblings' output.
                    lock (_outputSync)
                    {
                        if (_queue.Count == 0 || _writeFailed)
                        {
                            _flushing = false;
                            _output.Release();
                        }
                        else _ = Task.Run(() => FlushAsync(null));
                    }
                    break;
                }
            }
            if (leadingFailure != null) throw Rethrow(leadingFailure);
            return leadingResult;
        }

        private static Exception Rethrow(Exception error)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return error;
        }

        // Once any bytes of a batch may be on the wire, output is terminal.
        private void FailOutput(Exception error, bool started)
        {
            Http2OutputWrite[] pending;
            lock (_outputSync)
            {
                if (started) _writeFailed = true;
                if (!_writeFailed) return;
                pending = _queue.ToArray();
                _queue.Clear();
                foreach (var write in pending) write.Committed = true;
            }
            foreach (var write in pending)
            {
                write.Registration.Dispose();
                write.Completion?.TrySetException(new IOException("HTTP/2 output is no longer usable.", error));
            }
        }

        // The connection joins all I/O before disposing. Fail anything still queued.
        public void Dispose()
        {
            Http2OutputWrite[] pending;
            lock (_outputSync)
            {
                _outputClosed = true;
                pending = _queue.ToArray();
                _queue.Clear();
                foreach (var write in pending) write.Committed = true;
                if (!_flushing) _output.Release();
            }
            foreach (var write in pending)
            {
                write.Registration.Dispose();
                write.Completion?.TrySetException(new ObjectDisposedException(nameof(Http2FrameTransport)));
            }
        }

        internal static void ValidateMaximum(int maximum)
        {
            if (maximum < 16384 || maximum > 16777215) throw new ArgumentOutOfRangeException(nameof(maximum));
        }
    }

    // One queued unit of output. Commit runs on the flusher in wire order and
    // appends complete frames, or throws without leaving partial output behind.
    internal abstract class Http2OutputWrite
    {
        protected Http2OutputWrite(CancellationToken token, CancellationToken writeToken)
        { Token = token; WriteToken = writeToken; }
        internal CancellationToken Token { get; }
        internal CancellationToken WriteToken { get; }
        internal Exception? Invalid { get; set; }
        internal abstract int EstimatedBytes { get; }
        internal abstract bool Commit(Http2OutputBuffer output);

        // Transport state, guarded by its output lock.
        internal Http2FrameTransport? Owner;
        internal TaskCompletionSource<bool>? Completion;
        internal CancellationTokenRegistration Registration;
        internal bool Committed;
        internal bool Canceled;
        internal bool Result;
        internal Exception? Failure;
    }

    internal sealed class Http2FrameWrite : Http2OutputWrite
    {
        private readonly Http2Frame[] _frames;
        private readonly Http2SendFlowControl? _flow;
        private readonly Action? _apply;
        private readonly int _bytes;

        private Http2FrameWrite(Http2Frame[] frames, CancellationToken token, CancellationToken writeToken,
            Http2SendFlowControl? flow, Action? apply, int bytes) : base(token, writeToken)
        { _frames = frames; _flow = flow; _apply = apply; _bytes = bytes; }

        internal static Http2FrameWrite Create(Http2Frame[] frames, int peerMaximum, CancellationToken token,
            CancellationToken writeToken, Http2SendFlowControl? flow, Action? apply)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            var bytes = 0;
            Exception? invalid = null;
            try
            {
                Http2FrameTransport.ValidateMaximum(peerMaximum);
                foreach (var frame in frames)
                {
                    if (frame == null) throw new ArgumentException("Missing frame.", nameof(frames));
                    if (frame.PayloadLength > peerMaximum) throw new ArgumentException("Outbound frame exceeds peer maximum.", nameof(frames));
                    frame.ValidateShape();
                    bytes += frame.PayloadLength + 9;
                }
            }
            catch (Exception error) when (error is ArgumentException || error is IOException) { invalid = error; }
            return new Http2FrameWrite(frames, token, writeToken, flow, apply, bytes) { Invalid = invalid };
        }

        internal override int EstimatedBytes => _bytes;

        internal override bool Commit(Http2OutputBuffer output)
        {
            _apply?.Invoke();
            if (_flow != null && !_flow.CanSendReserved(_frames)) return false;
            foreach (var frame in _frames)
            {
                output.WriteFrameHeader(frame.PayloadLength, frame.Type, frame.Flags, frame.StreamId);
                output.Write(frame.Payload, frame.PayloadOffset, frame.PayloadLength);
            }
            return true;
        }
    }

    // Pooled, growable output storage for one batch. Used bytes are cleared
    // before reuse because they can hold response content.
    internal sealed class Http2OutputBuffer
    {
        private byte[] _buffer = Array.Empty<byte>();
        internal byte[] Buffer => _buffer;
        internal int Length { get; private set; }

        internal void WriteFrameHeader(int length, byte type, byte flags, int streamId)
        {
            Ensure(9);
            var buffer = _buffer;
            var offset = Length;
            buffer[offset] = (byte)(length >> 16); buffer[offset + 1] = (byte)(length >> 8); buffer[offset + 2] = (byte)length;
            buffer[offset + 3] = type; buffer[offset + 4] = flags;
            buffer[offset + 5] = (byte)(streamId >> 24); buffer[offset + 6] = (byte)(streamId >> 16);
            buffer[offset + 7] = (byte)(streamId >> 8); buffer[offset + 8] = (byte)streamId;
            Length = offset + 9;
        }

        internal void Write(byte[] source, int offset, int count)
        {
            if (count == 0) return;
            Ensure(count);
            System.Buffer.BlockCopy(source, offset, _buffer, Length, count);
            Length += count;
        }

        internal void Truncate(int length)
        {
            Array.Clear(_buffer, length, Length - length);
            Length = length;
        }

        internal void Clear() => Truncate(0);

        // Returns storage to the pool between busy periods.
        internal void Release()
        {
            Clear();
            if (_buffer.Length == 0) return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
        }

        private void Ensure(int count)
        {
            var required = (long)Length + count;
            if (required <= _buffer.Length) return;
            if (required > int.MaxValue) throw new IOException("HTTP/2 output batch is too large.");
            var next = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, Math.Max(4096, (long)_buffer.Length * 2)));
            System.Buffer.BlockCopy(_buffer, 0, next, 0, Length);
            if (_buffer.Length != 0)
            {
                Array.Clear(_buffer, 0, Length);
                ArrayPool<byte>.Shared.Return(_buffer);
            }
            _buffer = next;
        }
    }
}
