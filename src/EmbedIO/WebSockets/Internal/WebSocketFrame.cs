using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using EmbedIO.Net.Internal;
using EmbedIO.Internal;

namespace EmbedIO.WebSockets.Internal
{
    internal class WebSocketFrame
    {
        internal static readonly byte[] EmptyPingBytes = CreatePingFrame().ToArray();

        internal WebSocketFrame(Opcode opcode, PayloadData payloadData)
            : this(Fin.Final, opcode, payloadData)
        {
        }

        internal WebSocketFrame(Fin fin, Opcode opcode, byte[] data, bool compressed)
            : this(fin, opcode, new PayloadData(data), compressed)
        {
        }

        internal WebSocketFrame(
            Fin fin,
            Opcode opcode,
            PayloadData payloadData,
            bool compressed = false)
        {
            Fin = fin;
            Rsv1 = IsOpcodeData(opcode) && compressed ? Rsv.On : Rsv.Off;
            Rsv2 = Rsv.Off;
            Rsv3 = Rsv.Off;
            Opcode = opcode;

            var len = payloadData.Length;
            if (len < 126)
            {
                PayloadLength = (byte)len;
                ExtendedPayloadLength = Array.Empty<byte>();
            }
            else if (len < 0x010000)
            {
                PayloadLength = 126;
                ExtendedPayloadLength = ((ushort)len).ToByteArray(Endianness.Big);
            }
            else
            {
                PayloadLength = 127;
                ExtendedPayloadLength = len.ToByteArray(Endianness.Big);
            }

            Mask = Mask.Off;
            MaskingKey = Array.Empty<byte>();
            PayloadData = payloadData;
        }

        internal WebSocketFrame(
            Fin fin,
            Rsv rsv1,
            Rsv rsv2,
            Rsv rsv3,
            Opcode opcode,
            Mask mask,
            byte payloadLength)
        {
            Fin = fin;
            Rsv1 = rsv1;
            Rsv2 = rsv2;
            Rsv3 = rsv3;
            Opcode = opcode;
            Mask = mask;
            PayloadLength = payloadLength;
        }

        public byte[]? ExtendedPayloadLength { get; internal set; }

        public Fin Fin { get; internal set; }

        public bool IsCompressed => Rsv1 == Rsv.On;

        public bool IsFragment => Fin == Fin.More || Opcode == Opcode.Cont;

        public bool IsMasked => Mask == Mask.On;

        public Mask Mask { get; internal set; }

        public byte[] MaskingKey { get; internal set; } = Array.Empty<byte>();

        public Opcode Opcode { get; internal set; }

        public PayloadData PayloadData { get; internal set; } = new PayloadData();

        public byte PayloadLength { get; internal set; }

        public Rsv Rsv1 { get; internal set; }

        public Rsv Rsv2 { get; internal set; }

        public Rsv Rsv3 { get; internal set; }

        internal int ExtendedPayloadLengthCount => PayloadLength < 126 ? 0 : (PayloadLength == 126 ? 2 : 8);

        internal ulong FullPayloadLength
        {
            get
            {
                if (PayloadLength < 126)
                    return PayloadLength;

                return PayloadLength == 126
                    ? BinaryPrimitives.ReadUInt16BigEndian(ExtendedPayloadLength)
                    : BinaryPrimitives.ReadUInt64BigEndian(ExtendedPayloadLength);
            }
        }

        public IEnumerator<byte> GetEnumerator() => ((IEnumerable<byte>)ToArray()).GetEnumerator();

