using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Quic
{
    internal sealed class QuicStreamFailure(ulong errorCode, bool remote) : IOException("QUIC stream was aborted.")
    {
        internal ulong ErrorCode { get; } = errorCode;
        internal bool Remote { get; } = remote;
    }

    // The connection owner alone calls Receive/Process. Application operations
    // synchronize their buffers with that owner and wake it without a timer poll.
    internal sealed class AsyncQuicStream(ulong id, Action ready) : Stream
    {
        private sealed class ReadRequest(
            AsyncQuicStream owner,
            Memory<byte> memory,
            ulong code,
            CancellationToken token
        )
        {
            internal Memory<byte> Memory = memory;

            internal CancellationToken Token { get; } = token;
            internal ulong Code { get; } = code;
            internal TaskCompletionSource<int> Completion { get; } = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );

            internal void Cancel() => owner.CancelRead(this);
        }

        private sealed class WriteRequest(
            AsyncQuicStream owner,
            ReadOnlyMemory<byte> memory,
            bool fin,
            bool observe,
            CancellationToken token
        )
        {
            internal ReadOnlyMemory<byte> Memory = memory;
            internal int Position;
            internal bool Progress;

            internal bool Fin { get; } = fin;
            internal CancellationToken Token { get; } = token;
            internal TaskCompletionSource? Completion { get; } = observe
                ? new(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;

            internal void Cancel() => owner.CancelWrite(this);
        }

        private readonly object _gate = new();
        private TaskCompletionSource? _readsClosed;
        private TaskCompletionSource? _writesClosed;
        private byte[]? _buffer;
        private int _head;
        private int _count;
        private int _consumed;
        private ulong _readOffset;
        private bool _fin;
        private bool _accepted = (id & 1) != 0;
        private bool _ended;
        private bool _writeAcknowledged;
        private bool _disposed;
        private bool _retired;
        private Exception? _readError;
        private Exception? _writeError;
        private Exception? _connectionError;
        private ReadRequest? _read;
        private WriteRequest? _write;
        private byte[]? _reliablePrefix;
        private bool _prefixInstalled;
        private bool _writeStarted;
        private ulong? _abortRead;
        private ulong? _abortWrite;

        internal ulong Id { get; } = id;
        internal bool Unidirectional => (Id & 2) != 0;
        private bool Readable => (Id & 3) != 3;
        private bool Writable => (Id & 3) != 2;

        internal Task ReadsClosed
        {
            get
            {
                lock (_gate)
                {
                    if (!Readable) return Task.CompletedTask;

                    var completion = _readsClosed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    FinishRead();

                    if (_connectionError != null) completion.TrySetException(_connectionError);

                    return completion.Task;
                }
            }
        }

        internal Task WritesClosed
        {
            get
            {
                lock (_gate)
                {
                    if (!Writable) return Task.CompletedTask;

                    var completion = _writesClosed ??= new(TaskCreationOptions.RunContinuationsAsynchronously);

                    if (_writeError != null) completion.TrySetException(_writeError);

                    else if (_writeAcknowledged) completion.TrySetResult();

                    else if (_connectionError != null) completion.TrySetException(_connectionError);

                    return completion.Task;
                }
            }
        }

        internal int Buffered
        {
            get
            {
                lock (_gate) return _count;
            }
        }

        internal bool CanRetire
        {
            get
            {
                lock (_gate)
                    return _accepted
                        && (!Readable || _fin)
                        && _count == 0
                        && _consumed == 0
                        && _read == null
                        && (!Writable || _writeAcknowledged)
                        && _write == null;
            }
        }

        public override bool CanRead
        {
            get
            {
                lock (_gate) return Readable && !_disposed;
            }
        }

        public override bool CanWrite
        {
            get
            {
                lock (_gate) return Writable && !_disposed;
            }
        }

        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Flush()
        {
            lock (_gate)
            {
                CheckOpen();

                if (!Writable) throw new NotSupportedException();
            }
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flush();
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ReadOwnedAsync(buffer, 0x10c, cancellationToken);

        internal ValueTask<int> ReadOwnedAsync(Memory<byte> buffer, ulong cancellationCode, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateCode(cancellationCode);
            ReadRequest request;

            lock (_gate)
            {
                CheckOpen();

                if (!Readable) throw new NotSupportedException();

                if (_read != null) throw new InvalidOperationException("A read is already pending.");

                if (buffer.Length == 0) return ValueTask.FromResult(0);

                if (_count != 0)
                {
                    var copied = Copy(buffer.Span);
                    ready();
                    return ValueTask.FromResult(copied);
                }

                if (_readError != null) throw _readError;

                if (_fin) return ValueTask.FromResult(0);

                request = new ReadRequest(this, buffer, cancellationCode, token);
                _read = request;
            }

            return AwaitReadAsync(request);
        }

        private static async ValueTask<int> AwaitReadAsync(ReadRequest request)
        {
            using var registration = request.Token.UnsafeRegister(
                static state => (state as ReadRequest)?.Cancel(),
                request
            );
            return await request.Completion.Task.ConfigureAwait(false);
        }

        private int Copy(Span<byte> destination)
        {
            var buffer = _buffer ?? throw new InvalidOperationException("Missing stream buffer.");
            var count = Math.Min(_count, destination.Length);
            var first = Math.Min(count, buffer.Length - _head);
            buffer
                .AsSpan(_head, first)
                .CopyTo(destination);
            buffer
                .AsSpan(0, count - first)
                .CopyTo(destination[first..]);
            buffer
                .AsSpan(_head, first)
                .Clear();
            buffer
                .AsSpan(0, count - first)
                .Clear();
            _head = (_head + count) % buffer.Length;
            _count -= count;
            _consumed += count;
            _readOffset += (ulong)count;
            FinishRead();
            return count;
        }

        internal void Receive(byte[] bytes, bool fin)
        {
            lock (_gate)
            {
                if (_connectionError != null) return;

                if (_disposed || _readError != null)
                {
                    _consumed += bytes.Length;
                    ready();
                    return;
                }

                if (bytes.Length > 65536 - _count) throw new InvalidDataException("Async receive window exceeded.");

                if (bytes.Length != 0)
                {
                    _buffer ??= new byte[65536];
                    var tail = (_head + _count) % _buffer.Length;
                    var first = Math.Min(bytes.Length, _buffer.Length - tail);
                    bytes
                        .AsSpan(0, first)
                        .CopyTo(_buffer.AsSpan(tail));
                    bytes
                        .AsSpan(first)
                        .CopyTo(_buffer);
                    _count += bytes.Length;
                }

                _fin |= fin;
                CompleteRead();
            }
        }

        private void CompleteRead()
        {
            var request = _read;

            if (request != null && (_count != 0 || _fin || _readError != null))
            {
                _read = null;

                if (_count != 0)
                {
                    var count = Copy(request.Memory.Span);
                    request.Memory = default;
                    request.Completion.TrySetResult(count);
                    ready();
                }

                else
                {
                    request.Memory = default;

                    if (_readError != null) request.Completion.TrySetException(_readError);

                    else request.Completion.TrySetResult(0);
                }
            }

            FinishRead();
        }

        private void FinishRead()
        {
            if (!_fin || _count != 0) return;

            if (_readError != null) _readsClosed?.TrySetException(_readError);

            else _readsClosed?.TrySetResult();

            _buffer = null;
            _head = 0;
        }

        private void CancelRead(ReadRequest request)
        {
            lock (_gate)
            {
                if (_read != request) return;

                _read = null;
                request.Memory = default;
                AbortReadCore(request.Code);
                request.Completion.TrySetCanceled(request.Token);
                ready();
            }
        }

        internal void PeerReset(ulong code, ulong reliableSize)
        {
            lock (_gate)
            {
                var keep = (int)Math.Min((ulong)_count, reliableSize > _readOffset ? reliableSize - _readOffset : 0);
                var discard = _count - keep;

                if (discard != 0)
                {
                    var buffer = _buffer ?? throw new InvalidOperationException("Missing reset buffer.");
                    var tail = (_head + keep) % buffer.Length;
                    var first = Math.Min(discard, buffer.Length - tail);
                    buffer
                        .AsSpan(tail, first)
                        .Clear();
                    buffer
                        .AsSpan(0, discard - first)
                        .Clear();
                    _count = keep;
                    _consumed += discard;
                    ready();
                }

                _readError ??= new QuicStreamFailure(code, true);
                _fin = true;
                CompleteRead();
            }
        }

        internal void AbortRead(ulong code)
        {
            ValidateCode(code);

            lock (_gate)
            {
                CheckOpen();

                if (!Readable) throw new NotSupportedException();

                AbortReadCore(code);
                CompleteRead();
                ready();
            }
        }

        private void AbortReadCore(ulong code)
        {
            if (_fin && _count == 0) return;

            _readError ??= new QuicStreamFailure(code, false);
            _fin = true;
            _abortRead ??= code;
            DiscardBuffer();
            FinishRead();
        }

        private void DiscardBuffer()
        {
            _consumed += _count;
            _count = 0;
            _head = 0;

            if (_buffer != null) Array.Clear(_buffer);

            _buffer = null;
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => WriteOwnedAsync(buffer, false, cancellationToken);

        internal ValueTask WriteOwnedAsync(ReadOnlyMemory<byte> buffer, bool fin, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            WriteRequest request;

            lock (_gate)
            {
                CheckWritable();

                if (buffer.Length == 0 && !fin) return ValueTask.CompletedTask;

                request = new WriteRequest(this, buffer, fin, true, token);
                _write = request;
                _ended = fin;
                ready();
            }

            return AwaitWriteAsync(request);
        }

        private static async ValueTask AwaitWriteAsync(WriteRequest request)
        {
            using var registration = request.Token.UnsafeRegister(
                static state => (state as WriteRequest)?.Cancel(),
                request
            );
            await (request.Completion
                ?? throw new InvalidOperationException("Missing write completion.")).Task.ConfigureAwait(false);
        }

        private static void ValidateCode(ulong code)
        {
            if (code > StreamReassembly.MaximumOffset) throw new ArgumentOutOfRangeException(nameof(code));
        }

        private void CheckOpen()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_connectionError != null) throw _connectionError;
        }

        private void CheckWritable()
        {
            CheckOpen();

            if (!Writable) throw new NotSupportedException();

            if (_writeError != null) throw _writeError;

            if (_write != null) throw new InvalidOperationException("A write is already pending.");

            if (_ended) throw new InvalidOperationException("Stream writes have completed.");
        }

        internal void SetReliablePrefix(ReadOnlySpan<byte> prefix)
        {
            if (prefix.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(prefix));

            lock (_gate)
            {
                CheckWritable();

                if (_prefixInstalled || _writeStarted)
                    throw new InvalidOperationException("Reliable prefix must precede stream writes.");

                _reliablePrefix = prefix.ToArray();
                _prefixInstalled = true;
                ready();
            }
        }

        internal void CompleteWrites()
        {
            lock (_gate)
            {
                CheckOpen();

                if (!Writable) throw new NotSupportedException();

                if (_ended) return;

                CheckWritable();
                _ended = true;
                _write = new WriteRequest(this, default, true, false, default);
                ready();
            }
        }

        private void CancelWrite(WriteRequest request)
        {
            lock (_gate)
            {
                if (_write != request) return;

                _write = null;
                request.Memory = default;

                if (request.Progress) AbortWriteCore(0x10c);

                else _ended = false;

                request.Completion?.TrySetCanceled(request.Token);
                ready();
            }
        }

        internal void PeerStopped(ulong code)
        {
            lock (_gate)
            {
                _writeError ??= new QuicStreamFailure(code, true);
                FinishWriteError();
            }
        }

        internal void AbortWrite(ulong code)
        {
            ValidateCode(code);

            lock (_gate)
            {
                CheckOpen();

                if (!Writable) throw new NotSupportedException();

                AbortWriteCore(code);
                ready();
            }
        }

        private void AbortWriteCore(ulong code)
        {
            if (_writeAcknowledged) return;

            _abortWrite ??= code;
            _writeError ??= new QuicStreamFailure(code, false);
            FinishWriteError();
        }

        private void FinishWriteError()
        {
            _ended = true;
            var request = _write;
            _write = null;

            if (request != null)
            {
                request.Memory = default;
                request.Completion?.TrySetException(
                    _writeError ?? throw new InvalidOperationException("Missing write failure.")
                );
            }

            _writesClosed?.TrySetException(
                _writeError ?? throw new InvalidOperationException("Missing write failure.")
            );
        }

        internal void AcknowledgeWrites()
        {
            lock (_gate)
            {
                _writeAcknowledged = true;

                if (_writeError != null) _writesClosed?.TrySetException(_writeError);

                else _writesClosed?.TrySetResult();
            }
        }

        internal void Process(StreamOperations operations)
        {
            lock (_gate)
            {
                if (_connectionError != null || _retired) return;

                if (_consumed != 0)
                {
                    operations.Consume(Id, _consumed);
                    _consumed = 0;
                }

                if (_abortRead.HasValue)
                {
                    operations.AbortRead(Id, _abortRead.Value);
                    _abortRead = null;
                }

                if (_reliablePrefix is { } prefix)
                {
                    operations.ReliablePrefix(Id, prefix);
                    _reliablePrefix = null;
                }

                if (_abortWrite.HasValue)
                {
                    operations.AbortWrite(Id, _abortWrite.Value);
                    _abortWrite = null;
                }

                for (var index = 0; index < 2 && _write != null; index++)
                {
                    var request = _write;
                    var count = Math.Min(8192, request.Memory.Length - request.Position);
                    var end = request.Position + count == request.Memory.Length;

                    if (!operations.TryWrite(Id, request.Memory.Slice(request.Position, count), end && request.Fin))
                        return;

                    _writeStarted = true;
                    request.Progress = true;
                    request.Position += count;

                    if (end)
                    {
                        request.Memory = default;
                        _write = null;
                        request.Completion?.TrySetResult();
                    }

                    else ready();
                }
            }
        }

        internal void Accept()
        {
            lock (_gate) _accepted = true;

            ready();
        }

        public override async ValueTask DisposeAsync()
        {
            Task? graceful;

            lock (_gate)
                graceful = _ended && _writeError == null && !_writeAcknowledged && Writable ? WritesClosed : null;

            try
            {
                if (graceful != null) await graceful.ConfigureAwait(false);
            }

            catch (IOException) { }

            finally
            {
                Dispose();
            }
        }

        internal void Retire()
        {
            lock (_gate)
            {
                if (!CanRetire) throw new InvalidOperationException("Async stream retired before completion.");

                _retired = true;
                _buffer = null;
            }
        }

        internal void CloseConnection(Exception error)
        {
            lock (_gate)
            {
                _connectionError ??= error;
                _retired = true;
                _reliablePrefix = null;
                _consumed = 0;
                DiscardBuffer();
                _consumed = 0;
                var read = _read;
                _read = null;

                if (read != null)
                {
                    read.Memory = default;
                    read.Completion.TrySetException(error);
                }

                var write = _write;
                _write = null;

                if (write != null)
                {
                    write.Memory = default;
                    write.Completion?.TrySetException(error);
                }

                if (Readable) _readsClosed?.TrySetException(error);

                if (Writable) _writesClosed?.TrySetException(error);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (!_disposed)
                    {
                        if (!_retired && _connectionError == null)
                        {
                            if (Readable) AbortReadCore(0x10c);

                            if (Writable) AbortWriteCore(0x10c);

                            CompleteRead();
                            ready();
                        }

                        _disposed = true;
                    }
                }
            }

            base.Dispose(disposing);
        }
    }
}
