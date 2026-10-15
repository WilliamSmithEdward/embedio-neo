using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // A response output that can end its body in the same transport write as the body's
    // last bytes: for a chunked HTTP/1 response, the last chunk and the terminator leave
    // together instead of as two writes.
    internal interface IEndingBodyOutput
    {
        void WriteEnding(byte[] buffer, int offset, int count);

        Task WriteEndingAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    }

    // Compression over a response output. The compressor writes into a sink that forwards
    // every write and flush to the output unchanged, except the writes the compressor's own
    // disposal makes: those carry its final block, which the sink hands to the output
    // together with the body's ending (IEndingBodyOutput), before the output is disposed.
    // Only the last of those writes is held back: an earlier one is forwarded as soon as the
    // next arrives, so every write still becomes its own chunk. The bytes on the wire, chunk
    // boundaries included, are the same as with the compressor writing to the output
    // directly; a small compressed response takes one transport write fewer.
    internal sealed class CompressedResponseStream : Stream
    {
        private readonly Stream _compressor;
        private readonly Sink _sink;
        private readonly Stream _output;
        private bool _disposed;

        private CompressedResponseStream(Stream output, CompressionMethod method)
        {
            _output = output;
            _sink = new Sink(output);
            _compressor = method switch
            {
#if NET10_0_OR_GREATER
                // A dynamic response is compressed again on every request, so Brotli runs at quality 1,
                // as in ASP.NET Core: a quarter to half the CPU of gzip's default level, 6 to 17% larger.
                CompressionMethod.Brotli => new BrotliStream(_sink, CompressionLevel.Fastest, leaveOpen: true),
#endif
                CompressionMethod.Gzip => new GZipStream(_sink, CompressionMode.Compress, leaveOpen: true),
                CompressionMethod.Deflate => new DeflateStream(_sink, CompressionMode.Compress, leaveOpen: true),
                _ => throw new ArgumentOutOfRangeException(nameof(method)),
            };
        }

        // The output itself for identity (or an unsupported coding on this asset), otherwise
        // the compressor over it. Disposing the result disposes the output, as before.
        internal static Stream Open(Stream output, CompressionMethod method)
        {
            switch (method)
            {
#if NET10_0_OR_GREATER
                case CompressionMethod.Brotli:
#endif
                case CompressionMethod.Gzip:
                case CompressionMethod.Deflate:
                    return new CompressedResponseStream(output, method);
                default:
                    return output;
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => !_disposed && _compressor.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => _compressor.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _compressor.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _compressor.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _compressor.WriteAsync(buffer, offset, count, cancellationToken);
        public override void WriteByte(byte value) => _compressor.WriteByte(value);
#if NET10_0_OR_GREATER
        public override void Write(ReadOnlySpan<byte> buffer) => _compressor.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => _compressor.WriteAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _sink.BeginEnding();
                await _compressor.DisposeAsync().ConfigureAwait(false);
                await _sink.CompleteEndingAsync().ConfigureAwait(false);
            }
            finally
            {
                _sink.Dispose();
                _output.Dispose();
            }
        }
#endif

        // The compressor's final block is captured by the sink and written with the body's
        // ending; the output is disposed whether or not that write succeeds, as it was when
        // the compressor owned it.
        protected override void Dispose(bool disposing)
        {
            if (!disposing || _disposed) return;
            _disposed = true;
            try
            {
                _sink.BeginEnding();
                _compressor.Dispose();
                _sink.CompleteEnding();
            }
            finally
            {
                _sink.Dispose();
                _output.Dispose();
            }
        }

        private sealed class Sink : Stream
        {
            // Borrowed: the owner disposes the output after the sink.
            private readonly BorrowedResource<Stream> _output;
            private bool _ending;
            private byte[]? _captured;
            private int _capturedLength;

            internal Sink(Stream output) { _output = new BorrowedResource<Stream>(output); }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Flush() => _output.Value.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => _output.Value.FlushAsync(cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (!_ending)
                {
                    _output.Value.Write(buffer, offset, count);
                    return;
                }
                if (count == 0) return;
                ForwardCaptured();
                Capture(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (!_ending) return _output.Value.WriteAsync(buffer, offset, count, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
                if (count == 0) return Task.CompletedTask;
                if (_capturedLength != 0) return ForwardThenCaptureAsync(buffer, offset, count, cancellationToken);
                Capture(buffer, offset, count);
                return Task.CompletedTask;
            }

            // A further write during the ending: the one held so far leaves as an ordinary
            // write (its own chunk), and this one is held instead.
            private async Task ForwardThenCaptureAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await _output.Value.WriteAsync(_captured!, 0, _capturedLength, cancellationToken).ConfigureAwait(false);
                ClearCaptured();
                Capture(buffer, offset, count);
            }

            private void ForwardCaptured()
            {
                if (_capturedLength == 0) return;
                _output.Value.Write(_captured!, 0, _capturedLength);
                ClearCaptured();
            }

            private void ClearCaptured()
            {
                Array.Clear(_captured!, 0, _capturedLength);
                _capturedLength = 0;
            }

#if NET10_0_OR_GREATER
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (!_ending)
                {
                    _output.Value.Write(buffer);
                    return;
                }
                if (buffer.IsEmpty) return;
                ForwardCaptured();
                Capture(buffer);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_ending) return _output.Value.WriteAsync(buffer, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled(cancellationToken);
                if (buffer.IsEmpty) return default;
                if (_capturedLength != 0) return new ValueTask(ForwardThenCaptureAsync(buffer, cancellationToken));
                Capture(buffer.Span);
                return default;
            }

            private async Task ForwardThenCaptureAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
            {
                await _output.Value.WriteAsync(_captured!, 0, _capturedLength, cancellationToken).ConfigureAwait(false);
                ClearCaptured();
                Capture(buffer.Span);
            }
#endif

            internal void BeginEnding() => _ending = true;

            // Hands the captured final block to the output with the body's ending, or as an
            // ordinary write when the output cannot end the body itself.
            internal void CompleteEnding()
            {
                if (_captured == null || _capturedLength == 0) return;
                if (_output.Value is IEndingBodyOutput ending) ending.WriteEnding(_captured, 0, _capturedLength);
                else _output.Value.Write(_captured, 0, _capturedLength);
                Release();
            }

            internal async Task CompleteEndingAsync()
            {
                if (_captured == null || _capturedLength == 0) return;
                if (_output.Value is IEndingBodyOutput ending) await ending.WriteEndingAsync(_captured, 0, _capturedLength, CancellationToken.None).ConfigureAwait(false);
                else await _output.Value.WriteAsync(_captured, 0, _capturedLength, CancellationToken.None).ConfigureAwait(false);
                Release();
            }

            private void Capture(byte[] buffer, int offset, int count)
            {
                if (count == 0) return;
                var target = Reserve(count);
                Buffer.BlockCopy(buffer, offset, target, _capturedLength, count);
                _capturedLength += count;
            }

#if NET10_0_OR_GREATER
            private void Capture(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty) return;
                var target = Reserve(bytes.Length);
                bytes.CopyTo(target.AsSpan(_capturedLength));
                _capturedLength += bytes.Length;
            }
#endif

            private byte[] Reserve(int count)
            {
                var needed = checked(_capturedLength + count);
                if (_captured != null && _captured.Length >= needed) return _captured;
                var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _captured == null ? 0 : _captured.Length * 2));
                if (_captured != null)
                {
                    Buffer.BlockCopy(_captured, 0, grown, 0, _capturedLength);
                    Release(_captured, _capturedLength);
                }
                _captured = grown;
                return grown;
            }

            private void Release()
            {
                if (_captured == null) return;
                Release(_captured, _capturedLength);
                _captured = null;
                _capturedLength = 0;
            }

            // Only the bytes written here are cleared before the lease returns.
            private static void Release(byte[] buffer, int length)
            {
                Array.Clear(buffer, 0, length);
                ArrayPool<byte>.Shared.Return(buffer);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Release();
                base.Dispose(disposing);
            }
        }
    }
}
