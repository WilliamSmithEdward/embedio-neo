using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // Runtime inflation with local, bounded RFC 1950/1951 completion validation.
    internal sealed class DeflateRequestStream : Stream
    {
        private readonly FramedInput _input;
        private readonly DeflateStream _decoder;
        private readonly bool _zlib;
        private const int Reading = 1;
        private const int Disposed = 2;
        private int _state;
        private int _released;
        private bool _failed;
        private bool _finished;
        private ulong _decoded;
        private uint _adler = 1;
#if NET10_0_OR_GREATER
#else
        private byte[]? _singleByte;
#endif
        internal DeflateRequestStream(Stream source, bool zlib)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            _zlib = zlib;
            _input = new FramedInput(source, zlib);
            _decoder = new DeflateStream(_input, CompressionMode.Decompress);
        }
        public override bool CanRead => (Volatile.Read(ref _state) & Disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void Enter()
        {
            var state = Interlocked.CompareExchange(ref _state, Reading, 0);
            if ((state & Disposed) != 0) throw new ObjectDisposedException(nameof(DeflateRequestStream));
            if (state != 0) throw new InvalidOperationException("Concurrent DEFLATE request reads.");
        }
        private void Check()
        {
            if ((Volatile.Read(ref _state) & Disposed) != 0) throw new ObjectDisposedException(nameof(DeflateRequestStream));
            if (_failed) throw HttpException.BadRequest("DEFLATE request decoding has failed.");
        }
        private int UpdateState(int mask, bool set)
        {
            while (true)
            {
                var previous = Volatile.Read(ref _state);
                var next = set ? previous | mask : previous & ~mask;
                if (Interlocked.CompareExchange(ref _state, next, previous) == previous) return previous;
            }
        }
        private void ExitRead()
        {
            if ((UpdateState(Reading, false) & Disposed) != 0) Release();
        }
        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _decoder.Dispose();
        }
        private void Account(byte[] buffer, int offset, int count)
        {
            _decoded += (ulong)count;
            if (_zlib) _adler = Adler32.Update(_adler, buffer, offset, count);
        }
#if NET10_0_OR_GREATER
        private void Account(ReadOnlySpan<byte> buffer)
        {
            _decoded += (ulong)buffer.Length;
            if (_zlib) _adler = Adler32.Update(_adler, buffer);
        }
