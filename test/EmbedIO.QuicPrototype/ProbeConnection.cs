using System.Buffers.Binary;
using System.Security.Cryptography;

namespace EmbedIO.QuicPrototype
{
    // Deliberately restricted to one loopback connection and stream 0. This milestone
    // establishes the TLS boundary, not general QUIC conformance or loss recovery.
    internal sealed class ProbeConnection : IDisposable
    {
        private const int StreamBudget = 8192;
        private readonly byte[] _originalId;
        private readonly byte[] _clientId;
        private readonly byte[] _serverId = RandomNumberGenerator.GetBytes(8);
        private readonly PacketKeys _initialRead;
        private readonly PacketKeys _initialWrite;
        private readonly OpenSslTls _tls;
        private readonly ulong[] _sentNumbers = new ulong[3];
        private readonly ulong[] _nextReceived = new ulong[3];
        private readonly ulong[] _cryptoOffsets = new ulong[3];
        private readonly HashSet<ulong>[] _received =
        [
            [],
            [],
            []
        ];
        private readonly Reassembly _stream = new(StreamBudget);
        private readonly Queue<byte[]> _pending = new();
        private bool _addressValidated;
        private bool _parametersValidated;
        private bool _handshakeDone;
        private bool _responseSent;
        private ulong _peerMaxData;
        private ulong _peerStreamData;

        internal bool Established => _tls.Established;
        internal bool Complete => _responseSent;
        internal bool Closed { get; private set; }
        internal int EchoBytes { get; private set; }
        internal long ReceivedBytes { get; private set; }
        internal long SentBytes { get; private set; }
        internal int RejectedPackets { get; private set; }

        internal ProbeConnection(ReadOnlySpan<byte> firstPacket, string library, string certificate, string key)
        {
            if (firstPacket.Length < 1200) throw new InvalidDataException("Initial datagram must be padded.");

            var reader = new WireReader(firstPacket);

            if ((reader.Byte() & 240) != 192 || BinaryPrimitives.ReadUInt32BigEndian(reader.Take(4)) != 1)
                throw new InvalidDataException("Expected QUIC v1 Initial.");

            var length = reader.Byte();

            if (length < 8 || length > 20) throw new InvalidDataException("Invalid original destination ID.");

            _originalId = reader
                .Take(length)
                .ToArray();
            length = reader.Byte();

            if (length > 20) throw new InvalidDataException("Invalid client source ID.");

            _clientId = reader
                .Take(length)
                .ToArray();
            reader.Take(reader.Length(4096));
            var payloadLength = reader.Length(firstPacket.Length);
            var numberOffset = firstPacket.Length - reader.Remaining;

            if (payloadLength > reader.Remaining) throw new InvalidDataException("Truncated Initial packet.");

            // Authenticate the Initial before allocating a TLS connection or selecting a peer.
            _initialRead = PacketKeys.Initial(_originalId, false);

            try
            {
                _initialRead.Unprotect(firstPacket[..(numberOffset + payloadLength)], numberOffset, 0, out _);
                _initialWrite = PacketKeys.Initial(_originalId, true);
                _tls = new OpenSslTls(library, certificate, key, LocalParameters());
            }

            catch
            {
                _initialRead.Dispose();
                _initialWrite?.Dispose();
                throw;
            }
        }

