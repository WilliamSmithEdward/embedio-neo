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

        // Opens the decoders for a comma-separated Content-Encoding value. Returns false
        // for a coding that is unknown, disabled or beyond the layer budget; a value that
        // names only identity yields the source itself.
        internal static bool TryOpen(Stream source, string value, bool enabled, out Stream? decoded)
        {
            decoded = null;
            if (!TryReadCodings(value, enabled, out var codings, out var count)) return false;
            decoded = count == 0 ? source : OpenLayers(source, codings, count);
            return true;
        }

        // Reads the codings in wire order into four-bit slots of one ulong (eight layers at
        // most; the recognized codings are the enum values 1 to 3), so reading allocates
        // nothing. Empty elements and identity add no layer; any other coding needs decoding
        // to be enabled and a free layer. The loop ends at the last element, since end stays
        // below value.Length after every comma.
        private static bool TryReadCodings(string value, bool enabled, out ulong codings, out int count)
        {
            codings = 0;
            count = 0;
            var start = 0;
            while (true)
            {
                var end = value.IndexOf(",", start, StringComparison.Ordinal);
                if (end < 0) end = value.Length;
                var tokenStart = start;
                var tokenEnd = end;
                while (tokenStart < tokenEnd && value[tokenStart] is ' ' or '\t') tokenStart++;
                while (tokenEnd > tokenStart && value[tokenEnd - 1] is ' ' or '\t') tokenEnd--;
                if (tokenStart != tokenEnd && !TokenIs(value, tokenStart, tokenEnd - tokenStart, CompressionMethodNames.None))
                {
                    if (!enabled || count == MaximumLayers) return false;
                    if (!TryGetMethod(value, tokenStart, tokenEnd - tokenStart, out var method)) return false;
                    codings |= (ulong)method << (4 * count);
                    count++;
                }
                if (end == value.Length) return true;
                start = end + 1;
            }
        }

        private static bool TryGetMethod(string value, int start, int length, out CompressionMethod method)
        {
            if (TokenIs(value, start, length, CompressionMethodNames.Gzip)) { method = CompressionMethod.Gzip; return true; }
            if (TokenIs(value, start, length, CompressionMethodNames.Deflate)) { method = CompressionMethod.Deflate; return true; }
#if NET10_0_OR_GREATER
            if (TokenIs(value, start, length, CompressionMethodNames.Brotli)) { method = CompressionMethod.Brotli; return true; }
#endif
            method = default;
            return false;
        }

        private static bool TokenIs(string value, int start, int length, string name)
            => length == name.Length && string.Compare(value, start, name, 0, length, StringComparison.OrdinalIgnoreCase) == 0;

        // Wraps the source from the last listed coding inward, so reads undo the codings
        // in reverse wire order. A layer that fails to open disposes what was opened so far.
        private static Stream OpenLayers(Stream source, ulong codings, int count)
        {
            var layers = new Stream[count];
            var current = source;
            try
            {
                for (var index = count - 1; index >= 0; index--)
                {
                    current = OpenLayer((CompressionMethod)((codings >> (4 * index)) & 0xF), current);
                    layers[count - 1 - index] = current;
                }
                return new CodingChainRequestStream(layers);
            }
            catch { current.Dispose(); throw; }
        }

        private static Stream OpenLayer(CompressionMethod method, Stream inner) => method switch
        {
            CompressionMethod.Gzip => new GZipStream(inner, CompressionMode.Decompress),
            CompressionMethod.Deflate => new DeflateRequestStream(inner, false),
#if NET10_0_OR_GREATER
            CompressionMethod.Brotli => new BrotliRequestStream(inner),
#endif
            _ => throw new InvalidOperationException("Unexpected request coding.")
        };
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
