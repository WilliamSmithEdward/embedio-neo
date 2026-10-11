using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    internal static class RequestCodingChain
    {
        private const int MaximumLayers = 8;
        internal static string TrimValue(string value)
        {
            var start = 0;
            var end = value.Length;
            while (start < end && value[start] is ' ' or '\t') start++;
            while (end > start && value[end - 1] is ' ' or '\t') end--;
            return start == 0 && end == value.Length ? value : value.Substring(start, end - start);
        }
        internal static bool TryOpen(Stream source, string value, bool enabled, out Stream? decoded)
        {
            decoded = null;
            var methods = new CompressionMethod[MaximumLayers];
            var count = 0;
            var start = 0;
            while (start <= value.Length)
            {
                var end = value.IndexOf(",", start, StringComparison.Ordinal);
                if (end < 0) end = value.Length;
                var tokenStart = start;
                var tokenEnd = end;
                while (tokenStart < tokenEnd && value[tokenStart] is ' ' or '\t') tokenStart++;
                while (tokenEnd > tokenStart && value[tokenEnd - 1] is ' ' or '\t') tokenEnd--;
                if (tokenStart != tokenEnd)
                {
                    var length = tokenEnd - tokenStart;
                    bool Is(string name) => length == name.Length && string.Compare(value, tokenStart, name, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
                    if (!Is(CompressionMethodNames.None))
                    {
                        if (!enabled || count == MaximumLayers) return false;
                        if (Is(CompressionMethodNames.Gzip)) methods[count++] = CompressionMethod.Gzip;
                        else if (Is(CompressionMethodNames.Deflate)) methods[count++] = CompressionMethod.Deflate;
#if NET10_0_OR_GREATER
                        else if (Is(CompressionMethodNames.Brotli)) methods[count++] = CompressionMethod.Brotli;
#endif
                        else return false;
                    }
                }
                if (end == value.Length) break;
                start = end + 1;
            }
            if (count == 0) { decoded = source; return true; }
            var layers = new Stream[count];
            var current = source;
            try
            {
                for (var index = count - 1; index >= 0; index--)
                {
                    current = methods[index] switch
                    {
                        CompressionMethod.Gzip => new GZipStream(current, CompressionMode.Decompress),
                        CompressionMethod.Deflate => new DeflateRequestStream(current, false),
#if NET10_0_OR_GREATER
                        CompressionMethod.Brotli => new BrotliRequestStream(current),
#endif
                        _ => throw new InvalidOperationException("Unexpected request coding.")
                    };
                    layers[count - 1 - index] = current;
                }
                decoded = new CodingChainRequestStream(layers);
                return true;
            }
            catch { current.Dispose(); throw; }
        }
    }

    internal sealed class CodingChainRequestStream(Stream[] layers) : Stream
    {
        private readonly Stream _decoded = layers[layers.Length - 1];
        private int _disposed;
        private bool _finished;
        private bool _failed;
#if NET10_0_OR_GREATER
#else
        private byte[]? _singleByte;
#endif
        public override bool CanRead => Volatile.Read(ref _disposed) == 0 && _decoded.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void CheckDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(CodingChainRequestStream));
            if (_failed) throw HttpException.BadRequest("Request content-coding chain has failed.");
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            CheckDisposed();
            try
            {
                var read = _decoded.Read(buffer, offset, count);
                if (read == 0 && count != 0 && !_finished)
                {
                    VerifyCompletion();
                }
                return read;
            }
            catch (InvalidDataException) { _failed = true; throw HttpException.BadRequest("Invalid request content-coding chain."); }
            catch (HttpException) { _failed = true; throw; }
        }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            CheckDisposed();
            try
            {
                var read = await _decoded.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                if (read == 0 && count != 0 && !_finished)
                {
                    var probe = new byte[1];
                    for (var index = layers.Length - 2; index >= 0; index--)
                        if (await layers[index].ReadAsync(probe, 0, 1, cancellationToken).ConfigureAwait(false) != 0)
                            throw HttpException.BadRequest("Extra decoded data after request coding layer.");
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { _failed = true; throw HttpException.BadRequest("Invalid request content-coding chain."); }
            catch (HttpException) { _failed = true; throw; }
        }
        private void VerifyCompletion()
        {
            for (var index = layers.Length - 2; index >= 0; index--)
                if (layers[index].ReadByte() != -1) throw HttpException.BadRequest("Extra decoded data after request coding layer.");
            _finished = true;
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
            CheckDisposed();
            try
            {
                var read = _decoded.Read(buffer);
                if (read == 0 && !buffer.IsEmpty && !_finished) VerifyCompletion();
                return read;
            }
            catch (InvalidDataException) { _failed = true; throw HttpException.BadRequest("Invalid request content-coding chain."); }
            catch (HttpException) { _failed = true; throw; }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckDisposed();
            try
            {
                var read = await _decoded.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0 && !buffer.IsEmpty && !_finished)
                {
                    var probe = new byte[1];
                    for (var index = layers.Length - 2; index >= 0; index--)
                        if (await layers[index].ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                            throw HttpException.BadRequest("Extra decoded data after request coding layer.");
                    _finished = true;
                }
                return read;
            }
            catch (InvalidDataException) { _failed = true; throw HttpException.BadRequest("Invalid request content-coding chain."); }
            catch (HttpException) { _failed = true; throw; }
        }
#endif
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) _decoded.Dispose();
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
