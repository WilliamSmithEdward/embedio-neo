#if NET10_0_OR_GREATER
using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Validates a single Brotli stream against the already framed HTTP body.
    // The decoder remains the runtime codec; completion and ownership are local.
    internal sealed class BrotliRequestStream : Stream
    {
        private readonly Stream _source;
        private BrotliDecoder _decoder;
        private byte[]? _input;
        private int _offset;
        private int _count;
        private const int Reading = 1;
        private const int Disposed = 2;
        private int _state;
        private int _released;
        private bool _finished;
        private bool _verified;
        private bool _failed;

        internal BrotliRequestStream(Stream source)
        {
            ArgumentNullException.ThrowIfNull(source);
            _source = source;
        }

        public override bool CanRead => (Volatile.Read(ref _state) & Disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private byte[] Input => _input ??= ArrayPool<byte>.Shared.Rent(16384);

        private void EnterRead()
        {
            var state = Interlocked.CompareExchange(ref _state, Reading, 0);
            ObjectDisposedException.ThrowIf((state & Disposed) != 0, this);
            if (state != 0)
                throw new InvalidOperationException("Concurrent Brotli request reads are not supported.");
        }

        private void CheckState()
        {
            ObjectDisposedException.ThrowIf((Volatile.Read(ref _state) & Disposed) != 0, this);
            if (_failed) throw new IOException("Brotli request stream has failed.");
        }

        private void ExitRead()
        {
            var previous = Interlocked.And(ref _state, ~Reading);
            if ((previous & Disposed) != 0) ReleaseResources();
        }

        private void ReleaseResources()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _decoder.Dispose();
            var input = Interlocked.Exchange(ref _input, null);
            if (input != null) ArrayPool<byte>.Shared.Return(input, true);
        }

        private HttpException Invalid(string message)
        {
            _failed = true;
            return HttpException.BadRequest(message);
        }

        private bool Decode(Span<byte> destination, out int written)
        {
            CheckState();
            if (_finished) { written = 0; return true; }
            var status = _decoder.Decompress(Input.AsSpan(_offset, _count), destination, out var consumed, out written);
            _offset += consumed;
            _count -= consumed;
            if (status == OperationStatus.InvalidData) throw Invalid("Invalid Brotli request body.");
            if (status == OperationStatus.Done) { _finished = true; return true; }
            if (written != 0) return true;
            if (status != OperationStatus.NeedMoreData)
                throw Invalid("Brotli decoder did not make progress.");
            if (_count != 0 && _offset != 0) Buffer.BlockCopy(Input, _offset, Input, 0, _count);
            _offset = 0;
            if (_count == Input.Length) throw Invalid("Brotli decoder could not consume its input.");
            return false;
        }

        public override int ReadByte()
        {
            Span<byte> buffer = stackalloc byte[1];
            return Read(buffer) == 0 ? -1 : buffer[0];
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBufferArguments(buffer, offset, count);
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            EnterRead();
            try
            {
                CheckState();
                if (buffer.IsEmpty) return 0;
                int written;
                while (!Decode(buffer, out written))
                {
                    var read = _source.Read(Input, _count, Input.Length - _count);
                    if (read == 0) throw Invalid("Truncated Brotli request body.");
                    _count += read;
                }
                if (_finished && !_verified)
                {
                    if (_count != 0 || _source.Read(Input, 0, 1) != 0)
                        throw Invalid("Extra data after Brotli request body.");
                    _verified = true;
                }
                return written;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                _failed = true;
                throw;
            }
            finally { ExitRead(); }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBufferArguments(buffer, offset, count);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnterRead();
            try
            {
                CheckState();
                if (buffer.IsEmpty) return 0;
                int written;
                while (!Decode(buffer.Span, out written))
                {
                    var read = await _source.ReadAsync(Input.AsMemory(_count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw Invalid("Truncated Brotli request body.");
                    _count += read;
                }
                if (_finished && !_verified)
                {
                    if (_count != 0 || await _source.ReadAsync(Input.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                        throw Invalid("Extra data after Brotli request body.");
                    _verified = true;
                }
                return written;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                _failed = true;
                throw;
            }
            finally { ExitRead(); }
        }

        public override void Flush() => CheckState();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var previous = Interlocked.Or(ref _state, Disposed);
                if ((previous & Disposed) == 0)
                {
                    try { _source.Dispose(); }
                    finally { if ((previous & Reading) == 0) ReleaseResources(); }
                }
            }
            base.Dispose(disposing);
        }
    }
}
#endif
