using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2ProtocolException : IOException
    {
        internal Http2ProtocolException(uint errorCode, string message, int streamId = 0) : base(message)
        { ErrorCode = errorCode; StreamId = streamId; }
        public uint ErrorCode { get; }
        public int StreamId { get; }
    }

    internal sealed class Http2Frame : IDisposable
    {
        private byte[]? _payload;
        private readonly ArrayPool<byte>? _payloadPool;
        internal Http2Frame(byte type, byte flags, int streamId, byte[] payload)
        {
            if (streamId < 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            Type = type; Flags = flags; StreamId = streamId;
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            PayloadLength = payload.Length;
        }
        private Http2Frame(byte flags, int streamId, byte[] payload, int offset, int count)
            : this(0, flags, streamId, payload)
        {
            if (offset < 0 || count < 0 || offset > payload.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            PayloadOffset = offset;
            PayloadLength = count;
        }
        private Http2Frame(byte flags, int streamId, byte[] payload, int count, ArrayPool<byte> pool)
            : this(flags, streamId, payload, 0, count)
        { _payloadPool = pool ?? throw new ArgumentNullException(nameof(pool)); }
        // Outgoing DATA borrows this slice only until its awaited write completes.
        // Received DATA leases are disposed after dispatch copies their content.
        // Header/control frames retain their independent complete payloads.
        internal static Http2Frame BorrowData(byte flags, int streamId, byte[] payload, int offset, int count)
            => new(flags, streamId, payload, offset, count);
        internal static Http2Frame OwnData(byte flags, int streamId, byte[] payload, int count, ArrayPool<byte> pool)
            => new(flags, streamId, payload, count, pool);
        public void Dispose()
        {
            if (_payloadPool == null) return;
            var payload = Interlocked.Exchange(ref _payload, null);
            if (payload != null) _payloadPool.Return(payload, true);
        }
        public byte Type { get; }
        public byte Flags { get; }
        public int StreamId { get; }
        public byte[] Payload => _payload ?? throw new ObjectDisposedException(nameof(Http2Frame));
        internal int PayloadOffset { get; }
        internal int PayloadLength { get; }
        public Http2HeaderBlock? HeaderBlock { get; internal set; }

        // Validate after reading the complete frame so a stream error does not
        // leave unread payload bytes in front of the next frame. Stream lifetime,
        // continuation ordering and SETTINGS values belong to connection state.
        internal void ValidateShape()
        {
            var length = PayloadLength;
            switch (Type)
            {
                case 0: // DATA
                case 1: // HEADERS
                case 9: // CONTINUATION
                    RequireStream();
                    break;
                case 2: // PRIORITY
                    RequireStream();
                    if (length != 5) throw new Http2ProtocolException(6, "Invalid PRIORITY length.", StreamId);
                    break;
                case 3: // RST_STREAM
                    RequireStream();
                    RequireLength(4);
                    break;
                case 4: // SETTINGS
                    RequireConnection();
                    if (length % 6 != 0 || ((Flags & 1) != 0 && length != 0))
                        throw new Http2ProtocolException(6, "Invalid SETTINGS length.");
                    break;
                case 5: // PUSH_PROMISE
                    RequireStream();
                    break;
                case 6: // PING
                    RequireConnection();
                    RequireLength(8);
                    break;
                case 7: // GOAWAY
                    RequireConnection();
                    if (length < 8) throw new Http2ProtocolException(6, "Invalid GOAWAY length.");
                    break;
                case 16: // RFC 9218 PRIORITY_UPDATE
                    RequireConnection();
                    if (length < 4) throw new Http2ProtocolException(6, "Truncated priority-update identifier.");
                    break;
                case 8: // WINDOW_UPDATE
                    RequireLength(4);
                    break;
            }
            if (Type == 0 || Type == 1 || Type == 5)
            {
                var prefix = (Flags & 8) != 0 ? 1 : 0;
                var required = prefix + (Type == 1 && (Flags & 32) != 0 ? 5 : Type == 5 ? 4 : 0);
                if (length < required) throw new Http2ProtocolException(6, "Missing frame prefix.");
                if (prefix != 0 && Payload[PayloadOffset] > length - required)
                    throw new Http2ProtocolException(1, "Invalid frame padding.");
            }
        }

        private void RequireStream()
        { if (StreamId == 0) throw new Http2ProtocolException(1, "Frame requires a stream."); }
        private void RequireConnection()
        { if (StreamId != 0) throw new Http2ProtocolException(1, "Frame requires stream zero."); }
        private void RequireLength(int length)
        { if (PayloadLength != length) throw new Http2ProtocolException(6, "Invalid frame length."); }
    }
}
