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
        private readonly byte[] _integer = new byte[8];
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

        internal async Task<Http3FrameHeader?> ReadHeaderAsync(CancellationToken token)
        {
            Enter(token);
            try
            {
                if (_remaining != 0) throw new InvalidOperationException("Consume the current HTTP/3 frame before reading another header.");
                if (_ended) return null;
                var type = await ReadIntegerAsync(true, token).ConfigureAwait(false);
                if (!type.HasValue) { _ended = true; return null; }
                var length = await ReadIntegerAsync(false, token).ConfigureAwait(false)
                    ?? throw new Http3ProtocolException(0x106, "Missing HTTP/3 frame length.");
                _remaining = length;
                return new Http3FrameHeader(type.Value, length);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        private async Task<long?> ReadIntegerAsync(bool allowEnd, CancellationToken token)
        {
            var count = await _source.Value.ReadAsync(_integer, 0, 1, token).ConfigureAwait(false);
            if (count == 0)
            {
                if (allowEnd) return null;
                throw new Http3ProtocolException(0x106, "Truncated HTTP/3 frame header.");
            }
            var length = 1 << (_integer[0] >> 6);
            while (count < length)
            {
                var read = await _source.Value.ReadAsync(_integer, count, length - count, token).ConfigureAwait(false);
                if (read == 0) throw new Http3ProtocolException(0x106, "Truncated HTTP/3 frame integer.");
                count += read;
            }
            var offset = 0;
            return QuicInteger.Read(_integer, ref offset, length);
        }

        internal async Task<int> ReadPayloadAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            Enter(token);
            try { return await ReadPayloadCoreAsync(bytes, offset, count, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        private async Task<int> ReadPayloadCoreAsync(byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (_remaining == 0 || count == 0) return 0;
            var read = await _source.Value.ReadAsync(bytes, offset, (int)Math.Min(count, _remaining), token).ConfigureAwait(false);
            if (read == 0) throw new Http3ProtocolException(0x106, "Truncated HTTP/3 frame payload.");
            _remaining -= read;
            return read;
        }

        internal async Task<byte[]> ReadBufferedPayloadAsync(int maximum, CancellationToken token)
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

        internal async Task SkipPayloadAsync(CancellationToken token)
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
