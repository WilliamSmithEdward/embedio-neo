using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal.Http3
{
    internal class Http3ProtocolException : IOException
    {
        internal Http3ProtocolException(long errorCode, string message) : base(message) { ErrorCode = errorCode; }
        public long ErrorCode { get; }
    }

    internal readonly struct Http3FrameHeader
    {
        internal Http3FrameHeader(long type, long length) { Type = type; Length = length; }
        public long Type { get; }
        public long Length { get; }
    }

    // One reader per QUIC stream. The caller owns the transport and cancellation deadline.
    // DATA and unknown frames can be streamed without allocating their declared length.
    internal sealed class Http3FrameReader
    {
        private readonly BorrowedResource<Stream> _source;
        private readonly byte[] _integer = new byte[16];
        private long _remaining;
        private int _reading;
        private bool _failed;
        private bool _ended;

        internal Http3FrameReader(Stream source)
        { _source = new BorrowedResource<Stream>(source ?? throw new ArgumentNullException(nameof(source))); }
        public long Remaining => _remaining;

        private void Enter(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent HTTP/3 reads.");
            if (_failed) { Volatile.Write(ref _reading, 0); throw new IOException("HTTP/3 stream input is no longer usable."); }
        }

        internal async ValueTask<Http3FrameHeader?> ReadHeaderAsync(CancellationToken token)
        {
            Enter(token);
            try
            {
                if (_remaining != 0) throw new InvalidOperationException("Consume the current HTTP/3 frame before reading another header.");
                if (_ended) return null;
                // Every frame header has at least a one-byte type and a one-byte
                // length. Each read asks only for bytes the header must still
                // contain, so the payload stays in the transport.
                var count = await ReadSourceAsync(_integer, 0, 2, token).ConfigureAwait(false);
                if (count == 0) { _ended = true; return null; }
                var typeLength = 1 << (_integer[0] >> 6);
                count = await FillAsync(count, typeLength + 1, typeLength, token).ConfigureAwait(false);
                var lengthLength = 1 << (_integer[typeLength] >> 6);
                await FillAsync(count, typeLength + lengthLength, -1, token).ConfigureAwait(false);
                var offset = 0;
                var type = QuicInteger.Read(_integer, ref offset, typeLength);
                var length = QuicInteger.Read(_integer, ref offset, typeLength + lengthLength);
                _remaining = length;
                return new Http3FrameHeader(type, length);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        // Reads until `required` header bytes are buffered. A stream that ends at
        // `typeEnd`, after a complete type and before any length byte, has a
        // truncated header; any other early end truncates an integer.
        private async ValueTask<int> FillAsync(int count, int required, int typeEnd, CancellationToken token)
        {
            while (count < required)
            {
                var read = await ReadSourceAsync(_integer, count, required - count, token).ConfigureAwait(false);
                if (read == 0)
                    throw new Http3ProtocolException(0x106, count == typeEnd ? "Truncated HTTP/3 frame header." : "Truncated HTTP/3 frame integer.");
                count += read;
            }
            return count;
        }

        // The memory overload completes without allocating when the transport
        // already holds data; the legacy target only has the array overload.
        private ValueTask<int> ReadSourceAsync(byte[] bytes, int offset, int count, CancellationToken token)
#if NET10_0_OR_GREATER
            => _source.Value.ReadAsync(bytes.AsMemory(offset, count), token);
#else
            => new(_source.Value.ReadAsync(bytes, offset, count, token));
#endif

        internal async ValueTask<int> ReadPayloadAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Enter(token);
            try { return await ReadPayloadCoreAsync(bytes, offset, count, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        private async ValueTask<int> ReadPayloadCoreAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (_remaining == 0 || count == 0) return 0;
            var read = await ReadSourceAsync(bytes, offset, (int)Math.Min(count, _remaining), token).ConfigureAwait(false);
            if (read == 0) throw new Http3ProtocolException(0x106, "Truncated HTTP/3 frame payload.");
            _remaining -= read;
            return read;
        }

        internal async ValueTask<byte[]> ReadBufferedPayloadAsync(int maximum, CancellationToken token)
        {
            if (maximum < 0) throw new ArgumentOutOfRangeException(nameof(maximum));
            Enter(token);
            try
            {
                if (_remaining > maximum) throw new Http3ProtocolException(0x107, "HTTP/3 metadata exceeds the configured buffer limit.");
                var bytes = _remaining == 0 ? Array.Empty<byte>() : new byte[(int)_remaining];
                var offset = 0;
                while (_remaining > 0) offset += await ReadPayloadCoreAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                return bytes;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        internal async ValueTask SkipPayloadAsync(CancellationToken token)
        {
            Enter(token);
            byte[]? bytes = null;
            try
            {
                if (_remaining == 0) return;
                bytes = ArrayPool<byte>.Shared.Rent(4096);
                while (_remaining > 0) await ReadPayloadCoreAsync(bytes, 0, 4096, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally
            {
                if (bytes != null) ArrayPool<byte>.Shared.Return(bytes, true);
                Volatile.Write(ref _reading, 0);
            }
        }
    }
}
