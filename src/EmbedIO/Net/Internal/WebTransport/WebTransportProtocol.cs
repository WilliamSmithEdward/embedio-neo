using System;
using System.IO;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    // Identifiers and wire helpers for WebTransport over HTTP/3 as specified by
    // draft-ietf-webtrans-http3-16 (2026-07-06, WG Last Call, no RFC). The
    // codepoints below are the draft's IANA requests; the live HTTP/3 registries
    // held no WebTransport entries on 2026-10-10. Everything here is pure
    // framing: it never touches a QUIC stream and advertises no capability.
    internal static class WebTransportProtocol
    {
        // Section 3.2 and 9.1: the extended CONNECT :protocol value and upgrade token.
        internal const string ProtocolToken = "webtransport-h3";

        // Section 9.2 settings identifiers (all default to 0).
        internal const long SettingEnabled = 0x2c7cf000;
        internal const long SettingInitialMaxData = 0x2b61;
        internal const long SettingInitialMaxStreamsUnidirectional = 0x2b64;
        internal const long SettingInitialMaxStreamsBidirectional = 0x2b65;

        // Section 3.1: settings from RFC 9220 and RFC 9297 that must also be sent.
        internal const long SettingEnableConnectProtocol = 0x08;
        internal const long SettingH3Datagram = 0x33;

        // Section 4.2 unidirectional stream type and section 4.3 bidirectional signal.
        internal const long UnidirectionalStreamType = 0x54;
        internal const long BidirectionalStreamSignal = 0x41;

        // Section 9.6 capsule types.
        internal const long CloseSessionCapsule = 0x2843;
        internal const long DrainSessionCapsule = 0x78ae;
        internal const long MaxDataCapsule = 0x190B4D3D;
        internal const long MaxStreamsBidirectionalCapsule = 0x190B4D3F;
        internal const long MaxStreamsUnidirectionalCapsule = 0x190B4D40;
        internal const long DataBlockedCapsule = 0x190B4D41;
        internal const long StreamsBlockedBidirectionalCapsule = 0x190B4D43;
        internal const long StreamsBlockedUnidirectionalCapsule = 0x190B4D44;

        // Section 5.4: HTTP/2-mapping capsules whose receipt is a session error.
        internal const long ProhibitedMaxStreamDataCapsule = 0x190B4D3E;
        internal const long ProhibitedStreamDataBlockedCapsule = 0x190B4D42;

        // Section 9.5 error codes.
        internal const long BufferedStreamRejected = 0x3994bd84;
        internal const long SessionGone = 0x170d7b68;
        internal const long FlowControlError = 0x045d4487;
        internal const long AlpnError = 0x0817b3dd;
        internal const long RequirementsNotMet = 0x212c0d48;
        internal const long FirstApplicationErrorCode = 0x52e4a40fa8db;
        internal const long LastApplicationErrorCode = 0x52e5ac983162;

        // RFC 9114 section 8.1 codes this mapping relies on.
        internal const long H3FrameError = 0x106;
        internal const long H3ExcessiveLoad = 0x107;
        internal const long H3IdError = 0x108;
        internal const long H3SettingsError = 0x109;
        internal const long H3RequestRejected = 0x10b;
        internal const long H3MessageError = 0x10e;
        internal const long H3DatagramError = 0x33;

        // Section 6: the close message limit, in UTF-8 bytes.
        internal const int MaximumCloseMessageBytes = 1024;

        // Section 5.6.2: stream counts cannot exceed 2^60.
        internal const long MaximumStreamCount = 1L << 60;

        // A session identifier is the CONNECT stream identifier: client-initiated
        // and bidirectional (section 4; RFC 9000 section 2.1).
        internal static bool IsSessionId(long id) => id >= 0 && id <= QuicInteger.Maximum && (id & 3) == 0;

        // Section 4.4, figure 4. Codepoints of the form 0x1f * N + 0x21 are
        // reserved by RFC 9114 and skipped.
        internal static long ToHttp3ErrorCode(uint applicationErrorCode)
        {
            long n = applicationErrorCode;
            return FirstApplicationErrorCode + n + (n / 0x1e);
        }

        internal static bool TryToApplicationErrorCode(long http3ErrorCode, out uint applicationErrorCode)
        {
            applicationErrorCode = 0;
            if (http3ErrorCode < FirstApplicationErrorCode || http3ErrorCode > LastApplicationErrorCode) return false;
            if ((http3ErrorCode - 0x21) % 0x1f == 0) return false;
            var shifted = http3ErrorCode - FirstApplicationErrorCode;
            applicationErrorCode = (uint)(shifted - (shifted / 0x1f));
            return true;
        }

        // Reads the session identifier that follows a 0x54 stream type or a 0x41
        // signal. The caller has already consumed the type or signal and offers
        // whatever bytes it holds; a short buffer asks for more bytes rather than
        // failing, because the header may be split across QUIC frames.
        internal static WebTransportHeaderStatus TryReadSessionId(byte[] bytes, int offset, int count, out long sessionId, out int consumed)
        {
            ValidateBuffer(bytes, offset, count);
            sessionId = 0;
            consumed = 0;
            if (count == 0) return WebTransportHeaderStatus.NeedMoreData;
            var width = 1 << (bytes[offset] >> 6);
            if (count < width) return WebTransportHeaderStatus.NeedMoreData;
            var cursor = offset;
            sessionId = QuicInteger.Read(bytes, ref cursor, offset + width);
            consumed = width;
            // A session identifier never belongs to a server or unidirectional
            // stream (section 4). The connection must close with H3_ID_ERROR.
            if (!IsSessionId(sessionId)) throw new Http3ProtocolException(H3IdError, "WebTransport session identifier is not a client-initiated bidirectional stream.");
            return WebTransportHeaderStatus.Complete;
        }

        // Reads the first bytes of a client bidirectional stream. Any signal other
        // than 0x41 means the stream is an ordinary HTTP/3 request stream and the
        // caller must hand the unchanged bytes to its frame parser.
        internal static WebTransportHeaderStatus TryReadBidirectionalHeader(byte[] bytes, int offset, int count, out long sessionId, out int consumed)
        {
            ValidateBuffer(bytes, offset, count);
            sessionId = 0;
            consumed = 0;
            if (count == 0) return WebTransportHeaderStatus.NeedMoreData;
            var width = 1 << (bytes[offset] >> 6);
            if (count < width) return WebTransportHeaderStatus.NeedMoreData;
            var cursor = offset;
            var signal = QuicInteger.Read(bytes, ref cursor, offset + width);
            if (signal != BidirectionalStreamSignal) return WebTransportHeaderStatus.NotWebTransport;
            var status = TryReadSessionId(bytes, cursor, count - width, out sessionId, out var idBytes);
            if (status == WebTransportHeaderStatus.Complete) consumed = width + idBytes;
            return status;
        }

        // Section 4.2: stream type then session identifier, for server-opened streams.
        internal static int WriteUnidirectionalHeader(byte[] bytes, int offset, long sessionId)
        {
            if (!IsSessionId(sessionId)) throw new ArgumentOutOfRangeException(nameof(sessionId));
            var count = QuicInteger.Write(bytes, offset, UnidirectionalStreamType);
            return count + QuicInteger.Write(bytes, offset + count, sessionId);
        }

        // Section 4.3: signal then session identifier, for server-opened streams.
        internal static int WriteBidirectionalHeader(byte[] bytes, int offset, long sessionId)
        {
            if (!IsSessionId(sessionId)) throw new ArgumentOutOfRangeException(nameof(sessionId));
            var count = QuicInteger.Write(bytes, offset, BidirectionalStreamSignal);
            return count + QuicInteger.Write(bytes, offset + count, sessionId);
        }

        // Section 4.5 with RFC 9297 section 2.1: the quarter stream identifier at
        // the start of a QUIC DATAGRAM payload names the CONNECT stream. The
        // payload is borrowed from the caller's packet and never copied.
        internal static long ReadDatagramSessionId(byte[] packet, int offset, int count, out int payloadOffset)
        {
            ValidateBuffer(packet, offset, count);
            var cursor = offset;
            long quarter;
            try { quarter = QuicInteger.Read(packet, ref cursor, offset + count); }
            catch (EndOfStreamException) { throw new Http3ProtocolException(H3DatagramError, "Truncated HTTP datagram quarter stream identifier."); }
            if (quarter >= 1L << 60) throw new Http3ProtocolException(H3DatagramError, "HTTP datagram quarter stream identifier exceeds the QUIC stream space.");
            payloadOffset = cursor;
            return quarter << 2;
        }

        internal static int WriteDatagramHeader(byte[] packet, int offset, long sessionId)
        {
            if (!IsSessionId(sessionId)) throw new ArgumentOutOfRangeException(nameof(sessionId));
            return QuicInteger.Write(packet, offset, sessionId >> 2);
        }

        internal static void ValidateBuffer(byte[] bytes, int offset, int count)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        }
    }

    internal enum WebTransportHeaderStatus
    {
        Complete,
        NeedMoreData,
        NotWebTransport,
    }
}