        internal List<byte[]> Receive(ReadOnlySpan<byte> datagram)
        {
            if (Closed) return [];

            ReceivedBytes += datagram.Length;
            var position = 0;

            while (position < datagram.Length)
            {
                var remaining = datagram[position..];

                if (!TryHeader(remaining, out var level, out var length, out var numberOffset))
                {
                    RejectedPackets++;
                    break;
                }

                var keys = level == 0 ? _initialRead : _tls.ReadKeys[level];

                if (keys == null)
                {
                    RejectedPackets++;
                    position += length;
                    continue;
                }

                byte[] plaintext;
                ulong number;

                try
                {
                    plaintext = keys.Unprotect(remaining[..length], numberOffset, _nextReceived[level], out number);
                }

                catch (CryptographicException)
                {
                    RejectedPackets++;
                    position += length;
                    continue;
                }

                if (_received[level].Count >= 4096) throw new InvalidDataException("Packet budget exceeded.");

                var fresh = _received[level].Add(number);
                _nextReceived[level] = Math.Max(_nextReceived[level], number + 1);

                if (level == 1) _addressValidated = true;

                if (fresh)
                {
                    var acknowledge = Frames(level, plaintext);

                    if (acknowledge) Send(level, Wire.Frame(2, number, 0, 0, 0));

                    try
                    {
                        _tls.Pump();
                    }

                    catch (InvalidDataException) when (_tls.AlertCode.HasValue)
                    {
                        // RFC 9001 section 4.8: carry TLS alerts as QUIC CRYPTO_ERROR.
                        Send(level, Wire.Frame(0x1c, 0x100UL + _tls.AlertCode.Value, 6, 0));
                        Closed = true;
                        break;
                    }

                    ValidateParameters();
                    FlushCrypto();

                    if (_tls.Established && !_handshakeDone)
                    {
                        if (!_parametersValidated) throw new InvalidDataException("Missing peer transport parameters.");

                        _handshakeDone = true;
                        Send(2, Wire.Frame(0x1e));
                    }

                    TryEcho();
                }

                position += length;
            }

            var output = new List<byte[]>();

            while (
                _pending.TryPeek(out var packet)
                && (_addressValidated || SentBytes + packet.Length <= ReceivedBytes * 3)
            )
            {
                _pending.Dequeue();
                SentBytes += packet.Length;
                output.Add(packet);
            }

            return output;
        }

        private bool TryHeader(ReadOnlySpan<byte> packet, out int level, out int length, out int numberOffset)
        {
            level = 0;
            length = 0;
            numberOffset = 0;

            try
            {
                var reader = new WireReader(packet);
                var first = reader.Byte();

                if ((first & 64) == 0) return false;

                if ((first & 128) != 0)
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(reader.Take(4)) != 1) return false;

                    var type = (first >> 4) & 3;

                    if (type is not (0 or 2)) return false;

                    level = type == 0 ? 0 : 1;
                    var destination = reader.Take(reader.Byte());
                    var source = reader.Take(reader.Byte());

                    if (
                        (!destination.SequenceEqual(_originalId) && !destination.SequenceEqual(_serverId))
                        || !source.SequenceEqual(_clientId)
                    ) return false;

                    if (level == 0) reader.Take(reader.Length(4096));

                    var payloadLength = reader.Length(packet.Length);
                    numberOffset = packet.Length - reader.Remaining;

                    if (payloadLength > reader.Remaining || payloadLength < 20) return false;

                    length = numberOffset + payloadLength;
                }

                else
                {
                    level = 2;

                    if (
                        !reader
                            .Take(_serverId.Length)
                            .SequenceEqual(_serverId)
                    ) return false;

                    numberOffset = packet.Length - reader.Remaining;
                    length = packet.Length;
                }

                return numberOffset + 20 <= length;
            }

