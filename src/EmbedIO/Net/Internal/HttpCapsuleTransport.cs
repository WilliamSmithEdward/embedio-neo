using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal
{
    internal readonly struct HttpCapsuleHeader
    {
        internal HttpCapsuleHeader(long type, long length) { Type = type; Length = length; }
        public long Type { get; }
        public long Length { get; }
    }

    // RFC 9297 framing for an already negotiated carrier. The caller owns the
    // stream and supplies cancellation/resource policy. Payloads are streamed:
    // no allocation depends on the declared capsule length. This codec alone
    // neither negotiates a tunnel nor advertises QUIC datagram support.
    internal sealed class HttpCapsuleTransport
    {
        private readonly Stream _stream;
        private readonly byte[] _readHeader = new byte[8];
        private readonly byte[] _writeHeader = new byte[16];
        private int _reading;
        private int _writing;
        private bool _readFailed;
        private bool _writeFailed;
        private bool _readEnded;
        private bool _readStarted;
        private bool _writeStarted;
        private long _readRemaining;
        private long _writeRemaining;

        internal HttpCapsuleTransport(Stream stream) { _stream = stream ?? throw new ArgumentNullException(nameof(stream)); }

        internal async Task<HttpCapsuleHeader?> ReadHeaderAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Enter(ref _reading);
            var started = false;
            try
            {
                if (_readFailed) throw new IOException("Capsule input is no longer usable.");
                if (_readRemaining != 0) throw new InvalidOperationException("Consume or skip the current capsule before reading another header.");
                if (_readEnded) return null;
                started = true;
                var type = await ReadIntegerAsync(true, token).ConfigureAwait(false);
                if (!type.HasValue) { _readEnded = true; _readStarted = false; return null; }
                var length = await ReadIntegerAsync(false, token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("Missing capsule length.");
                _readRemaining = length;
                _readStarted = true;
                return new HttpCapsuleHeader(type.Value, length);
            }
            catch { if (started) _readFailed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        internal async Task<int> ReadPayloadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            ValidateBuffer(buffer, offset, count);
            token.ThrowIfCancellationRequested();
            Enter(ref _reading);
            try
            {
                if (_readFailed) throw new IOException("Capsule input is no longer usable.");
                if (!_readStarted) throw new InvalidOperationException("Read a capsule header first.");
                return await ReadPayloadCoreAsync(buffer, offset, count, token).ConfigureAwait(false);
            }
            finally { Volatile.Write(ref _reading, 0); }
        }

        // Unknown types can be discarded without retaining their values or
        // waiting for the entire value to fit an underlying flow-control window.
        internal async Task SkipPayloadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Enter(ref _reading);
            byte[]? scratch = null;
            try
            {
                if (_readFailed) throw new IOException("Capsule input is no longer usable.");
                if (!_readStarted) throw new InvalidOperationException("Read a capsule header first.");
                if (_readRemaining == 0) return;
                scratch = ArrayPool<byte>.Shared.Rent(1024);
                while (_readRemaining != 0)
                    await ReadPayloadCoreAsync(scratch, 0, 1024, token).ConfigureAwait(false);
            }
            finally
            {
                if (scratch != null) ArrayPool<byte>.Shared.Return(scratch, true);
                Volatile.Write(ref _reading, 0);
            }
        }

        private async Task<int> ReadPayloadCoreAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (count == 0 || _readRemaining == 0) return 0;
            try
            {
                var read = await _stream.ReadAsync(buffer, offset, (int)Math.Min(count, _readRemaining), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Truncated capsule value.");
                _readRemaining -= read;
                return read;
            }
            catch { _readFailed = true; throw; }
        }

        private async Task<long?> ReadIntegerAsync(bool allowEnd, CancellationToken token)
        {
            var read = await _stream.ReadAsync(_readHeader, 0, 1, token).ConfigureAwait(false);
            if (read == 0)
            {
                if (allowEnd) return null;
                throw new EndOfStreamException("Missing capsule integer.");
            }
            var width = 1 << (_readHeader[0] >> 6);
            while (read < width)
            {
                var count = await _stream.ReadAsync(_readHeader, read, width - read, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated capsule integer.");
                read += count;
            }
            var offset = 0;
            return QuicInteger.Read(_readHeader, ref offset, width);
        }

        internal async Task WriteHeaderAsync(long type, long length, CancellationToken token)
        {
            if (type < 0 || type > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(type));
            if (length < 0 || length > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(length));
            token.ThrowIfCancellationRequested();
            Enter(ref _writing);
            var started = false;
            try
            {
                if (_writeFailed) throw new IOException("Capsule output is no longer usable.");
                if (_writeRemaining != 0) throw new InvalidOperationException("Finish the current capsule before writing another header.");
                var count = QuicInteger.Write(_writeHeader, 0, type);
                count += QuicInteger.Write(_writeHeader, count, length);
                started = true;
                await _stream.WriteAsync(_writeHeader, 0, count, token).ConfigureAwait(false);
                _writeRemaining = length;
                _writeStarted = true;
            }
            catch { if (started) _writeFailed = true; throw; }
            finally { Volatile.Write(ref _writing, 0); }
        }

        internal async Task WritePayloadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            ValidateBuffer(buffer, offset, count);
            token.ThrowIfCancellationRequested();
            Enter(ref _writing);
            var started = false;
            try
            {
                if (_writeFailed) throw new IOException("Capsule output is no longer usable.");
                if (!_writeStarted) throw new InvalidOperationException("Write a capsule header first.");
                if (count > _writeRemaining) throw new ArgumentException("Payload exceeds the declared capsule length.", nameof(count));
                if (count == 0) return;
                started = true;
                await _stream.WriteAsync(buffer, offset, count, token).ConfigureAwait(false);
                _writeRemaining -= count;
            }
            catch { if (started) _writeFailed = true; throw; }
            finally { Volatile.Write(ref _writing, 0); }
        }

        private static void Enter(ref int busy)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent capsule operations in one direction.");
        }

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