#endif
        private void VerifyCount()
        {
            _input.Validator.Complete();
            if (_decoded != _input.Validator.DecodedBytes) throw new InvalidDataException("DEFLATE decoded length mismatch.");
        }
        private HttpException Invalid()
        {
            _failed = true;
            return HttpException.BadRequest("Invalid or incomplete DEFLATE request body.");
        }
        private static void Validate(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Validate(buffer, offset, count);
            Enter();
            try
            {
                Check();
                if (count == 0 || _finished) return 0;
                var read = _decoder.Read(buffer, offset, count);
                Check();
                Account(buffer, offset, read);
                if (read == 0)
                {
                    VerifyCount();
                    _input.Finish(_adler);
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { throw Invalid(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { ExitRead(); }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Validate(buffer, offset, count);
            cancellationToken.ThrowIfCancellationRequested();
            Enter();
            try
            {
                Check();
                if (count == 0 || _finished) return 0;
                var read = await _decoder.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                Check();
                Account(buffer, offset, read);
                if (read == 0)
                {
                    VerifyCount();
                    await _input.FinishAsync(_adler, cancellationToken).ConfigureAwait(false);
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { throw Invalid(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { ExitRead(); }
        }
        public override int ReadByte()
        {
#if NET10_0_OR_GREATER
            Span<byte> buffer = stackalloc byte[1];
            return Read(buffer) == 0 ? -1 : buffer[0];
#else
            var buffer = _singleByte ??= new byte[1];
            return Read(buffer, 0, 1) == 0 ? -1 : buffer[0];
#endif
        }
#if NET10_0_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            Enter();
            try
            {
                Check();
                if (buffer.IsEmpty || _finished) return 0;
                var read = _decoder.Read(buffer);
                Check();
                Account(buffer[..read]);
                if (read == 0)
                {
                    VerifyCount();
                    _input.Finish(_adler);
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { throw Invalid(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { ExitRead(); }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enter();
            try
            {
                Check();
                if (buffer.IsEmpty || _finished) return 0;
                var read = await _decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                Check();
                Account(buffer.Span[..read]);
                if (read == 0)
                {
                    VerifyCount();
                    await _input.FinishAsync(_adler, cancellationToken).ConfigureAwait(false);
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { throw Invalid(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { ExitRead(); }
        }
#endif
        public override void Flush() => Check();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var previous = UpdateState(Disposed, true);
                if ((previous & Disposed) == 0)
                {
                    try { _input.Dispose(); }
                    finally { if ((previous & Reading) == 0) Release(); }
                }
            }
            base.Dispose(disposing);
        }

        private sealed class FramedInput(Stream source, bool zlib) : Stream
        {
            private readonly byte[] _metadata = new byte[5];
            private int _metadataCount;
            private bool _header;
            private int _disposed;
            internal DeflateFramingValidator Validator { get; private set; } = new();
            public override bool CanRead => Volatile.Read(ref _disposed) == 0;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            private void ValidateHeader()
            {
                var cmf = _metadata[0];
                var flg = _metadata[1];
                if ((cmf & 15) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0)
                    throw new InvalidDataException("Invalid zlib header.");
                if ((flg & 32) != 0) throw new InvalidDataException("Zlib preset dictionaries are unsupported for this request coding.");
                Validator = new DeflateFramingValidator(1 << ((cmf >> 4) + 8));
                _metadataCount = 0;
                _header = true;
            }
            private void Header()
            {
                if (!zlib || _header) return;
                while (_metadataCount < 2)
                {
                    var read = source.Read(_metadata, _metadataCount, 2 - _metadataCount);
                    if (read == 0) throw new InvalidDataException("Truncated zlib header.");
                    _metadataCount += read;
                }
                ValidateHeader();
            }
            private async Task HeaderAsync(CancellationToken token)
            {
                if (!zlib || _header) return;
                while (_metadataCount < 2)
                {
                    var read = await source.ReadAsync(_metadata, _metadataCount, 2 - _metadataCount, token).ConfigureAwait(false);
                    if (read == 0) throw new InvalidDataException("Truncated zlib header.");
                    _metadataCount += read;
                }
                ValidateHeader();
            }
            private int Consume(byte[] buffer, int offset, int count)
            {
                var consumed = Validator.Feed(buffer, offset, count);
                var remaining = count - consumed;
                if (remaining != 0)
                {
                    if (!zlib || remaining > 4 - _metadataCount) throw new InvalidDataException("Extra data after compressed request body.");
                    Buffer.BlockCopy(buffer, offset + consumed, _metadata, _metadataCount, remaining);
                    _metadataCount += remaining;
                }
                return consumed;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                Validate(buffer, offset, count);
                if (count == 0) return 0;
                Header();
                if (Validator.Finished) return 0;
                return Consume(buffer, offset, source.Read(buffer, offset, count));
            }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Validate(buffer, offset, count);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) return 0;
                await HeaderAsync(cancellationToken).ConfigureAwait(false);
                if (Validator.Finished) return 0;
                return Consume(buffer, offset, await source.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
            }
            private void CheckChecksum(uint actual)
            {
                var expected = ((uint)_metadata[0] << 24) | ((uint)_metadata[1] << 16) | ((uint)_metadata[2] << 8) | _metadata[3];
                if (zlib && expected != actual) throw new InvalidDataException("Invalid zlib Adler-32 checksum.");
            }
            internal void Finish(uint checksum)
            {
                while (zlib && _metadataCount < 4)
                {
                    var read = source.Read(_metadata, _metadataCount, 4 - _metadataCount);
                    if (read == 0) throw new InvalidDataException("Truncated zlib checksum.");
                    _metadataCount += read;
                }
                CheckChecksum(checksum);
                if (source.Read(_metadata, 4, 1) != 0) throw new InvalidDataException("Extra data after compressed request body.");
            }
            internal async Task FinishAsync(uint checksum, CancellationToken token)
            {
                while (zlib && _metadataCount < 4)
                {
                    var read = await source.ReadAsync(_metadata, _metadataCount, 4 - _metadataCount, token).ConfigureAwait(false);
                    if (read == 0) throw new InvalidDataException("Truncated zlib checksum.");
                    _metadataCount += read;
                }
                CheckChecksum(checksum);
                if (await source.ReadAsync(_metadata, 4, 1, token).ConfigureAwait(false) != 0) throw new InvalidDataException("Extra data after compressed request body.");
            }
            internal void Abort()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) source.Dispose();
            }
            protected override void Dispose(bool disposing) { if (disposing) Abort(); base.Dispose(disposing); }
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
