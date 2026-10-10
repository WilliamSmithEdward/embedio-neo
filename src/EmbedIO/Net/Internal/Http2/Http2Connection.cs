using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Server-side connection startup and controls. The caller owns the transport
    // lifetime and handshake deadline. Stream dispatch is added above this layer.
    internal sealed class Http2Connection : IDisposable
    {
        private static readonly byte[] Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        private readonly Http2FrameTransport _transport;
        private CancellationToken _transportCancellation;
        // Both are used only by the transport's output flusher, in wire order.
        private readonly HpackEncoder _encoder = new();
        private readonly MemoryStream _encoded = new();
        private readonly Http2HeaderBlocks _headers = new();
        private int _pendingSettings = 1;
        public Http2PeerSettings Peer { get; } = new();
        internal Http2SendFlowControl SendFlow { get; } = new();
        internal Http2ReceiveFlowControl ReceiveFlow { get; } = new();
        internal Http2StreamRegistry Streams { get; } = new() { ExtendedConnectEnabled = true };
        public bool PeerSentGoAway { get; private set; }
        public int PeerLastStreamId { get; private set; }
        public uint PeerErrorCode { get; private set; }
        internal Action<Exception>? OutputFailed { get; set; }
        internal void UseTransportCancellation(CancellationToken token) => _transport.ConnectionToken = _transportCancellation = token;
        internal Action<int> AdjustStreamWindows { get; set; } = _ => { };

        private Http2Connection(Stream stream) : this(stream, ArrayPool<byte>.Shared) { }
        private Http2Connection(Stream stream, ArrayPool<byte> dataPool)
        { _transport = new Http2FrameTransport(stream, 16384, dataPool ?? throw new ArgumentNullException(nameof(dataPool))); }

        internal static async Task<Http2Connection> AcceptAsync(Stream stream, CancellationToken token)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            var received = new byte[Preface.Length];
            var offset = 0;
            while (offset < received.Length)
            {
                var count = await stream.ReadAsync(received, offset, received.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("Incomplete HTTP/2 connection preface.");
                for (var i = 0; i < count; i++)
                    if (received[offset + i] != Preface[offset + i]) throw new Http2ProtocolException(1, "Invalid HTTP/2 connection preface.");
                offset += count;
            }
            var connection = new Http2Connection(stream);
            connection.UseTransportCancellation(token);
            try
            {
                // Bound incoming streams/headers, advertise RFC 8441 tunnels and RFC 9218 priorities.
                var settings = new byte[] { 0, 3, 0, 0, 0, 128, 0, 6, 0, 0, 128, 0, 0, 8, 0, 0, 0, 1, 0, 9, 0, 0, 0, 1 };
                await connection.SendAsync(new[] { new Http2Frame(4, 0, 0, settings) }, token).ConfigureAwait(false);
                using var first = await connection.ReadFrameAsync(token).ConfigureAwait(false);
                if (first == null || first.Type != 4 || (first.Flags & 1) != 0)
                    throw new Http2ProtocolException(1, "Client preface must start with non-ACK SETTINGS.");
                await connection.ProcessControlAsync(first, token).ConfigureAwait(false);
                return connection;
            }
            catch { connection.Dispose(); throw; }
        }

        internal async Task<Http2Frame?> ReadFrameAsync(CancellationToken token)
        {
            var frame = await _transport.ReadAsync(token).ConfigureAwait(false);
            try
            {
                if (frame == null) _headers.CompleteInput();
                else
                {
                    frame.HeaderBlock = _headers.Process(frame);
                    frame.ValidateShape();
                }
                return frame;
            }
            catch { frame?.Dispose(); throw; }
        }

        // The stream layer must enforce an outstanding CONTINUATION sequence
        // before allowing control frames through this method.
        internal async Task<bool> ProcessControlAsync(Http2Frame frame, CancellationToken token)
        {
            frame.ValidateShape();
            switch (frame.Type)
            {
                case 4:
                    if ((frame.Flags & 1) != 0)
                    {
                        if (_pendingSettings == 0) throw new Http2ProtocolException(1, "Unsolicited SETTINGS acknowledgment.");
                        _pendingSettings--;
                    }
                    else
                    {
                        // Applied when the ACK commits, so the encoder table size and
                        // DATA admission change exactly at the ACK's wire position.
                        try
                        {
                            await _transport.WriteSettingsAsync(new[] { new Http2Frame(4, 1, 0, Array.Empty<byte>()) },
                                Peer.MaximumFrameSize, () => Peer.Apply(frame.Payload, delta =>
                                {
                                    SendFlow.AdjustInitialWindow(delta);
                                    AdjustStreamWindows(delta);
                                }, size => _encoder.SetMaximumTableSize((int)Math.Min(size, 4096u))), token).ConfigureAwait(false);
                        }
                        catch (Exception error) when (_transport.IsWriteFailed)
                        {
                            OutputFailed?.Invoke(error);
                            throw;
                        }
                    }
                    return true;
                case 6:
                    if ((frame.Flags & 1) == 0)
                        await SendAsync(new[] { new Http2Frame(6, 1, 0, frame.Payload) }, token).ConfigureAwait(false);
                    return true;
                case 7:
                    PeerSentGoAway = true;
                    PeerLastStreamId = (int)(Http2PeerSettings.ReadUInt32(frame.Payload, 0) & 0x7fffffff);
                    PeerErrorCode = Http2PeerSettings.ReadUInt32(frame.Payload, 4);
                    return true;
                default: return false;
            }
        }

        // A reset before commit leaves the HPACK table untouched. Once encoded,
        // the block is committed whole even if its stream resets, or the next
        // block could refer to table entries the peer never received.
        internal async Task SendHeadersAsync(int streamId, HpackField[] fields, bool endStream, CancellationToken token)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            try
            {
                await _transport.QueueWriteAsync(new HeaderWrite(this, streamId, fields, endStream, token, _transportCancellation)).ConfigureAwait(false);
            }
            catch (Exception error) when (_transport.IsWriteFailed)
            {
                OutputFailed?.Invoke(error);
                throw;
            }
        }

        private sealed class HeaderWrite : Http2OutputWrite
        {
            private readonly Http2Connection _connection;
            private readonly int _streamId;
            private readonly HpackField[] _fields;
            private readonly bool _endStream;
            private readonly int _estimate;

            internal HeaderWrite(Http2Connection connection, int streamId, HpackField[] fields, bool endStream,
                CancellationToken token, CancellationToken writeToken) : base(token, writeToken)
            {
                _connection = connection; _streamId = streamId; _fields = fields; _endStream = endStream;
                var estimate = 9;
                foreach (var field in fields) estimate += field.Name.Length + field.Value.Length + 2;
                _estimate = estimate;
            }

            internal override int EstimatedBytes => _estimate;

            internal override bool Commit(Http2OutputBuffer output)
            {
                long size = 0;
                foreach (var field in _fields) size += field.Size;
                if (size > _connection.Peer.MaximumHeaderListSize) throw new IOException("Response headers exceed peer limit.");
                var encoded = _connection._encoded;
                encoded.SetLength(0);
                _connection._encoder.EncodeTo(_fields, encoded);
                var bytes = encoded.GetBuffer();
                var length = (int)encoded.Length;
                // Use the universally supported size even while peer settings change.
                var count = Math.Max(1, (length + 16383) / 16384);
                for (var i = 0; i < count; i++)
                {
                    var fragment = Math.Min(16384, length - i * 16384);
                    var flags = (byte)((i == count - 1 ? 4 : 0) | (i == 0 && _endStream ? 1 : 0));
                    output.WriteFrameHeader(fragment, i == 0 ? (byte)1 : (byte)9, flags, _streamId);
                    output.Write(bytes, i * 16384, fragment);
                }
                return true;
            }
        }

        // The owner cancels and joins connection I/O before disposing this state.
        // The underlying stream remains caller-owned.
        public void Dispose()
        {
            SendFlow.Abort(new ObjectDisposedException(nameof(Http2Connection)));
            _headers.Dispose();
            ReceiveFlow.Abort();
            Streams.Abort();
            _transport.Dispose();
            _encoded.Dispose();
        }

        // DATA borrowing the caller's buffer until this completes. Its credit
        // must already be reserved; it is returned unless the frame commits.
        internal async Task<bool> SendDataAsync(int streamId, bool endStream, byte[] bytes, int offset, int count, CancellationToken token)
        {
            try
            {
                var committed = await _transport.QueueWriteAsync(new DataWrite(SendFlow, streamId, endStream, bytes, offset, count,
                    token, _transportCancellation)).ConfigureAwait(false);
                if (!committed) SendFlow.ReturnUnusedReservation(streamId, count);
                return committed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !_transport.IsWriteFailed)
            {
                // No DATA reached the wire. A reset may already have removed its
                // stream window; connection credit still belongs to siblings.
                try { SendFlow.ReturnUnusedReservation(streamId, count); }
                catch (Exception error) { OutputFailed?.Invoke(error); throw; }
                throw;
            }
            catch (Exception error) { OutputFailed?.Invoke(error); throw; }
        }

        private sealed class DataWrite : Http2OutputWrite
        {
            private readonly Http2SendFlowControl _flow;
            private readonly int _streamId;
            private readonly bool _endStream;
            private readonly byte[] _bytes;
            private readonly int _offset;
            private readonly int _count;

            internal DataWrite(Http2SendFlowControl flow, int streamId, bool endStream, byte[] bytes, int offset, int count,
                CancellationToken token, CancellationToken writeToken) : base(token, writeToken)
            {
                _flow = flow; _streamId = streamId; _endStream = endStream;
                _bytes = bytes; _offset = offset; _count = count;
            }

            internal override int EstimatedBytes => _count + 9;

            internal override bool Commit(Http2OutputBuffer output)
            {
                if (_count != 0 && !_flow.CanSendReserved(_streamId)) return false;
                output.WriteFrameHeader(_count, 0, _endStream ? (byte)1 : (byte)0, _streamId);
                output.Write(_bytes, _offset, _count);
                return true;
            }
        }

        internal async Task<bool> SendStreamAsync(Http2Frame[] frames, CancellationToken token)
        {
            try
            {
                var committed = await _transport.WriteRequestAsync(frames, Peer.MaximumFrameSize, token,
                    _transportCancellation, SendFlow).ConfigureAwait(false);
                if (!committed) ReturnReservations(frames);
                return committed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested && !_transport.IsWriteFailed)
            {
                // No DATA reached the wire. A reset may already have removed its
                // stream window; connection credit still belongs to siblings.
                try
                {
                    ReturnReservations(frames);
                }
                catch (Exception error) { OutputFailed?.Invoke(error); throw; }
                throw;
            }
            catch (Exception error) { OutputFailed?.Invoke(error); throw; }
        }

        private void ReturnReservations(Http2Frame[] frames)
        {
            foreach (var frame in frames)
                if (frame.Type == 0) SendFlow.ReturnUnusedReservation(frame.StreamId, frame.PayloadLength);
        }

        internal async Task SendAsync(Http2Frame[] frames, CancellationToken token)
        {
            try { await _transport.WriteAsync(frames, Peer.MaximumFrameSize, token).ConfigureAwait(false); }
            catch (Exception error) { OutputFailed?.Invoke(error); throw; }
        }
    }
}
