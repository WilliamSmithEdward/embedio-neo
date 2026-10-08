using System;

namespace EmbedIO.Net.Internal.Http2
{
    // Settings received by a server. Connection synchronization is owned by the
    // caller; callbacks apply stream-window and encoder changes in wire order.
    internal sealed class Http2PeerSettings
    {
        private bool _receivedInitial;
        public bool NoRfc7540Priorities { get; private set; }
        public uint HeaderTableSize { get; private set; } = 4096;
        public bool EnablePush { get; private set; } = true;
        public uint MaximumConcurrentStreams { get; private set; } = uint.MaxValue;
        public int InitialWindowSize { get; private set; } = 65535;
        public int MaximumFrameSize { get; private set; } = 16384;
        public uint MaximumHeaderListSize { get; private set; } = uint.MaxValue;
        public bool EnableConnectProtocol { get; private set; }

        internal void Apply(byte[] payload, Action<int> adjustWindows, Action<uint> adjustHeaderTable)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (adjustWindows == null) throw new ArgumentNullException(nameof(adjustWindows));
            if (adjustHeaderTable == null) throw new ArgumentNullException(nameof(adjustHeaderTable));
            if (payload.Length % 6 != 0) throw new Http2ProtocolException(6, "Invalid SETTINGS length.");
            // Validate every value before changing externally observable state.
            var connectEnabled = EnableConnectProtocol;
            for (var offset = 0; offset < payload.Length; offset += 6)
            {
                var id = (payload[offset] << 8) | payload[offset + 1];
                var value = ReadUInt32(payload, offset + 2);
                if (id == 2 && value > 1) throw new Http2ProtocolException(1, "Invalid ENABLE_PUSH.");
                if (id == 4 && value > int.MaxValue) throw new Http2ProtocolException(3, "Invalid initial flow-control window.");
                if (id == 5 && (value < 16384 || value > 16777215)) throw new Http2ProtocolException(1, "Invalid maximum frame size.");
                if (id == 9 && (value > 1 || (_receivedInitial && (value == 1) != NoRfc7540Priorities)))
                    throw new Http2ProtocolException(1, "Invalid or changed NO_RFC7540_PRIORITIES.");
                if (id == 8)
                {
                    if (value > 1 || (connectEnabled && value == 0)) throw new Http2ProtocolException(1, "Invalid CONNECT protocol setting.");
                    connectEnabled = value != 0;
                }
            }
            for (var offset = 0; offset < payload.Length; offset += 6)
            {
                var id = (payload[offset] << 8) | payload[offset + 1];
                var value = ReadUInt32(payload, offset + 2);
                switch (id)
                {
                    case 1: adjustHeaderTable(value); HeaderTableSize = value; break;
                    case 2: EnablePush = value != 0; break;
                    case 3: MaximumConcurrentStreams = value; break;
                    case 4:
                        adjustWindows((int)value - InitialWindowSize);
                        InitialWindowSize = (int)value;
                        break;
                    case 5: MaximumFrameSize = (int)value; break;
                    case 6: MaximumHeaderListSize = value; break;
                    case 8: EnableConnectProtocol = value != 0; break;
                    case 9: NoRfc7540Priorities = value != 0; break;
                        // Unknown settings are explicitly ignored by HTTP/2.
                }
            }
            _receivedInitial = true;
        }

        internal static uint ReadUInt32(byte[] bytes, int offset) => ((uint)bytes[offset] << 24)
            | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
    }
}
