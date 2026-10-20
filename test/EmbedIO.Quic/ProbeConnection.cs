using System.Buffers.Binary;
using System.Security.Cryptography;
using EmbedIO.Net.Internal.Http3;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Quic
{
    // Connection state for raw stream echo or the bounded HTTP/3 WebTransport profile. This executable is not a
    // production QUIC provider.
    internal sealed class ProbeConnection : IDisposable
    {
        private const int StreamBudget = 8192;
        private readonly bool _webTransport;
        private readonly bool _streamed;
        private readonly string _authorization;
        private ConnectionStreams? _streams;
        private ulong _peerRemoteStreamData;
        private ulong _peerUniStreamData;
        private ulong _peerBidiCount;
        private ulong _peerUniCount;
        private ulong _peerDatagram;
        private bool _peerReset;
        private PacketKeys? _previousRead;
        private PacketKeys? _nextRead;
        private readonly KeyUpdateState _keyUpdates;
        private ulong _failedAuthentications;
        private readonly int _encryptionLimit;
        private readonly ulong _authenticationLimit;
        private bool _authenticationLimitReached;
        private double _previousExpires;

        internal int KeyUpdates { get; private set; }
        internal int LocalKeyUpdates { get; private set; }
        internal ulong SentPackets => _sentNumbers[0] + _sentNumbers[1] + _sentNumbers[2];
        internal int RetiredStreams => _streams?.RetiredStreams ?? 0;
        internal int RetainedIncoming => _streams?.IncomingCount ?? 0;
        internal int RetainedOutgoing => _streams?.OutgoingCount ?? 0;
        internal int RetainedApplicationInputs => _streams?.Protocol switch
        {
            WebTransportApplication application => application.RetainedInputs,
            StreamEchoApplication application => application.Retained,
            _ => 0
        };
        internal int OutboundResets => _streams?.OutboundResets ?? 0;
        internal int ReceiveCreditUpdates => _streams?.ReceiveCreditUpdates ?? 0;
        internal ulong StreamBytesReceived => _streams?.ReceivedData ?? 0;
        internal WebTransportApplication? WebTransport => _streams?.Protocol as WebTransportApplication;
        internal int RawStreamsCompleted => (_streams?.Protocol as StreamEchoApplication)?.Completed ?? 0;

        private readonly byte[] _originalId;
        private readonly byte[] _clientId;
        private readonly byte[] _serverId = RandomNumberGenerator.GetBytes(8);
        private readonly PacketKeys _initialRead;
        private readonly PacketKeys _initialWrite;
        private readonly OpenSslTls _tls;
        private readonly ulong[] _sentNumbers = new ulong[3];
        private readonly ulong[] _nextReceived = new ulong[3];
        private readonly ulong[] _cryptoOffsets = new ulong[3];
        private readonly PacketNumberWindow[] _received =
        [
            new(),
            new(),
            new()
        ];
        private readonly Reassembly _stream = new(StreamBudget);
        private readonly Queue<Delivery> _pending = new();
        private readonly Queue<Delivery> _urgent = new();
        private readonly Recovery _recovery;
        private readonly ConnectionLifetime _lifetime = new();
        private byte[]? _closePayload;
        private int _closeLevel;

        internal int ClosePackets { get; private set; }
        internal ConnectionPhase Phase => _lifetime.Phase;
        internal bool IdleExpired => _lifetime.IdleExpired;

        private double _now;
        private int _ackDelayExponent = 3;

        internal int LostPackets => _recovery.LostPackets;
        internal int RetransmittedPackets => _recovery.RetransmittedPackets;
        internal int ProbeTimeouts => _recovery.ProbeTimeouts;
        internal int CongestionEvents => _recovery.CongestionEvents;
        internal long BytesInFlight => _recovery.BytesInFlight;

        private bool _addressValidated;
        private bool _parametersValidated;
        private bool _handshakeDone;
        private bool _responseSent;
        private ulong _peerMaxData;
        private ulong _peerStreamData;

        internal ReadOnlySpan<byte> ServerId => _serverId;
        internal bool Established => _tls.Established;
        internal bool Complete =>
            _webTransport ? WebTransport?.AcceptedSessions > 0 : _streamed ? RawStreamsCompleted > 0 : _responseSent;
        internal bool Closed => Phase == ConnectionPhase.Closed;
        internal int EchoBytes { get; private set; }
        internal long ReceivedBytes { get; private set; }
        internal long SentBytes { get; private set; }
        internal int RejectedPackets { get; private set; }

        internal ProbeConnection(
            ReadOnlySpan<byte> firstPacket,
            string library,
            string certificate,
            string key,
            bool webTransport = false,
            string authorization = "",
            double now = 0,
            int keyUpdateInterval = KeyUpdateState.DefaultInterval,
            int encryptionLimit = PacketKeys.MaximumEncryptions,
            ulong authenticationLimit = 1UL << 52,
            bool multiplexed = false
        )
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(encryptionLimit, 16);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(encryptionLimit, PacketKeys.MaximumEncryptions);
            ArgumentOutOfRangeException.ThrowIfLessThan(authenticationLimit, 1UL);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(authenticationLimit, 1UL << 52);
            _authenticationLimit = authenticationLimit;
            _encryptionLimit = encryptionLimit;
            _keyUpdates = new KeyUpdateState(keyUpdateInterval);
            _now = now;
            _lifetime.Authenticated(now);
            _webTransport = webTransport;
            _streamed = webTransport || multiplexed;
            _authorization = authorization;
            _recovery = new Recovery(QueueRepeat);

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
                _tls = new OpenSslTls(
                    library,
                    certificate,
                    key,
                    LocalParameters(),
                    webTransport ? "h3" : OpenSslTls.ApplicationProtocol
                );
            }

            catch
            {
                _initialRead.Dispose();
                _initialWrite?.Dispose();
                throw;
            }
        }

        internal List<byte[]> Receive(ReadOnlySpan<byte> datagram, double now)
        {
            try
            {
                return ReceiveCore(datagram, now);
            }

            catch (Http3ProtocolException error)
            {
                BeginClose(2, Wire.Frame(0x1d, (ulong)error.ErrorCode, 0));
                return Drain();
            }

            catch (WebTransportException error)
            {
                BeginClose(2, Wire.Frame(0x1d, (ulong)error.ErrorCode, 0));
                return Drain();
            }

            catch (TransportException error)
            {
                BeginClose(
                    _tls.WriteKeys[2] != null ? 2 : _tls.WriteKeys[1] != null ? 1 : 0,
                    Wire.Frame(0x1c, error.ErrorCode, 0, 0)
                );
                return Drain();
            }

            catch (KeyUpdateException)
            {
                BeginClose(2, Wire.Frame(0x1c, 0x0e, 0, 0));
                return Drain();
            }

            catch (InvalidDataException)
            {
                BeginClose(
                    _tls.WriteKeys[2] != null ? 2 : _tls.WriteKeys[1] != null ? 1 : 0,
                    Wire.Frame(0x1c, 0x0a, 0, 0)
                );
                return Drain();
            }
        }

        private List<byte[]> ReceiveCore(ReadOnlySpan<byte> datagram, double now)
        {
            _lifetime.Tick(now, _recovery.KeyRetention);

            if (Closed || Phase == ConnectionPhase.Draining || _authenticationLimitReached) return [];

            _now = now;
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

                if (_recovery.IsDiscarded(level))
                {
                    position += length;
                    continue;
                }

                var keys = level == 0 ? _initialRead : _tls.ReadKeys[level];

                if (keys == null || (level == 2 && !_tls.Established))
                {
                    RejectedPackets++;
                    position += length;
                    continue;
                }

                byte[] plaintext;
                ulong number;

                try
                {
                    plaintext = level == 2
                        ? UnprotectApplication(remaining[..length], numberOffset, out number)
                        : keys.Unprotect(remaining[..length], numberOffset, _nextReceived[level], out number);
                }

                catch (CryptographicException)
                {
                    RejectedPackets++;

                    if (++_failedAuthentications >= _authenticationLimit)
                    {
                        _authenticationLimitReached = true;
                        BeginClose(level, Wire.Frame(0x1c, 0x0f, 0, 0));
                        break;
                    }

                    position += length;
                    continue;
                }

                _lifetime.Authenticated(now);

                if (Phase == ConnectionPhase.Closing)
                {
                    var closeReader = new WireReader(plaintext);

                    while (closeReader.Remaining != 0)
                    {
                        var type = closeReader.Integer();

                        if (type == 0) continue;

                        if (type is 0x1c or 0x1d)
                        {
                            if (type == 0x1d && level != 2)
                                throw new InvalidDataException("Application close outside application packet space.");

                            closeReader.Integer();

                            if (type == 0x1c) closeReader.Integer();

                            closeReader.Take(closeReader.Length(4096));
                            _lifetime.Drain(now, _recovery.KeyRetention);
                        }

                        break;
                    }

                    position += length;
                    continue;
                }

                var fresh = _received[level].Add(number);
                _nextReceived[level] = Math.Max(_nextReceived[level], number + 1);

                if (level == 1)
                {
                    _addressValidated = true;
                    _recovery.Discard(0);
                }

                if (fresh)
                {
                    var acknowledge = Frames(level, plaintext, number >= _keyUpdates.ReadMinimum);

                    if (acknowledge && Phase == ConnectionPhase.Active) AcknowledgeReceived(level);

                    if (Phase != ConnectionPhase.Active) break;

                    try
                    {
                        _tls.Pump();
                    }

                    catch (InvalidDataException) when (_tls.AlertCode.HasValue)
                    {
                        // RFC 9001 section 4.8: carry TLS alerts as QUIC CRYPTO_ERROR.
                        BeginClose(level, Wire.Frame(0x1c, 0x100UL + _tls.AlertCode.Value, 6, 0));
                        break;
                    }

                    ValidateParameters();
                    FlushCrypto();

                    if (_tls.Established && !_handshakeDone)
                    {
                        if (!_parametersValidated) throw new InvalidDataException("Missing peer transport parameters.");

                        _nextRead ??= (_tls.ReadKeys[2]
                            ?? throw new InvalidOperationException("Missing application keys.")).Next();
                        _handshakeDone = true;
                        _recovery.HandshakeConfirmed = true;
                        _recovery.Discard(1);
                        Send(2, Wire.Frame(0x1e));

                        if (_streamed)
                            _streams = new ConnectionStreams(
                                (bytes, reliable) =>
                                {
                                    var delivery = new Delivery(2, bytes, reliable);
                                    QueueRepeat(delivery, false);
                                    return delivery;
                                },
                                _peerMaxData,
                                _peerStreamData,
                                _peerRemoteStreamData,
                                _peerUniStreamData,
                                _peerUniCount,
                                _peerBidiCount,
                                _peerDatagram,
                                _peerReset,
                                createApplication: operations => _webTransport
                                    ? new WebTransportApplication(
                                        _peerDatagram > 0 && _peerReset,
                                        operations,
                                        headers => headers.TryGetValue("authorization", out var value)
                                            && value == "Bearer " + _authorization
                                    )
                                    : new StreamEchoApplication(operations)
                            );
                    }

                    TryEcho();
                }

                else AcknowledgeReceived(level);

                position += length;
            }

            return Drain();
        }

        private byte[] UnprotectApplication(ReadOnlySpan<byte> packet, int offset, out ulong number)
        {
            var current = _tls.ReadKeys[2] ?? throw new InvalidOperationException("Missing application keys.");
            var decoded = current.Decode(packet, offset, _nextReceived[2]);
            number = decoded.Number;
            var phase = decoded.Phase;

            if (phase == _keyUpdates.ReadPhase)
            {
                var currentPayload = current.Unprotect(decoded, phase);
                _keyUpdates.ReadCurrent(number);
                return currentPayload;
            }

            if (number < _keyUpdates.ReadMinimum)
            {
                if (_previousRead == null || _now > _previousExpires)
                    throw new CryptographicException("Previous packet keys expired.");

                var previous = _previousRead.Unprotect(decoded, phase);
                _keyUpdates.ReadPrevious(number);
                return previous;
            }

            // Next keys are prepared when TLS installs the application keys and
            // after an authenticated transition, not on a forged phase change.
            var next = _nextRead ?? throw new CryptographicException("Next packet keys unavailable.");
            var plaintext = next.Unprotect(decoded, phase);

            if (!_recovery.HandshakeConfirmed || number < _nextReceived[2])
                throw new KeyUpdateException("Invalid key update ordering.");

            var mirrorWrite = _keyUpdates.PeerUpdated(number, _sentNumbers[2]);
            _previousRead?.Dispose();
            _previousRead = current;
            _previousExpires = _now + _recovery.KeyRetention;
            _tls.ReadKeys[2] = _nextRead;
            _nextRead = null;

            if (mirrorWrite) UpdateWriteKeys();

            _nextRead = next.Next();
            KeyUpdates++;
            return plaintext;
        }

        internal List<byte[]> Tick(double now)
        {
            _now = now;

            if (_previousRead != null && now > _previousExpires)
            {
                _previousRead.Dispose();
                _previousRead = null;
            }

            _lifetime.Tick(now, _recovery.KeyRetention);

            if (Phase == ConnectionPhase.Active && (_addressValidated || SentBytes + 1200 <= ReceivedBytes * 3))
                _recovery.Tick(now);

            return Drain();
        }

        internal List<byte[]> CloseProtocol(double now)
        {
            _now = now;
            BeginClose(_tls.WriteKeys[2] != null ? 2 : _tls.WriteKeys[1] != null ? 1 : 0, Wire.Frame(0x1c, 0x0a, 0, 0));
            return Drain();
        }

        private void BeginClose(int level, byte[] payload)
        {
            if (Phase != ConnectionPhase.Active) return;

            _pending.Clear();
            _urgent.Clear();
            _closeLevel = level;
            _closePayload = payload;
            _lifetime.Close(_now, _recovery.KeyRetention);
        }

        private void AcknowledgeReceived(int level)
        {
            var ranges = _received[level].Descending();

            using var frame = new MemoryStream();
            Wire.Integer(frame, 2);
            Wire.Integer(frame, ranges[0].High);
            Wire.Integer(frame, 0);
            Wire.Integer(frame, (ulong)ranges.Count - 1);
            Wire.Integer(frame, ranges[0].High - ranges[0].Low);

            for (var i = 1; i < ranges.Count; i++)
            {
                Wire.Integer(frame, ranges[i - 1].Low - ranges[i].High - 2);
                Wire.Integer(frame, ranges[i].High - ranges[i].Low);
            }

            Send(level, frame.ToArray(), false, false);
        }

        private void QueueRepeat(Delivery delivery, bool probe)
        {
            if (Phase != ConnectionPhase.Active || delivery.Acknowledged || _recovery.IsDiscarded(delivery.Level))
                return;

            if (delivery.Queued)
            {
                if (probe && !delivery.ProbePending)
                {
                    if (_urgent.Count >= 128) throw new InvalidDataException("Urgent queue budget exceeded.");

                    delivery.ProbePending = true;
                    _urgent.Enqueue(delivery);
                }

                return;
            }

            if (_pending.Count + _urgent.Count >= 512)
                throw new InvalidDataException("Outbound probe budget exceeded.");

            delivery.Queued = true;
            delivery.ProbePending = probe;
            (probe || !delivery.Eliciting ? _urgent : _pending).Enqueue(delivery);
        }

        private List<byte[]> Drain()
        {
            var output = new List<byte[]>();

            if (Phase != ConnectionPhase.Active)
            {
                _pending.Clear();
                _urgent.Clear();
                _recovery.Abandon();
                _streams?.Dispose();

                if (
                    Phase == ConnectionPhase.Closing
                    && _closePayload != null
                    && (_addressValidated || SentBytes + 1200 <= ReceivedBytes * 3)
                    && _lifetime.TakeCloseResponse(_now)
                )
                {
                    var keys = _closeLevel == 0 ? _initialWrite : _tls.WriteKeys[_closeLevel];

                    if (keys == null || !keys.CanEncrypt || _sentNumbers[_closeLevel] > PacketNumberWindow.Maximum)
                    {
                        _lifetime.Drain(_now, _recovery.KeyRetention);
                        return output;
                    }

                    var packet = Protect(new Delivery(_closeLevel, _closePayload, false, false));
                    _sentNumbers[_closeLevel]++;
                    SentBytes += packet.Length;
                    ClosePackets++;
                    output.Add(packet);
                }

                return output;
            }

            _streams?.Prune();

            while (_urgent.Count + _pending.Count != 0)
            {
                var queue = _urgent.Count != 0 ? _urgent : _pending;
                var delivery = queue.Peek();
                var level = delivery.Level;

                if (!delivery.Queued || delivery.Acknowledged || _recovery.IsDiscarded(level))
                {
                    queue.Dequeue();
                    delivery.Queued = false;
                    continue;
                }

                if (!PrepareKeys(level, delivery.Eliciting))
                {
                    output.AddRange(Drain());
                    return output;
                }

                var (header, numberOffset, payload) = Prepare(delivery);
                var size = header.Length + payload.Length + 16;

                if (!_addressValidated && SentBytes + size > ReceivedBytes * 3) break;

                if (delivery.Eliciting && !_recovery.CanSend(size, _now, queue == _urgent)) break;

                var keys = (level == 0 ? _initialWrite : _tls.WriteKeys[level])
                    ?? throw new InvalidOperationException("Packet keys unavailable.");
                var packet = keys.Protect(header, numberOffset, _sentNumbers[level], payload);

                queue.Dequeue();
                delivery.Queued = false;
                delivery.ProbePending = false;
                _recovery.SentPacket(
                    level,
                    _sentNumbers[level]++,
                    packet.Length,
                    _now,
                    delivery,
                    level == 0,
                    _pending.Count == 0
                );
                SentBytes += packet.Length;

                if (delivery.Eliciting) _lifetime.SentEliciting(_now);

                output.Add(packet);
            }

            return output;
        }

        private bool TryHeader(ReadOnlySpan<byte> packet, out int level, out int length, out int numberOffset) =>
            PacketHeader.TryRead(packet, _originalId, _serverId, _clientId, out level, out length, out numberOffset);

        private bool Frames(int level, ReadOnlySpan<byte> plaintext, bool currentReadKeys)
        {
            var reader = new WireReader(plaintext);
            var acknowledge = false;

            while (reader.Remaining != 0)
            {
                var type = reader.Integer();

                if (type == 0) continue;

                if (type is 2 or 3)
                {
                    var acknowledged = AckRanges.Read(
                        ref reader,
                        _sentNumbers[level],
                        _ackDelayExponent,
                        out var delay
                    );

                    _recovery.Acknowledge(level, acknowledged, delay, _now);

                    if (level == 2) _keyUpdates.Acknowledge(acknowledged[0].High, currentReadKeys, _now);

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
                    if (type == 0x1d && level != 2)
                        throw new InvalidDataException("Application close outside application packet space.");

                    reader.Integer();

                    if (type == 0x1c) reader.Integer();

                    reader.Take(reader.Length(4096));
                    _pending.Clear();
                    _urgent.Clear();
                    _lifetime.Drain(_now, _recovery.KeyRetention);
                    return false;
                }

                if (level != 2 || !_tls.Established)
                    throw new InvalidDataException("Application frame before TLS completion.");

                if (type is >= 8 and <= 15)
                {
                    var id = reader.Integer();

                    if (!_streamed && id != 0)
                        throw new InvalidDataException("Only client bidirectional stream 0 is admitted by the probe.");

                    var offset = (type & 4) != 0 ? reader.Integer() : 0;
                    var length = (type & 2) != 0 ? reader.Length(_streamed ? 65536 : StreamBudget) : reader.Remaining;
                    var bytes = reader.Take(length);

                    if (_streams != null) _streams.Receive(id, offset, bytes.ToArray(), (type & 1) != 0);

                    else _stream.Add(offset, bytes, (type & 1) != 0);
                }

                else if (type == 0x10)
                {
                    _peerMaxData = Math.Max(_peerMaxData, reader.Integer());
                    _streams?.UpdateData(_peerMaxData);
                }

                else if (type == 0x11)
                {
                    var id = reader.Integer();
                    var maximum = reader.Integer();

                    if (id == 0) _peerStreamData = Math.Max(_peerStreamData, maximum);

                    _streams?.UpdateStream(id, maximum);
                }

                else if (type is 0x12 or 0x13)
                {
                    var value = reader.Integer();
                    _streams?.UpdateCount(type == 0x13, value);
                }

                else if (_streams != null && type is 4 or 0x24)
                {
                    var id = reader.Integer();
                    var error = reader.Integer();
                    var final = reader.Integer();
                    var reliable = type == 0x24 ? reader.Integer() : 0;
                    _streams.Reset(id, error, final, reliable, type == 0x24);
                }

                else if (_streams != null && type == 5) _streams.Stop(reader.Integer(), reader.Integer());

                else if (_streams != null && type is 0x30 or 0x31)
                {
                    var length = type == 0x31 ? reader.Length(1100) : reader.Remaining;
                    _streams.Datagram(
                        reader
                            .Take(length)
                            .ToArray()
                    );
                }

                else if (type is 0x14 or 0x16 or 0x17) reader.Integer();

                else if (type == 0x15)
                {
                    reader.Integer();
                    reader.Integer();
                }

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
            Wire.Parameter(output, 4, _streamed ? 262144UL : StreamBudget);
            Wire.Parameter(output, 5, _streamed ? 65536UL : StreamBudget);
            Wire.Parameter(output, 6, _streamed ? 65536UL : StreamBudget);
            Wire.Parameter(output, 7, _streamed ? 65536UL : 0);
            Wire.Parameter(output, 8, _streamed ? 16UL : 1);
            Wire.Parameter(output, 9, _streamed ? 16UL : 0);
            Wire.Parameter(output, 12, []);
            Wire.Parameter(output, 14, 2);
            Wire.Parameter(output, 15, _serverId);

            if (_streamed)
            {
                Wire.Parameter(output, 0x20, 1200);
                Wire.Parameter(output, 0x1d, []);
            }

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

                else if (id == 0x1d)
                {
                    if (bytes.Length != 0) throw new InvalidDataException("Invalid reliable-reset parameter.");

                    _peerReset = true;
                }

                else if (id is >= 1 and <= 11 or 14 or 0x20)
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
                        || (id is 8 or 9 && value > 1UL << 60)
                    )
                        throw new InvalidDataException("Invalid transport parameter value.");

                    if (id == 1 && value != 0) _lifetime.IdleTimeout = Math.Min(10000, value);

                    if (id == 10) _ackDelayExponent = (int)value;

                    if (id == 11) _recovery.MaximumAckDelay = value;

                    if (id == 4) _peerMaxData = value;

                    if (id == 5) _peerStreamData = value;

                    if (id == 6) _peerRemoteStreamData = value;

                    if (id == 7) _peerUniStreamData = value;

                    if (id == 8) _peerBidiCount = value;

                    if (id == 9) _peerUniCount = value;

                    if (id == 0x20) _peerDatagram = value;
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
            if (_streamed || _responseSent || !_tls.Established || !_stream.Complete) return;

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

        private void Send(int level, byte[] payload, bool reliable = true, bool eliciting = true) =>
            QueueRepeat(new Delivery(level, payload, reliable, eliciting), false);

        private void UpdateWriteKeys()
        {
            var current = _tls.WriteKeys[2] ?? throw new InvalidOperationException("Missing application write keys.");
            _tls.WriteKeys[2] = current.Next();
            current.Dispose();
        }

        private bool PrepareKeys(int level, bool eliciting)
        {
            var keys = (level == 0 ? _initialWrite : _tls.WriteKeys[level])
                ?? throw new InvalidOperationException("Packet keys unavailable.");

            if (
                level == 2
                && eliciting
                && _keyUpdates.TryInitiate(
                    _sentNumbers[2],
                    keys.Encryptions,
                    _recovery.HandshakeConfirmed,
                    _now,
                    _recovery.KeyRetention
                )
            )
            {
                UpdateWriteKeys();
                LocalKeyUpdates++;
                keys = _tls.WriteKeys[2] ?? throw new InvalidOperationException("Missing updated write keys.");
            }

            if (keys.Encryptions < _encryptionLimit - 8 && _sentNumbers[level] < PacketNumberWindow.Maximum - 8)
                return true;

            BeginClose(level, Wire.Frame(0x1c, 0x0f, 0, 0));
            return false;
        }

        private byte[] Protect(Delivery delivery)
        {
            var (header, numberOffset, payload) = Prepare(delivery);
            var keys = (delivery.Level == 0 ? _initialWrite : _tls.WriteKeys[delivery.Level])
                ?? throw new InvalidOperationException("Packet keys are unavailable.");
            return keys.Protect(header, numberOffset, _sentNumbers[delivery.Level], payload);
        }

        private (byte[] Header, int NumberOffset, byte[] Payload) Prepare(Delivery delivery)
        {
            var level = delivery.Level;
            var payload = delivery.Bytes;

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
                header.WriteByte(_keyUpdates.WritePhase ? (byte)0x47 : (byte)0x43);
                header.Write(_clientId);
            }

            var numberOffset = (int)header.Length;
            var number = _sentNumbers[level];
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, unchecked((uint)number));
            header.Write(encoded);
            return (header.ToArray(), numberOffset, payload);
        }

        public void Dispose()
        {
            _previousRead?.Dispose();
            _nextRead?.Dispose();
            _streams?.Dispose();
            _tls.Dispose();
            _initialRead.Dispose();
            _initialWrite.Dispose();
        }
    }
}