            catch (InvalidDataException)
            {
                return false;
            }
        }

        private bool Frames(int level, ReadOnlySpan<byte> plaintext)
        {
            var reader = new WireReader(plaintext);
            var acknowledge = false;

            while (reader.Remaining != 0)
            {
                var type = reader.Integer();

                if (type == 0) continue;

                if (type is 2 or 3)
                {
                    var largest = reader.Integer();

                    if (largest >= _sentNumbers[level]) throw new InvalidDataException("ACK for unsent packet.");

                    reader.Integer();
                    var ranges = reader.Length(128);
                    var range = reader.Integer();

                    if (range > largest) throw new InvalidDataException("Invalid ACK range.");

                    var smallest = largest - range;

                    for (var i = 0; i < ranges; i++)
                    {
                        var gap = reader.Integer();
                        range = reader.Integer();

                        if (gap + 2 > smallest || range > smallest - gap - 2)
                            throw new InvalidDataException("Invalid ACK gap.");

                        smallest -= gap + 2 + range;
                    }

                    if (type == 3)
                    {
                        reader.Integer();
                        reader.Integer();
                        reader.Integer();
                    }

                    continue;
                }

                acknowledge = true;

                if (type == 1) continue;

                if (type == 6)
                {
                    var offset = reader.Integer();
                    var bytes = reader.Take(reader.Length(65536));
                    _tls.Add(level, offset, bytes);
                    continue;
                }

                if (type is 0x1c or 0x1d)
                {
                    reader.Integer();

                    if (type == 0x1c) reader.Integer();

                    reader.Take(reader.Length(4096));
                    Closed = true;
                    return false;
                }

                if (level != 2 || !_tls.Established)
                    throw new InvalidDataException("Application frame before TLS completion.");

                if (type is >= 8 and <= 15)
                {
                    if (reader.Integer() != 0)
                        throw new InvalidDataException("Only client bidirectional stream 0 is admitted by the probe.");

                    var offset = (type & 4) != 0 ? reader.Integer() : 0;
                    var length = (type & 2) != 0 ? reader.Length(StreamBudget) : reader.Remaining;
                    _stream.Add(offset, reader.Take(length), (type & 1) != 0);
                }

                else if (type == 0x10) _peerMaxData = Math.Max(_peerMaxData, reader.Integer());

                else if (type == 0x11)
                {
                    var id = reader.Integer();
                    var maximum = reader.Integer();

                    if (id == 0) _peerStreamData = Math.Max(_peerStreamData, maximum);
                }

                else if (type is 0x12 or 0x13) reader.Integer();

                else if (type == 0x18)
                {
                    var sequence = reader.Integer();

                    if (reader.Integer() > sequence) throw new InvalidDataException("Invalid retire-prior-to.");

                    var size = reader.Byte();

                    if (size is 0 or > 20) throw new InvalidDataException("Invalid connection ID.");

                    reader.Take(size + 16);
                    // The probe never changes the path or destination ID.
                }

                else if (type == 0x1a)
                {
                    using var response = new MemoryStream();
                    response.WriteByte(0x1b);
                    response.Write(reader.Take(8));
                    Send(2, response.ToArray());
                }

                else
                    throw new InvalidDataException(
                        "Frame is outside the feasibility profile: "
                            + type.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    );
            }

            return acknowledge;
        }

        private byte[] LocalParameters()
        {
            using var output = new MemoryStream();
            Wire.Parameter(output, 0, _originalId);
            Wire.Parameter(output, 1, 10000);
            Wire.Parameter(output, 3, 1472);
            Wire.Parameter(output, 4, StreamBudget);
            Wire.Parameter(output, 5, StreamBudget);
            Wire.Parameter(output, 6, StreamBudget);
            Wire.Parameter(output, 7, 0);
            Wire.Parameter(output, 8, 1);
            Wire.Parameter(output, 9, 0);
            Wire.Parameter(output, 12, []);
            Wire.Parameter(output, 14, 2);
            Wire.Parameter(output, 15, _serverId);
            return output.ToArray();
        }

        private void ValidateParameters()
        {
            if (_parametersValidated || _tls.PeerParameters == null) return;

            var reader = new WireReader(_tls.PeerParameters);
            var seen = new HashSet<ulong>();
            var sourceMatched = false;

            while (reader.Remaining != 0)
            {
                var id = reader.Integer();

                if (!seen.Add(id)) throw new InvalidDataException("Duplicate transport parameter.");

                var bytes = reader.Take(reader.Length(4096));

                if (id == 15) sourceMatched = bytes.SequenceEqual(_clientId);

                else if (id is 0 or 2 or 13 or 16) throw new InvalidDataException("Server-only transport parameter.");

                else if (id == 12 && bytes.Length != 0)
                    throw new InvalidDataException("Invalid disable-migration parameter.");

                else if (id is >= 1 and <= 11 or 14)
                {
                    var valueReader = new WireReader(bytes);
                    var value = valueReader.Integer();

                    if (valueReader.Remaining != 0)
                        throw new InvalidDataException("Trailing transport parameter bytes.");

                    if (
                        (id == 3 && value < 1200)
                        || (id == 10 && value > 20)
                        || (id == 11 && value >= 16384)
                        || (id == 14 && value < 2)
                    )
                        throw new InvalidDataException("Invalid transport parameter value.");

                    if (id == 4) _peerMaxData = value;

                    if (id == 5) _peerStreamData = value;
                }
            }

            if (!sourceMatched) throw new InvalidDataException("Peer source connection ID mismatch.");

            _parametersValidated = true;
        }

        private void FlushCrypto()
        {
            while (_tls.Output.TryDequeue(out var output))
            {
                for (var position = 0; position < output.Bytes.Length;)
                {
                    var count = Math.Min(900, output.Bytes.Length - position);
                    using var frame = new MemoryStream();
                    Wire.Integer(frame, 6);
                    Wire.Integer(frame, _cryptoOffsets[output.Level]);
                    Wire.Integer(frame, (ulong)count);
                    frame.Write(output.Bytes, position, count);
                    Send(output.Level, frame.ToArray());
                    _cryptoOffsets[output.Level] += (ulong)count;
                    position += count;
                }
            }
        }

        private void TryEcho()
        {
            if (_responseSent || !_tls.Established || !_stream.Complete) return;

            if ((ulong)_stream.Contiguous > Math.Min(_peerMaxData, _peerStreamData)) return;

            var data = _stream.Consume();

            for (var offset = 0; offset < data.Length || offset == 0;)
            {
                var count = Math.Min(900, data.Length - offset);
                using var frame = new MemoryStream();
                var last = offset + count == data.Length;
                Wire.Integer(frame, last ? 15UL : 14UL);
                Wire.Integer(frame, 0);
                Wire.Integer(frame, (ulong)offset);
                Wire.Integer(frame, (ulong)count);
                frame.Write(data, offset, count);
                Send(2, frame.ToArray());
                offset += count;

                if (last) break;
            }

            EchoBytes = data.Length;
            _responseSent = true;
        }

        private void Send(int level, byte[] payload)
        {
            if (_pending.Count >= 128 || _sentNumbers[level] >= 4096)
                throw new InvalidDataException("Outbound probe budget exceeded.");

            using var header = new MemoryStream();

            if (level < 2)
            {
                header.WriteByte(level == 0 ? (byte)0xc3 : (byte)0xe3);
                header.Write([0, 0, 0, 1]);
                header.WriteByte((byte)_clientId.Length);
                header.Write(_clientId);
                header.WriteByte((byte)_serverId.Length);
                header.Write(_serverId);

                if (level == 0)
                {
                    header.WriteByte(0);
                    // Initial length always uses two bytes at the required 1200-byte size.
                    Array.Resize(ref payload, Math.Max(payload.Length, 1200 - (int)header.Length - 2 - 4 - 16));
                }

                Wire.Integer(header, (ulong)(4 + payload.Length + 16));
            }

            else
            {
                header.WriteByte(0x43);
                header.Write(_clientId);
            }

            var numberOffset = (int)header.Length;
            var number = _sentNumbers[level]++;
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, checked((uint)number));
            header.Write(encoded);
            var keys = (level == 0 ? _initialWrite : _tls.WriteKeys[level])
                ?? throw new InvalidOperationException("Packet keys are unavailable.");
            _pending.Enqueue(keys.Protect(header.ToArray(), numberOffset, number, payload));
        }

        public void Dispose()
        {
            _tls.Dispose();
            _initialRead.Dispose();
            _initialWrite.Dispose();
        }
    }
}
