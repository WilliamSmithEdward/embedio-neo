using System;
using System.Buffers.Binary;
using System.Text;

namespace EmbedIO.Quic
{
    internal readonly record struct TlsClientHelloInfo(byte[] TransportParameters, string? ServerName);

    // Bounded TLS handshake framing used before native credential selection.
    internal static class TlsClientHello
    {
        internal static byte[] ReadTransportParameters(ReadOnlySpan<byte> message) => Read(message).TransportParameters;

        internal static TlsClientHelloInfo Read(ReadOnlySpan<byte> message)
        {
            if (message.Length < 42 || message.Length > 65536 || message[0] != 1)
                throw Failure(50, "Invalid ClientHello.");

            var size = (message[1] << 16) | (message[2] << 8) | message[3];

            if (size != message.Length - 4) throw Failure(50, "Invalid ClientHello boundary.");

            var body = message[4..];

            if (BinaryPrimitives.ReadUInt16BigEndian(body) != 0x303) throw Failure(70, "Invalid legacy TLS version.");

            var position = 34;
            var session = body[position++];

            if (session > 32 || session > body.Length - position) throw Failure(50, "Invalid TLS session ID.");

            position += session;
            var ciphers = TakeLength(body, ref position);

            if (ciphers.IsEmpty || (ciphers.Length & 1) != 0) throw Failure(50, "Invalid TLS cipher list.");

            if (position + 2 > body.Length || body[position++] != 1 || body[position++] != 0)
                throw Failure(47, "Invalid TLS compression list.");

            var extensions = TakeLength(body, ref position);

            if (position != body.Length) throw Failure(50, "Trailing ClientHello bytes.");

            byte[]? parameters = null;
            string? serverName = null;
            var seen = new byte[8192];

            while (!extensions.IsEmpty)
            {
                if (extensions.Length < 4) throw Failure(50, "Truncated TLS extension.");

                var type = BinaryPrimitives.ReadUInt16BigEndian(extensions);
                var bit = 1 << (type & 7);

                if ((seen[type >> 3] & bit) != 0) throw Failure(47, "Duplicate TLS extension.");

                seen[type >> 3] |= (byte)bit;
                var offset = 2;
                var value = TakeLength(extensions, ref offset);

                if (type == 0) serverName = ReadServerName(value);

                if (type == 57)
                {
                    if (value.Length > 4096) throw Failure(47, "Oversized client transport parameters.");

                    parameters = value.ToArray();
                }

                extensions = extensions[offset..];
            }

            return new TlsClientHelloInfo(
                parameters ?? throw Failure(109, "Missing client transport parameters."),
                serverName
            );
        }

        private static string ReadServerName(ReadOnlySpan<byte> extension)
        {
            var position = 0;
            var names = TakeLength(extension, ref position);

            if (position != extension.Length || names.IsEmpty) throw Failure(50, "Invalid server-name list.");

            string? result = null;

            while (!names.IsEmpty)
            {
                if (names[0] != 0 || result != null) throw Failure(47, "Unsupported or duplicate TLS server name.");

                var offset = 1;
                var name = TakeLength(names, ref offset);

                if (name.IsEmpty || name.Length > 255) throw Failure(47, "Invalid TLS server-name length.");

                var label = 0;

                foreach (var value in name)
                {
                    if (value == '.')
                    {
                        if (label == 0) throw Failure(47, "Empty TLS server-name label.");

                        label = 0;
                    }

                    else
                    {
                        if (
                            value is not (>= (byte)'a'
                                and <= (byte)'z'
                                or >= (byte)'A'
                                and <= (byte)'Z'
                                or >= (byte)'0'
                                and <= (byte)'9'
                                or (byte)'-'
                                or (byte)'_')
                        )
                            throw Failure(47, "Invalid TLS server-name encoding.");

                        if (++label > 63) throw Failure(47, "Oversized TLS server-name label.");
                    }
                }

                // Preserve the wire spelling for application selectors. Native TLS
                // still verifies the full ClientHello and negotiated extensions.
                result = Encoding.ASCII.GetString(name);
                names = names[offset..];
            }

            return result ?? throw Failure(50, "Missing TLS server name.");
        }

        private static ReadOnlySpan<byte> TakeLength(ReadOnlySpan<byte> bytes, ref int position)
        {
            if (bytes.Length - position < 2) throw Failure(50, "Truncated TLS vector length.");

            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[position..]);
            position += 2;

            if (size > bytes.Length - position) throw Failure(50, "Truncated TLS vector.");

            var result = bytes.Slice(position, size);
            position += size;
            return result;
        }

        private static TransportException Failure(byte alert, string message) => new(0x100UL + alert, message);
    }
}
