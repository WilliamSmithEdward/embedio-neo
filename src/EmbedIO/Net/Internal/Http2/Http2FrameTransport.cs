using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Stream lifetime belongs to the connection. One read may run concurrently
    // with one serialized write batch. Header-block batches cannot interleave.
    internal sealed class Http2FrameTransport : IDisposable
    {
        private readonly EmbedIO.Internal.BorrowedResource<Stream> _stream;
        private readonly byte[] _header = new byte[9];
        private readonly Http2OutputWriter _writer;
        private readonly int _receiveMaximum;
        private readonly ArrayPool<byte>? _dataPool;
        private int _reading;
        private bool _readFailed;


        internal Http2FrameTransport(Stream stream, int receiveMaximum = 16384)
            : this(stream, receiveMaximum, null) { }

        internal Http2FrameTransport(Stream stream, int receiveMaximum, ArrayPool<byte>? dataPool)
        {
            _dataPool = dataPool;
            _stream = new EmbedIO.Internal.BorrowedResource<Stream>(stream ?? throw new ArgumentNullException(nameof(stream)));
            _writer = new Http2OutputWriter(_stream);
            ValidateMaximum(receiveMaximum);
            _receiveMaximum = receiveMaximum;
        }

        internal async Task<Http2Frame?> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _reading, 1) != 0) throw new InvalidOperationException("Concurrent HTTP/2 reads.");
            byte[]? rented = null;
            try
            {
                if (_readFailed) throw new IOException("HTTP/2 input is no longer usable.");
                var count = await _stream.Value.ReadAsync(_header, 0, 9, token).ConfigureAwait(false);
                if (count == 0) return null;
                await ReadRemaining(_header, count, 9, token).ConfigureAwait(false);
                var length = (_header[0] << 16) | (_header[1] << 8) | _header[2];
                if (length > _receiveMaximum) throw new Http2ProtocolException(6, "HTTP/2 frame exceeds negotiated maximum.");
                var streamId = ((_header[5] & 127) << 24) | (_header[6] << 16) | (_header[7] << 8) | _header[8];
                byte[] payload;
                if (length == 0) payload = Array.Empty<byte>();
                else if (_header[3] == 0 && _dataPool != null) payload = rented = _dataPool.Rent(length);
                else payload = new byte[length];
                await ReadRemaining(payload, 0, length, token).ConfigureAwait(false);
                if (rented == null || _dataPool == null) return new Http2Frame(_header[3], _header[4], streamId, payload);
                var frame = Http2Frame.OwnData(_header[4], streamId, rented, length, _dataPool);
                rented = null;
                return frame;
            }
            catch (IOException error) when (token.IsCancellationRequested
                && error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.OperationAborted)
            {
                // Some socket cancellation races surface as wrapped error 995
                // instead of OCE. Preserve the token and original transport error.
                _readFailed = true;
                throw new OperationCanceledException("HTTP/2 transport read canceled.", error, token);
            }
            catch { _readFailed = true; throw; }
            finally
            {
                if (rented != null) _dataPool?.Return(rented, true);
                Volatile.Write(ref _reading, 0);
            }
        }

        private async Task ReadRemaining(byte[] buffer, int offset, int end, CancellationToken token)
        {
            while (offset < end)
            {
                var count = await _stream.Value.ReadAsync(buffer, offset, end - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Truncated HTTP/2 frame.");
                offset += count;
            }
        }

        internal bool IsWriteFailed => _writer.IsFailed;

        internal Task WriteAsync(Http2Frame[] frames, int peerMaximum, CancellationToken token)
            => WriteCoreAsync(frames, peerMaximum, token, token);

        internal Task<bool> WriteRequestAsync(Http2Frame[] frames, int peerMaximum, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl flow)
            => WriteCoreAsync(frames, peerMaximum, requestToken, connectionToken, flow);

        internal Task WriteSettingsAsync(Http2Frame[] frames, int peerMaximum, Action apply, CancellationToken token)
            => WriteCoreAsync(frames, peerMaximum, token, token, apply: apply);

        private async Task<bool> WriteCoreAsync(Http2Frame[] frames, int peerMaximum, CancellationToken requestToken,
            CancellationToken connectionToken, Http2SendFlowControl? flow = null, Action? apply = null)
        {
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            ValidateMaximum(peerMaximum);

            foreach (var frame in frames)
            {
                if (frame == null) throw new ArgumentException("Missing frame.", nameof(frames));
                if (frame.PayloadLength > peerMaximum) throw new ArgumentException("Outbound frame exceeds peer maximum.", nameof(frames));
                frame.ValidateShape();

            }
            return await _writer.WriteAsync(frames, requestToken, connectionToken, flow, apply).ConfigureAwait(false);
        }

        // The connection joins all I/O before disposing its serialization gate.
        public void Dispose() => _writer.Dispose();

        private static void ValidateMaximum(int maximum)
        {
            if (maximum < 16384 || maximum > 16777215) throw new ArgumentOutOfRangeException(nameof(maximum));
        }
    }
}