        public string PrintToString()
        {
            // Payload Length
            var payloadLen = PayloadLength;

            // Extended Payload Length
            var extPayloadLen = payloadLen > 125 ? FullPayloadLength.ToString(CultureInfo.InvariantCulture) : string.Empty;

            // Masking Key
            var maskingKey = BitConverter.ToString(MaskingKey);

            // Payload Data
            var payload = payloadLen == 0
                ? string.Empty
                : payloadLen > 125
                    ? "---"
                    : Opcode == Opcode.Text && !(IsFragment || IsMasked || IsCompressed)
                        ? PayloadData.ApplicationData.ToArray().ToText()
                        : PayloadData.ToString();

            return $@"
                    FIN: {Fin}
                   RSV1: {Rsv1}
                   RSV2: {Rsv2}
                   RSV3: {Rsv3}
                 Opcode: {Opcode}
                   MASK: {Mask}
         Payload Length: {payloadLen}
Extended Payload Length: {extPayloadLen}
            Masking Key: {maskingKey}
           Payload Data: {payload}";
        }

        public byte[] ToArray()
        {
            var header = (int)Fin;

            header = (header << 1) + (int)Rsv1;
            header = (header << 1) + (int)Rsv2;
            header = (header << 1) + (int)Rsv3;
            header = (header << 4) + (int)Opcode;
            header = (header << 1) + (int)Mask;
            header = (header << 7) + PayloadLength;
            var bytes = PayloadLength > 0 ? PayloadData.ToArray() : Array.Empty<byte>();
            var extendedLength = ExtendedPayloadLengthCount;
            var maskLength = Mask == Mask.On ? 4 : 0;
            var result = new byte[2 + extendedLength + maskLength + bytes.Length];
            result[0] = (byte)(header >> 8);
            result[1] = (byte)header;
            if (extendedLength > 0)
                Buffer.BlockCopy(ExtendedPayloadLength ?? throw new InvalidOperationException("The frame has no extended payload length."), 0, result, 2, extendedLength);
            if (maskLength > 0)
                Buffer.BlockCopy(MaskingKey, 0, result, 2 + extendedLength, maskLength);
            Buffer.BlockCopy(bytes, 0, result, 2 + extendedLength + maskLength, bytes.Length);
            return result;
        }

        public override string ToString() => BitConverter.ToString(ToArray());

        internal static WebSocketFrame CreateCloseFrame(PayloadData? payloadData) => new(Fin.Final, Opcode.Close, payloadData ?? new PayloadData());

        internal static WebSocketFrame CreatePingFrame() => new(Fin.Final, Opcode.Ping, new PayloadData());

        internal static WebSocketFrame CreatePingFrame(byte[] data) => new(Fin.Final, Opcode.Ping, new PayloadData(data));

        internal void Validate(WebSocket webSocket)
        {
            if (!IsMasked)
            {
                throw new WebSocketException(CloseStatusCode.ProtocolError, "A frame from a client isn't masked.");
            }

            if (!webSocket.InContinuation && Opcode == Opcode.Cont)
                throw new WebSocketException(CloseStatusCode.ProtocolError, "A continuation frame has no preceding fragmented message.");

            if (webSocket.InContinuation && (Opcode == Opcode.Text || Opcode == Opcode.Binary))
            {
                throw new WebSocketException(CloseStatusCode.ProtocolError,
                    "A data frame has been received while receiving continuation frames.");
            }

            if (IsCompressed)
            {
                throw new WebSocketException(CloseStatusCode.ProtocolError,
                    "A compressed frame has been received without any agreement for it.");
            }

            if (Rsv2 == Rsv.On)
            {
                throw new WebSocketException(CloseStatusCode.ProtocolError,
                    "The RSV2 of a frame is non-zero without any negotiation for it.");
            }

            if (Rsv3 == Rsv.On)
            {
                throw new WebSocketException(CloseStatusCode.ProtocolError,
                    "The RSV3 of a frame is non-zero without any negotiation for it.");
            }
        }

        internal void Unmask()
        {
            if (Mask == Mask.Off)
            {
                return;
            }

            Mask = Mask.Off;
            PayloadData.Mask(MaskingKey);
            MaskingKey = Array.Empty<byte>();
        }

        private static bool IsOpcodeData(Opcode opcode) => opcode == Opcode.Text || opcode == Opcode.Binary;
    }
}
