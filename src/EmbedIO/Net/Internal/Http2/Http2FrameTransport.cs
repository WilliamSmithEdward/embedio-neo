using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Stream lifetime belongs to the connection. One read may run concurrently
    // with one serialized write batch. Header-block batches cannot interleave.
    internal sealed class Http2FrameTransport
    {
        private readonly Stream _stream;
        private readonly byte[] _header = new byte[9];
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly int _receiveMaximum;
        private int _reading;
        private bool _readFailed;
        private bool _writeFailed;

        internal Http2FrameTransport(Stream stream, int receiveMaximum = 16384)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            ValidateMaximum(receiveMaximum);
            _receiveMaximum = receiveMaximum;
        }

        internal async Task<Http2Frame?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Concurrent HTTP/2 reads.");
            try
            {
                if (_readFailed) throw new IOException("HTTP/2 input is no longer usable.");
                var count = await _stream.ReadAsync(_header, 0, 9, token).ConfigureAwait(false);
                if (count == 0) return null;
                await ReadRemaining(_header, count, 9, token).ConfigureAwait(false);
                var length = (_header[0] << 16) | (_header[1] << 8) | _header[2];
                if (length > _receiveMaximum) throw new Http2ProtocolException(6, "HTTP/2 frame exceeds negotiated maximum.");
                var streamId = ((_header[5] & 127) << 24) | (_header[6] << 16) | (_header[7] << 8) | _header[8];
                var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                await ReadRemaining(payload, 0, length, token).ConfigureAwait(false);
                return new Http2Frame(_header[3], _header[4], streamId, payload);
            }
            catch { _readFailed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }

        private async Task ReadRemaining(byte[] buffer, int offset, int end, CancellationToken token)
        {
            while (offset < end)
            {
                var count = await _stream.ReadAsync(buffer, offset, end - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated HTTP/2 frame.");
                offset += count;
            }
        }

        internal async Task WriteAsync(Http2Frame[] frames, int peerMaximum, CancellationToken token)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            ValidateMaximum(peerMaximum);
            var capacity = 9;
            foreach (var frame in frames)
            {
                if (frame == null) throw new ArgumentException("Missing frame.", nameof(frames));
                if (frame.Payload.Length > peerMaximum) throw new ArgumentException("Outbound frame exceeds peer maximum.", nameof(frames));
                frame.ValidateShape();
                capacity = Math.Max(capacity, frame.Payload.Length + 9);
            }
            await _writeGate.WaitAsync(token).ConfigureAwait(false);
            byte[]? buffer = null;
            try
            {
                if (_writeFailed) throw new IOException("HTTP/2 output is no longer usable.");
                buffer = ArrayPool<byte>.Shared.Rent(capacity);
                foreach (var frame in frames)
                {
                    var length = frame.Payload.Length;
                    buffer[0] = (byte)(length >> 16); buffer[1] = (byte)(length >> 8); buffer[2] = (byte)length;
                    buffer[3] = frame.Type; buffer[4] = frame.Flags;
                    buffer[5] = (byte)(frame.StreamId >> 24); buffer[6] = (byte)(frame.StreamId >> 16);
                    buffer[7] = (byte)(frame.StreamId >> 8); buffer[8] = (byte)frame.StreamId;
                    Buffer.BlockCopy(frame.Payload, 0, buffer, 9, length);
                    await _stream.WriteAsync(buffer, 0, length + 9, token).ConfigureAwait(false);
                }
            }
            catch { _writeFailed = true; throw; }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, true);
                _writeGate.Release();
            }
        }

        private static void ValidateMaximum(int maximum)
        {
            if (maximum < 16384 || maximum > 16777215) throw new ArgumentOutOfRangeException(nameof(maximum));
        }
    }
}
