using System;
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
        private readonly HpackEncoder _encoder = new();
        private readonly Http2HeaderBlocks _headers = new();
        private int _pendingSettings = 1;
        private readonly SemaphoreSlim _headerOutput = new(1, 1);
        public Http2PeerSettings Peer { get; } = new();
        internal Http2SendFlowControl SendFlow { get; } = new();
        internal Http2ReceiveFlowControl ReceiveFlow { get; } = new();
        internal Http2StreamRegistry Streams { get; } = new() { ExtendedConnectEnabled = true };
        public bool PeerSentGoAway { get; private set; }
        public int PeerLastStreamId { get; private set; }
        public uint PeerErrorCode { get; private set; }
        internal Action<Exception>? OutputFailed { get; set; }
        internal Action<int> AdjustStreamWindows { get; set; } = _ => { };

        private Http2Connection(Stream stream) { _transport = new Http2FrameTransport(stream); }

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
            try
            {
                // Bound incoming streams/headers, advertise RFC 8441 tunnels and RFC 9218 priorities.
                var settings = new byte[] { 0, 3, 0, 0, 0, 128, 0, 6, 0, 0, 128, 0, 0, 8, 0, 0, 0, 1, 0, 9, 0, 0, 0, 1 };
                await connection.SendAsync(new[] { new Http2Frame(4, 0, 0, settings) }, token).ConfigureAwait(false);
                var first = await connection.ReadFrameAsync(token).ConfigureAwait(false);
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
            if (frame == null) _headers.CompleteInput();
            else
            {
                frame.HeaderBlock = _headers.Process(frame);
                frame.ValidateShape();
            }
            return frame;
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
                        await _headerOutput.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            Peer.Apply(frame.Payload, delta =>
                            {
                                SendFlow.AdjustInitialWindow(delta);
                                AdjustStreamWindows(delta);
                            },
                                size => _encoder.SetMaximumTableSize((int)Math.Min(size, 4096u)));
                            await SendAsync(new[] { new Http2Frame(4, 1, 0, Array.Empty<byte>()) }, token).ConfigureAwait(false);
                        }
                        finally { _headerOutput.Release(); }
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

        internal async Task SendHeadersAsync(int streamId, HpackField[] fields, bool endStream, CancellationToken token)
        {
            await _headerOutput.WaitAsync(token).ConfigureAwait(false);
            try
            {
                long size = 0;
                foreach (var field in fields) size += field.Size;
                if (size > Peer.MaximumHeaderListSize) throw new IOException("Response headers exceed peer limit.");
                var encoded = _encoder.Encode(fields);
                // Use the universally supported size even while peer settings change.
                var frames = new Http2Frame[Math.Max(1, (encoded.Length + 16383) / 16384)];
                for (var i = 0; i < frames.Length; i++)
                {
                    var count = Math.Min(16384, encoded.Length - i * 16384);
                    var fragment = new byte[count];
                    Buffer.BlockCopy(encoded, i * 16384, fragment, 0, count);
                    var flags = (byte)((i == frames.Length - 1 ? 4 : 0) | (i == 0 && endStream ? 1 : 0));
                    frames[i] = new Http2Frame(i == 0 ? (byte)1 : (byte)9, flags, streamId, fragment);
                }
                try { await SendAsync(frames, token).ConfigureAwait(false); }
                catch (Exception error) { OutputFailed?.Invoke(error); throw; }
            }
            finally { _headerOutput.Release(); }
        }

        // The owner cancels and joins connection I/O before disposing this state.
        // The underlying stream remains caller-owned.
        public void Dispose()
        {
            SendFlow.Abort(new ObjectDisposedException(nameof(Http2Connection)));
            _headers.Dispose();
            ReceiveFlow.Abort();
            Streams.Abort();
            _headerOutput.Dispose();
            _transport.Dispose();
        }

        internal async Task SendAsync(Http2Frame[] frames, CancellationToken token)
        {
            try { await _transport.WriteAsync(frames, Peer.MaximumFrameSize, token).ConfigureAwait(false); }
            catch (Exception error) { OutputFailed?.Invoke(error); throw; }
        }
    }
}
