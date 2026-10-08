using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO.WebSockets.Internal
{
    internal class WebSocketFrameStream
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly bool _unmask;
        private readonly Stream? _stream;
        private Utf8MessageValidator _textValidator;
        private bool _textMessage;

        public WebSocketFrameStream(Stream? stream, bool unmask = false)
        {
            _stream = stream;
            _unmask = unmask;
        }

        internal async Task<WebSocketFrame?> ReadFrameAsync(WebSocket webSocket)
        {
            if (_stream == null) return null;

            var frame = ProcessHeader(await _stream.ReadBytesAsync(2).ConfigureAwait(false));
            // Reject invalid mask/flags/fragment state before reading attacker-
            // supplied lengths or waiting for/allocating a payload.
            frame.Validate(webSocket);

            await ReadExtendedPayloadLengthAsync(frame).ConfigureAwait(false);
            await ReadMaskingKeyAsync(frame).ConfigureAwait(false);
            await ReadPayloadDataAsync(frame).ConfigureAwait(false);

            if (_unmask)
                frame.Unmask();

            frame.Unmask();
            if (frame.Opcode == Opcode.Close) ValidateClosePayload(frame.PayloadData.ToArray());
            ValidateTextPayload(frame);

            return frame;
        }

        private void ValidateTextPayload(WebSocketFrame frame)
        {
            // Control and binary bytes never advance the text validator.
            if (frame.Opcode == Opcode.Text) { _textMessage = true; _textValidator = default; }
            else if (frame.Opcode == Opcode.Binary) { _textMessage = false; return; }
            else if (frame.Opcode != Opcode.Cont || !_textMessage) return;
            var final = frame.Fin == Fin.Final;
            if (!_textValidator.Validate(frame.PayloadData.ToArray(), final))
                throw new WebSocketException(CloseStatusCode.InvalidData, "Text message is not valid UTF-8.");
            if (final) _textMessage = false;
        }

        private static bool IsOpcodeData(byte opcode) => opcode == 0x1 || opcode == 0x2;

        private static bool IsOpcodeControl(byte opcode) => opcode > 0x7 && opcode < 0x10;

        private static WebSocketFrame ProcessHeader(byte[] header)
        {
            if (header.Length != 2)
                throw new WebSocketException("The header of a frame cannot be read from the stream.");

            // FIN
            var fin = (header[0] & 0x80) == 0x80 ? Fin.Final : Fin.More;

            // RSV1
            var rsv1 = (header[0] & 0x40) == 0x40 ? Rsv.On : Rsv.Off;

            // RSV2
            var rsv2 = (header[0] & 0x20) == 0x20 ? Rsv.On : Rsv.Off;

            // RSV3
            var rsv3 = (header[0] & 0x10) == 0x10 ? Rsv.On : Rsv.Off;

            // Opcode
            var opcode = (byte)(header[0] & 0x0f);

            // MASK
            var mask = (header[1] & 0x80) == 0x80 ? Mask.On : Mask.Off;

            // Payload Length
            var payloadLen = (byte)(header[1] & 0x7f);

            var err = !Enum.IsDefined(typeof(Opcode), opcode) ? "An unsupported opcode."
            : !IsOpcodeData(opcode) && rsv1 == Rsv.On ? "A non data frame is compressed."
            : IsOpcodeControl(opcode) && fin == Fin.More ? "A control frame is fragmented."
            : IsOpcodeControl(opcode) && payloadLen > 125 ? "A control frame has a long payload length."
            : opcode == 8 && payloadLen == 1 ? "A close frame cannot contain a one-byte status."
            : null;

            if (err != null)
                throw new WebSocketException(CloseStatusCode.ProtocolError, err);

            return new WebSocketFrame(fin, rsv1, rsv2, rsv3, (Opcode)opcode, mask, payloadLen);
        }

        private static void ValidateClosePayload(byte[] payload)
        {
            if (payload.Length == 0) return;
            // RFC 6455 and the IANA registry as of 2026-10-08. No negotiated
            // extension defines another status in the reserved 1000-2999 range.
            var code = (payload[0] << 8) | payload[1];
            if (code < 1000 || code >= 5000 || code == 1004 || code == 1005 || code == 1006
                || (code >= 1015 && code < 3000))
                throw new WebSocketException(CloseStatusCode.ProtocolError, "Invalid close status on the wire.");
            try { _ = StrictUtf8.GetCharCount(payload, 2, payload.Length - 2); }
            catch (DecoderFallbackException error)
            {
                throw new WebSocketException(CloseStatusCode.InvalidData, "Close reason is not valid UTF-8.", error);
            }
        }

        private async Task ReadExtendedPayloadLengthAsync(WebSocketFrame frame)
        {
            var len = frame.ExtendedPayloadLengthCount;

            if (len == 0)
            {
                frame.ExtendedPayloadLength = Array.Empty<byte>();
                return;
            }

            var bytes = await (_stream ?? throw new InvalidOperationException("The frame reader has no stream.")).ReadBytesAsync(len).ConfigureAwait(false);

            if (bytes.Length != len)
            {
                throw new WebSocketException(
                    "The extended payload length of a frame cannot be read from the stream.");
            }

            frame.ExtendedPayloadLength = bytes;
            var length = frame.FullPayloadLength;
            if ((len == 8 && (bytes[0] & 0x80) != 0)
                || (len == 2 && length < 126) || (len == 8 && length < 65536))
                throw new WebSocketException(CloseStatusCode.ProtocolError, "Invalid or nonminimal frame payload length.");
            // Payloads are represented by byte[] and the stream reader accepts
            // int lengths. Never let an unchecked cast wrap the wire length.
            if (length > int.MaxValue)
                throw new WebSocketException(CloseStatusCode.TooBig, "Frame payload exceeds the supported representation.");
        }

        private async Task ReadMaskingKeyAsync(WebSocketFrame frame)
        {
            var len = frame.IsMasked ? 4 : 0;

            if (len == 0)
            {
                frame.MaskingKey = Array.Empty<byte>();
                return;
            }

            var bytes = await (_stream ?? throw new InvalidOperationException("The frame reader has no stream.")).ReadBytesAsync(len).ConfigureAwait(false);
            if (bytes.Length != len)
            {
                throw new WebSocketException(
                      "The masking key of a frame cannot be read from the stream.");
            }

            frame.MaskingKey = bytes;
        }

        private async Task ReadPayloadDataAsync(WebSocketFrame frame)
        {
            var len = frame.FullPayloadLength;
            if (len == 0)
            {
                frame.PayloadData = new PayloadData();

                return;
            }

            if (len > PayloadData.MaxLength)
                throw new WebSocketException(CloseStatusCode.TooBig, "A frame has a long payload length.");

            var bytes = frame.PayloadLength < 127
                ? await (_stream ?? throw new InvalidOperationException("The frame reader has no stream.")).ReadBytesAsync((int)len).ConfigureAwait(false)
                : await (_stream ?? throw new InvalidOperationException("The frame reader has no stream.")).ReadBytesAsync((int)len, 1024).ConfigureAwait(false);

            if (bytes.Length != (int)len)
            {
                throw new WebSocketException(
                      "The payload data of a frame cannot be read from the stream.");
            }

            frame.PayloadData = new PayloadData(bytes);
        }
    }
}
