using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // The connection reader supplies DATA content only (padding is accounted for
    // separately). Small frames are coalesced so retained memory scales with body
    // bytes, not the number or padded size of the wire frames.
    internal sealed class Http2RequestBody : Stream
    {
        private sealed class Chunk
        {
            internal readonly byte[] Bytes = ArrayPool<byte>.Shared.Rent(4096);
            internal int Read;
            internal int Written;
        }
        private readonly object _sync = new();
        private readonly Queue<Chunk> _chunks = new();
        private readonly Action<int> _consumed;
        private readonly int _streamId;
        private readonly long? _length;
        private Chunk? _tail;
        private TaskCompletionSource<bool>? _changed;
        private Exception? _failure;
        private int _buffered;
        private int _reader;
        private long _received;
        private bool _complete;
        private bool _disposed;

        internal Http2RequestBody(int streamId, long? length, Action<int> consumed)
        {
            if (streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            _streamId = streamId; _length = length;
            _consumed = consumed ?? throw new ArgumentNullException(nameof(consumed));
        }
        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        internal void Append(byte[] bytes, int offset, int count, bool endStream)
        {
            Validate(bytes, offset, count);
            lock (_sync)
            {
                ThrowIfUnavailable();
                if (_complete) throw new Http2ProtocolException(5, "DATA after body completion.", _streamId);
                if (count > long.MaxValue - _received || (_length.HasValue && count > _length.Value - _received))
                    throw new Http2ProtocolException(1, "DATA exceeds Content-Length.", _streamId);
                if (endStream && _length.HasValue && _received + count != _length.Value)
                    throw new Http2ProtocolException(1, "DATA does not match Content-Length.", _streamId);
                if (count > 65535 - _buffered) throw new Http2ProtocolException(11, "Unread request body exceeds receive capacity.", _streamId);
                _received += count;
                _buffered += count;
                while (count > 0)
                {
                    if (_tail == null || _tail.Written == 4096)
                    {
                        _tail = new Chunk();
                        _chunks.Enqueue(_tail);
                    }
                    var copied = Math.Min(count, 4096 - _tail.Written);
                    Buffer.BlockCopy(bytes, offset, _tail.Bytes, _tail.Written, copied);
                    _tail.Written += copied; offset += copied; count -= copied;
                }
                _complete = endStream;
                Pulse();
            }
        }

        internal void Fail(Exception error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            int discarded;
            lock (_sync)
            {
                if (_disposed || _failure != null) return;
                _failure = error;
                discarded = Clear();
                Pulse();
            }
            if (discarded != 0) _consumed(discarded);
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            Validate(buffer, offset, count);
            if (Interlocked.CompareExchange(ref _reader, 1, 0) != 0) throw new InvalidOperationException("A request body read is already pending.");
            try
            {
                while (true)
                {
                    Task? changed = null;
                    var copied = 0;
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
                        ThrowIfUnavailable();
                        if (count == 0) return 0;
                        if (_buffered > 0)
                        {
                            var remaining = Math.Min(count, _buffered);
                            copied = remaining;
                            while (remaining > 0)
                            {
                                var chunk = _chunks.Peek();
                                var part = Math.Min(remaining, chunk.Written - chunk.Read);
                                Buffer.BlockCopy(chunk.Bytes, chunk.Read, buffer, offset, part);
                                chunk.Read += part; offset += part; remaining -= part;
                                if (chunk.Read == chunk.Written)
                                {
                                    _chunks.Dequeue();
                                    if (ReferenceEquals(_tail, chunk)) _tail = null;
                                    ArrayPool<byte>.Shared.Return(chunk.Bytes, true);
                                }
                            }
                            _buffered -= copied;
                        }
                        else if (_complete) return 0;
                        else changed = (_changed ??= NewSignal()).Task;
                    }
                    if (copied != 0) { _consumed(copied); return copied; }
                    var signal = changed ?? throw new InvalidOperationException("Missing pending body signal.");
                    if (!token.CanBeCanceled) await signal.ConfigureAwait(false);
                    else
                    {
                        var canceled = NewSignal();
                        using (token.Register(state => ((TaskCompletionSource<bool>)(state ?? throw new InvalidOperationException("Missing cancellation signal."))).TrySetResult(true), canceled))
                        {
                            await Task.WhenAny(signal, canceled.Task).ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                        }
                    }
                }
            }
            finally { Volatile.Write(ref _reader, 0); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                int discarded;
                lock (_sync)
                {
                    if (_disposed) return;
                    _disposed = true;
                    discarded = Clear();
                    Pulse();
                }
                if (discarded != 0) _consumed(discarded);
            }
            base.Dispose(disposing);
        }
        private int Clear()
        {
            while (_chunks.Count != 0) ArrayPool<byte>.Shared.Return(_chunks.Dequeue().Bytes, true);
            _tail = null;
            var discarded = _buffered; _buffered = 0;
            return discarded;
        }
        private void ThrowIfUnavailable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(Http2RequestBody));
            if (_failure != null) throw new IOException("HTTP/2 request body failed.", _failure);
        }
        private void Pulse() { var changed = _changed; _changed = null; changed?.TrySetResult(true); }
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static void Validate(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
