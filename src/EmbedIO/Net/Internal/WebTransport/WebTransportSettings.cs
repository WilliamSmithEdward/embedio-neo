using System;
using System.Collections.Generic;
using System.IO;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    // The HTTP/3 SETTINGS values WebTransport depends on (draft-ietf-webtrans-http3-16
    // sections 3.1, 5.1 and 5.5). Parsing reads the same SETTINGS payload the
    // connection already validates with Http3PeerSettings, so the connection's
    // duplicate and HTTP/2-only checks are not repeated here beyond the identifiers
    // this extension owns. Transport parameters (max_datagram_frame_size and
    // reset_stream_at) are not visible to this layer and are reported separately.
    internal sealed class WebTransportSettings
    {
        public bool ExtendedConnect { get; private set; }
        public bool Datagrams { get; private set; }
        public bool Enabled { get; private set; }
        public long InitialMaxData { get; private set; }
        public long InitialMaxStreamsUnidirectional { get; private set; }
        public long InitialMaxStreamsBidirectional { get; private set; }

        // Section 5.1: declaring any non-zero initial limit is an intent to use flow control.
        public bool DeclaresFlowControl => InitialMaxData != 0 || InitialMaxStreamsUnidirectional != 0 || InitialMaxStreamsBidirectional != 0;

        // Section 3.1: every SETTINGS requirement a server peer must have met before
        // :protocol=webtransport-h3 requests are processed as sessions.
        public bool SettingsRequirementsMet => ExtendedConnect && Datagrams && Enabled;

        internal static WebTransportSettings Local(bool enabled, long initialMaxData, long initialMaxStreamsUnidirectional, long initialMaxStreamsBidirectional)
        {
            if (initialMaxData < 0 || initialMaxData > QuicInteger.Maximum) throw new ArgumentOutOfRangeException(nameof(initialMaxData));
            if (initialMaxStreamsUnidirectional < 0 || initialMaxStreamsUnidirectional > WebTransportProtocol.MaximumStreamCount)
                throw new ArgumentOutOfRangeException(nameof(initialMaxStreamsUnidirectional));
            if (initialMaxStreamsBidirectional < 0 || initialMaxStreamsBidirectional > WebTransportProtocol.MaximumStreamCount)
                throw new ArgumentOutOfRangeException(nameof(initialMaxStreamsBidirectional));
            return new WebTransportSettings
            {
                ExtendedConnect = true,
                Datagrams = true,
                Enabled = enabled,
                InitialMaxData = initialMaxData,
                InitialMaxStreamsUnidirectional = initialMaxStreamsUnidirectional,
                InitialMaxStreamsBidirectional = initialMaxStreamsBidirectional,
            };
        }

        internal static WebTransportSettings Parse(byte[] payload, int maximumEntries = 1024)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (maximumEntries < 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
            var settings = new WebTransportSettings();
            var seen = new HashSet<long>();
            var offset = 0;
            try
            {
                while (offset < payload.Length)
                {
                    if (seen.Count == maximumEntries) throw new Http3ProtocolException(WebTransportProtocol.H3ExcessiveLoad, "Too many HTTP/3 settings.");
                    var id = QuicInteger.Read(payload, ref offset, payload.Length);
                    var value = QuicInteger.Read(payload, ref offset, payload.Length);
                    if (!seen.Add(id)) throw new Http3ProtocolException(WebTransportProtocol.H3SettingsError, "Duplicate HTTP/3 setting identifier.");
                    switch (id)
                    {
                        case WebTransportProtocol.SettingEnableConnectProtocol: settings.ExtendedConnect = Boolean(value); break;
                        case WebTransportProtocol.SettingH3Datagram: settings.Datagrams = Boolean(value); break;
                        case WebTransportProtocol.SettingEnabled: settings.Enabled = Boolean(value); break;
                        case WebTransportProtocol.SettingInitialMaxData: settings.InitialMaxData = value; break;
                        case WebTransportProtocol.SettingInitialMaxStreamsUnidirectional: settings.InitialMaxStreamsUnidirectional = StreamCount(value); break;
                        case WebTransportProtocol.SettingInitialMaxStreamsBidirectional: settings.InitialMaxStreamsBidirectional = StreamCount(value); break;
                    }
                }
            }
            catch (EndOfStreamException) { throw new Http3ProtocolException(WebTransportProtocol.H3FrameError, "Truncated HTTP/3 setting."); }
            return settings;
        }

        // Section 5.1: flow control applies only when both endpoints declared it.
        internal static bool FlowControlEnabled(WebTransportSettings local, WebTransportSettings peer)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            return local.DeclaresFlowControl && peer.DeclaresFlowControl;
        }

        // The extension's own identifier/value pairs for the connection's SETTINGS
        // frame. The connection already sends SETTINGS_ENABLE_CONNECT_PROTOCOL and
        // owns SETTINGS_H3_DATAGRAM; zero-valued limits are omitted as defaults.
        internal byte[] EncodeLocal()
        {
            var bytes = new byte[64];
            var count = 0;
            if (Enabled) count += Pair(bytes, count, WebTransportProtocol.SettingEnabled, 1);
            if (InitialMaxData != 0) count += Pair(bytes, count, WebTransportProtocol.SettingInitialMaxData, InitialMaxData);
            if (InitialMaxStreamsUnidirectional != 0) count += Pair(bytes, count, WebTransportProtocol.SettingInitialMaxStreamsUnidirectional, InitialMaxStreamsUnidirectional);
            if (InitialMaxStreamsBidirectional != 0) count += Pair(bytes, count, WebTransportProtocol.SettingInitialMaxStreamsBidirectional, InitialMaxStreamsBidirectional);
            var result = new byte[count];
            Array.Copy(bytes, result, count);
            return result;
        }

        private static int Pair(byte[] bytes, int offset, long id, long value)
        {
            var count = QuicInteger.Write(bytes, offset, id);
            return count + QuicInteger.Write(bytes, offset + count, value);
        }

        private static bool Boolean(long value)
        {
            if (value > 1) throw new Http3ProtocolException(WebTransportProtocol.H3SettingsError, "Invalid HTTP/3 boolean setting.");
            return value != 0;
        }

        // A stream count the peer could never use is treated like any other invalid
        // setting value; section 5.6.2 states the 2^60 bound for capsules only.
        private static long StreamCount(long value)
        {
            if (value > WebTransportProtocol.MaximumStreamCount) throw new Http3ProtocolException(WebTransportProtocol.H3SettingsError, "WebTransport initial stream limit exceeds 2^60.");
            return value;
        }
    }
}
