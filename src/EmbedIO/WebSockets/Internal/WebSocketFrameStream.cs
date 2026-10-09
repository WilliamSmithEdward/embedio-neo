using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace EmbedIO.WebSockets.Internal
{
    internal class WebSocketFrameStream
    {
        // Matches WebSocketModule's receive buffer. Holds every frame header and
        // small payloads, so a burst of small frames needs one transport read.
        internal const int InputBufferLength = 2048;

        // Payloads up to this length are allocated when the header arrives.
        // Longer ones grow only as bytes arrive, so a header announcing a huge
        // length cannot reserve memory the peer never sends.
        internal const int ExactAllocationLimit = 65536;

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private readonly Stream? _stream;
        private byte[]? _input;
        private int _start;
        private int _end;
        private long _messageLength;
        private long _skip;
        private Utf8MessageValidator _textValidator;
        private bool _textMessage;

        // Frames from a client are always masked and are always unmasked here; the
        // flag is retained for existing callers.
        public WebSocketFrameStream(Stream? stream, bool unmask = false)
        {
            _stream = stream;
            _ = unmask;
        }

        // Zero disables the check. Applies to the total payload of a data message.
        internal int MaxMessageSize { get; set; }

        // Set when the last read threw for a frame whose boundaries are still known:
        // an oversized message (its payload is skipped by the next read) or invalid
        // text. The connection can then complete a close handshake.
        internal WebSocketFrame? Rejected { get; private set; }

        internal async Task<WebSocketFrame?> ReadFrameAsync(WebSocket webSocket)
        {
            if (_stream == null) return null;
            Rejected = null;
            if (_skip > 0) await SkipAsync().ConfigureAwait(false);

            if (!await FillAsync(2).ConfigureAwait(false))
                throw new WebSocketException("The header of a frame cannot be read from the stream.");
            var input = _input!;
            var frame = ProcessHeader(input[_start], input[_start + 1]);
            _start += 2;
            // Reject invalid mask/flags/fragment state before reading attacker-
            // supplied lengths or waiting for/allocating a payload.
            frame.Validate(webSocket);

            await ReadExtendedPayloadLengthAsync(frame).ConfigureAwait(false);
            CheckMessageSize(frame);
            if (!await FillAsync(4).ConfigureAwait(false))
                throw new WebSocketException("The masking key of a frame cannot be read from the stream.");
            var key = new byte[4];
            Buffer.BlockCopy(input, _start, key, 0, 4);
            _start += 4;
            await ReadPayloadDataAsync(frame, key).ConfigureAwait(false);
            frame.Mask = Mask.Off;

            if (frame.Opcode == Opcode.Close) ValidateClosePayload(frame.PayloadData.ToArray());
            ValidateTextPayload(frame);

            return frame;
        }

        private void CheckMessageSize(WebSocketFrame frame)
        {
            if (frame.Opcode != Opcode.Text && frame.Opcode != Opcode.Binary && frame.Opcode != Opcode.Cont)
                return;
            // Validate has already rejected continuation/new-message mismatches.
            var total = (frame.Opcode == Opcode.Cont ? _messageLength : 0) + (long)frame.FullPayloadLength;
            if (MaxMessageSize > 0 && total > MaxMessageSize)
                throw Reject(frame, 4 + (long)frame.FullPayloadLength, CloseStatusCode.TooBig, $"Message too big. Maximum is {MaxMessageSize} bytes.");
            // A delivered message is one byte[]; refuse before buffering one that cannot be.
            if (total > int.MaxValue)
                throw Reject(frame, 4 + (long)frame.FullPayloadLength, CloseStatusCode.TooBig, "Message exceeds the supported representation.");
            _messageLength = frame.Fin == Fin.Final ? 0 : total;
        }

        // The rest of the message is discarded, so neither size nor text state carries on.
        private WebSocketException Reject(WebSocketFrame frame, long skip, CloseStatusCode code, string message)
        {
            Rejected = frame;
            _skip = skip;
            _messageLength = 0;
            _textMessage = false;
            return new WebSocketException(code, message);
        }

        private async Task SkipAsync()
        {
            var input = _input!;
            var buffered = Math.Min(_end - _start, _skip);
            _start += (int)buffered;
            _skip -= buffered;
            if (_skip == 0) return;
            _start = _end = 0;
            while (_skip > 0)
            {
                var read = await _stream!.ReadAsync(input, 0, (int)Math.Min(input.Length, _skip)).ConfigureAwait(false);
                if (read == 0) throw Truncated();
                _skip -= read;
            }
        }

        private void ValidateTextPayload(WebSocketFrame frame)
        {
            // Control and binary bytes never advance the text validator.
            if (frame.Opcode == Opcode.Text) { _textMessage = true; _textValidator = default; }
            else if (frame.Opcode == Opcode.Binary) { _textMessage = false; return; }
            else if (frame.Opcode != Opcode.Cont || !_textMessage) return;
            var final = frame.Fin == Fin.Final;
            if (!_textValidator.Validate(frame.PayloadData.ToArray(), final))
                throw Reject(frame, 0, CloseStatusCode.InvalidData, "Text message is not valid UTF-8.");
            if (final) _textMessage = false;
        }

        private static bool IsOpcodeControl(byte opcode) => opcode > 0x7 && opcode < 0x10;

        private static WebSocketFrame ProcessHeader(byte first, byte second)
        {
            var fin = (first & 0x80) == 0x80 ? Fin.Final : Fin.More;
            var rsv1 = (first & 0x40) == 0x40 ? Rsv.On : Rsv.Off;
            var rsv2 = (first & 0x20) == 0x20 ? Rsv.On : Rsv.Off;
            var rsv3 = (first & 0x10) == 0x10 ? Rsv.On : Rsv.Off;
            var opcode = (byte)(first & 0x0f);
            var mask = (second & 0x80) == 0x80 ? Mask.On : Mask.Off;
            var payloadLen = (byte)(second & 0x7f);

            var err = !IsDefinedOpcode(opcode) ? "An unsupported opcode."
            : opcode != 0x1 && opcode != 0x2 && rsv1 == Rsv.On ? "A non data frame is compressed."
            : IsOpcodeControl(opcode) && fin == Fin.More ? "A control frame is fragmented."
            : IsOpcodeControl(opcode) && payloadLen > 125 ? "A control frame has a long payload length."
            : opcode == 8 && payloadLen == 1 ? "A close frame cannot contain a one-byte status."
            : null;

            if (err != null)
                throw new WebSocketException(CloseStatusCode.ProtocolError, err);

            return new WebSocketFrame(fin, rsv1, rsv2, rsv3, (Opcode)opcode, mask, payloadLen);
        }

        // Equivalent to Enum.IsDefined(typeof(Opcode), opcode) without reflection or boxing.
        private static bool IsDefinedOpcode(byte opcode)
            => opcode <= 0x2 || (opcode >= 0x8 && opcode <= 0xa);

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

        // Ensures at least count unconsumed bytes are buffered. Reads only until the
        // request is satisfied, so validation can stop before further bytes are read.
        private async Task<bool> FillAsync(int count)
        {
            var input = _input ??= new byte[InputBufferLength];
            if (_end - _start >= count) return true;
            if (_start > 0)
            {
                Buffer.BlockCopy(input, _start, input, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            while (_end < count)
            {
                var read = await _stream!.ReadAsync(input, _end, input.Length - _end).ConfigureAwait(false);
                if (read == 0) return false;
                _end += read;
            }
            return true;
        }

        private async Task ReadExtendedPayloadLengthAsync(WebSocketFrame frame)
        {
            var len = frame.ExtendedPayloadLengthCount;

            if (len == 0)
            {
                frame.ExtendedPayloadLength = Array.Empty<byte>();
                return;
            }

            if (!await FillAsync(len).ConfigureAwait(false))
            {
                throw new WebSocketException(
                    "The extended payload length of a frame cannot be read from the stream.");
            }

            var bytes = new byte[len];
            Buffer.BlockCopy(_input!, _start, bytes, 0, len);
            _start += len;
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

        private async Task ReadPayloadDataAsync(WebSocketFrame frame, byte[] key)
        {
            var length = (int)frame.FullPayloadLength;
            if (length == 0)
            {
                frame.PayloadData = new PayloadData();
                return;
            }

            byte[] payload;
            if (length <= InputBufferLength)
            {
                // Small payloads are read ahead with the following headers.
                if (!await FillAsync(length).ConfigureAwait(false)) throw Truncated();
                payload = new byte[length];
                Buffer.BlockCopy(_input!, _start, payload, 0, length);
                _start += length;
            }
            else
            {
                payload = new byte[Math.Min(length, ExactAllocationLimit)];
                var filled = Math.Min(_end - _start, length);
                Buffer.BlockCopy(_input!, _start, payload, 0, filled);
                _start += filled;
                while (filled < length)
                {
                    if (filled == payload.Length)
                    {
                        // Grow only after the previous capacity is full and more bytes
                        // are due, never beyond the frame's announced length.
                        var grown = new byte[(int)Math.Min(length, (long)payload.Length * 2)];
                        Buffer.BlockCopy(payload, 0, grown, 0, filled);
                        payload = grown;
                    }
                    var read = await _stream!.ReadAsync(payload, filled, payload.Length - filled).ConfigureAwait(false);
                    if (read == 0) throw Truncated();
                    filled += read;
                }
            }

            PayloadData.Mask(payload, key);
            frame.PayloadData = new PayloadData(payload);
        }

        private static WebSocketException Truncated()
            => new("The payload data of a frame cannot be read from the stream.");
    }
}
