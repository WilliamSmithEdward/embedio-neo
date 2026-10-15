using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    internal readonly struct WebTransportCapsule
    {
        internal WebTransportCapsule(long type, byte[] payload) { Type = type; Payload = payload; }
        public long Type { get; }
        public byte[] Payload { get; }
    }

    // Payload codecs for the capsules of draft-ietf-webtrans-http3-16 sections
    // 4.7, 5.6 and 6. Encoding produces a complete RFC 9297 capsule; decoding
    // takes the already-bounded payload. Malformed payloads are session errors
    // that reset the CONNECT stream with H3_MESSAGE_ERROR, matching the draft's
    // treatment of an oversized or invalid close message.
    internal static class WebTransportCapsuleCodec
    {
        // The largest payload any session capsule can carry: a close code plus message.
        internal const int MaximumPayloadLength = 4 + WebTransportProtocol.MaximumCloseMessageBytes;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        internal static bool IsSessionCapsule(long type)
            => type is WebTransportProtocol.CloseSessionCapsule or WebTransportProtocol.DrainSessionCapsule
                or WebTransportProtocol.MaxDataCapsule or WebTransportProtocol.DataBlockedCapsule
                or WebTransportProtocol.MaxStreamsBidirectionalCapsule or WebTransportProtocol.MaxStreamsUnidirectionalCapsule
                or WebTransportProtocol.StreamsBlockedBidirectionalCapsule or WebTransportProtocol.StreamsBlockedUnidirectionalCapsule
                or WebTransportProtocol.ProhibitedMaxStreamDataCapsule or WebTransportProtocol.ProhibitedStreamDataBlockedCapsule;

        // Section 6: senders truncate at a UTF-8 character boundary.
        internal static string TruncateCloseMessage(string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var bytes = Encoding.UTF8.GetBytes(message);
            if (bytes.Length <= WebTransportProtocol.MaximumCloseMessageBytes) return message;
            var end = WebTransportProtocol.MaximumCloseMessageBytes;
            while (end > 0 && (bytes[end] & 0xC0) == 0x80) end--;
            return Encoding.UTF8.GetString(bytes, 0, end);
        }

        internal static byte[] EncodeClose(uint applicationErrorCode, string message)
        {
            var text = Encoding.UTF8.GetBytes(TruncateCloseMessage(message));
            var header = new byte[16];
            var count = QuicInteger.Write(header, 0, WebTransportProtocol.CloseSessionCapsule);
            count += QuicInteger.Write(header, count, 4 + text.Length);
            var capsule = new byte[count + 4 + text.Length];
            Array.Copy(header, capsule, count);
            capsule[count] = (byte)(applicationErrorCode >> 24);
            capsule[count + 1] = (byte)(applicationErrorCode >> 16);
            capsule[count + 2] = (byte)(applicationErrorCode >> 8);
            capsule[count + 3] = (byte)applicationErrorCode;
            Array.Copy(text, 0, capsule, count + 4, text.Length);
            return capsule;
        }

        internal static uint DecodeClose(byte[] payload, out string message)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length < 4) throw Malformed("WT_CLOSE_SESSION is shorter than its error code.");
            if (payload.Length - 4 > WebTransportProtocol.MaximumCloseMessageBytes) throw Malformed("WT_CLOSE_SESSION message exceeds 1024 bytes.");
            try { message = StrictUtf8.GetString(payload, 4, payload.Length - 4); }
            catch (DecoderFallbackException) { throw Malformed("WT_CLOSE_SESSION message is not valid UTF-8."); }
            return ((uint)payload[0] << 24) | ((uint)payload[1] << 16) | ((uint)payload[2] << 8) | payload[3];
        }

        internal static byte[] EncodeDrain()
        {
            var bytes = new byte[16];
            var count = QuicInteger.Write(bytes, 0, WebTransportProtocol.DrainSessionCapsule);
            bytes[count++] = 0;
            var capsule = new byte[count];
            Array.Copy(bytes, capsule, count);
            return capsule;
        }

        internal static byte[] EncodeLimit(long type, long value)
        {
            if (!IsLimitCapsule(type)) throw new ArgumentOutOfRangeException(nameof(type));
            var maximum = IsStreamLimitCapsule(type) ? WebTransportProtocol.MaximumStreamCount : QuicInteger.Maximum;
            if (value < 0 || value > maximum) throw new ArgumentOutOfRangeException(nameof(value));
            var bytes = new byte[24];
            var count = QuicInteger.Write(bytes, 0, type);
            var valueLength = value < 64 ? 1 : value < 16384 ? 2 : value < 1073741824 ? 4 : 8;
            count += QuicInteger.Write(bytes, count, valueLength);
            count += QuicInteger.Write(bytes, count, value);
            var capsule = new byte[count];
            Array.Copy(bytes, capsule, count);
            return capsule;
        }

        // Sections 5.6.2 to 5.6.5: one variable-length integer, nothing more.
        internal static long DecodeLimit(long type, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (!IsLimitCapsule(type)) throw new ArgumentOutOfRangeException(nameof(type));
            var offset = 0;
            long value;
            try { value = QuicInteger.Read(payload, ref offset, payload.Length); }
            catch (EndOfStreamException) { throw Malformed("Truncated WebTransport flow control capsule."); }
            if (offset != payload.Length) throw Malformed("WebTransport flow control capsule carries trailing bytes.");
            if (IsStreamLimitCapsule(type) && value > WebTransportProtocol.MaximumStreamCount)
                throw new WebTransportException(WebTransportProtocol.FlowControlError, "WebTransport stream limit exceeds 2^60.");
            return value;
        }

        internal static void ValidateDrain(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length != 0) throw Malformed("WT_DRAIN_SESSION must have a zero length.");
        }

        internal static bool IsLimitCapsule(long type)
            => type is WebTransportProtocol.MaxDataCapsule or WebTransportProtocol.DataBlockedCapsule
                or WebTransportProtocol.MaxStreamsBidirectionalCapsule or WebTransportProtocol.MaxStreamsUnidirectionalCapsule
                or WebTransportProtocol.StreamsBlockedBidirectionalCapsule or WebTransportProtocol.StreamsBlockedUnidirectionalCapsule;

        internal static bool IsStreamLimitCapsule(long type)
            => type is WebTransportProtocol.MaxStreamsBidirectionalCapsule or WebTransportProtocol.MaxStreamsUnidirectionalCapsule
                or WebTransportProtocol.StreamsBlockedBidirectionalCapsule or WebTransportProtocol.StreamsBlockedUnidirectionalCapsule;

        private static WebTransportException Malformed(string message)
            => new(WebTransportProtocol.H3MessageError, message);
    }

    // Reads session capsules from a CONNECT stream through the existing RFC 9297
    // codec. Session capsule payloads are tiny, so they are buffered up to the
    // codec maximum; unknown capsule types are skipped without allocating their
    // declared length (RFC 9297 section 3.2). Truncation and oversized session
    // payloads are reported as session errors; the caller resets the CONNECT
    // stream and owns the stream, cancellation and resource policy.
    internal sealed class WebTransportCapsuleReader
    {
        private readonly HttpCapsuleTransport _transport;
        private readonly int _maximumPayload;
        private int _reading;

        internal WebTransportCapsuleReader(Stream stream) : this(stream, WebTransportCapsuleCodec.MaximumPayloadLength) { }
        internal WebTransportCapsuleReader(Stream stream, int maximumPayload)
        {
            if (maximumPayload < 0) throw new ArgumentOutOfRangeException(nameof(maximumPayload));
            _transport = new HttpCapsuleTransport(stream);
            _maximumPayload = maximumPayload;
        }

        // Null means the peer finished the CONNECT stream cleanly.
        internal async Task<WebTransportCapsule?> ReadAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent WebTransport capsule reads.");
            try
            {
                while (true)
                {
                    var header = await _transport.ReadHeaderAsync(token).ConfigureAwait(false);
                    if (!header.HasValue) return null;
                    var type = header.Value.Type;
                    var length = header.Value.Length;
                    if (!WebTransportCapsuleCodec.IsSessionCapsule(type))
                    {
                        await _transport.SkipPayloadAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    if (length > _maximumPayload) throw new WebTransportException(WebTransportProtocol.H3MessageError, "WebTransport session capsule exceeds its payload limit.");
                    var payload = length == 0 ? Array.Empty<byte>() : new byte[(int)length];
                    var offset = 0;
                    while (offset < payload.Length)
                        offset += await _transport.ReadPayloadAsync(payload, offset, payload.Length - offset, token).ConfigureAwait(false);
                    return new WebTransportCapsule(type, payload);
                }
            }
            catch (EndOfStreamException error)
            { throw new WebTransportException(WebTransportProtocol.H3MessageError, error.Message); }
            finally { Volatile.Write(ref _reading, 0); }
        }
    }
}
