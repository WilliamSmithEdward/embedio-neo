using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace EmbedIO.Quic
{
    internal readonly record struct QuicPeerFeatures(ulong MaximumDatagramFrameSize, bool ReliableReset);

    // A single owner drives packet, TLS, recovery and stream state. TLS and the
    // stream application are supplied by the listener; no HTTP policy lives here.
    internal class QuicConnection : IDisposable
    {
        private readonly Func<StreamOperations, QuicPeerFeatures, StreamApplication> _createApplication;
        private readonly QuicConnectionLimits _limits;
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
        internal int OutboundResets => _streams?.OutboundResets ?? 0;
        internal int ReceiveCreditUpdates => _streams?.ReceiveCreditUpdates ?? 0;
        internal ulong StreamBytesReceived => _streams?.ReceivedData ?? 0;
        internal StreamApplication? Application => _streams?.Protocol;
        internal TlsSession Tls => _tls;

        private readonly byte[] _originalId;
        private readonly byte[] _clientId;
        private byte[] _destinationId;
        private PeerConnectionIds? _peerIds;
        private readonly byte[] _serverId;
        private readonly LocalConnectionIds _localIds;
        private readonly QuicConnectionIdIssuer? _ownedIdIssuer;
        private readonly PacketKeys _initialRead;
        private readonly PacketKeys _initialWrite;
        private readonly TlsSession _tls;
        private readonly ulong[] _sentNumbers = new ulong[3];
        private readonly ulong[] _nextReceived = new ulong[3];
        private readonly ulong[] _cryptoOffsets = new ulong[3];
        private readonly PacketNumberWindow[] _received =
        [
            new(),
            new(),
            new()
        ];
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
        private Func<double>? _executionClock;
        private int _ackDelayExponent = 3;

        internal int LostPackets => _recovery.LostPackets;
        internal int RetransmittedPackets => _recovery.RetransmittedPackets;
        internal int ProbeTimeouts => _recovery.ProbeTimeouts;
        internal int CongestionEvents => _recovery.CongestionEvents;
        internal long BytesInFlight => _recovery.BytesInFlight;

        private bool _addressValidated;
        private bool _parametersValidated;
        private bool _disposed;
        private int _peerProtectionLevel;
        private bool _handshakeDone;
        private ulong _peerMaxData;
        private ulong _peerStreamData;

        internal ReadOnlySpan<byte> ServerId => _serverId;
        internal IReadOnlyList<ReadOnlyMemory<byte>> IssuedConnectionIds => _localIds.Snapshot;
        internal ReadOnlyMemory<byte> ResetToken => _peerIds?.ResetToken ?? default;
        internal bool StatelessResetReceived { get; private set; }
        internal bool DatagramAuthenticated { get; private set; }
        internal bool Established => _tls.Established;
        private int CloseLevel =>
            _tls.Established && _tls.WriteKeys[2] != null
                ? 2
                : _peerProtectionLevel >= 1 && _tls.WriteKeys[1] != null ? 1 : 0;
        internal bool Closed => Phase == ConnectionPhase.Closed;
        internal long ReceivedBytes { get; private set; }
        internal long SentBytes { get; private set; }
        internal int RejectedPackets { get; private set; }

        internal QuicConnection(
            ReadOnlySpan<byte> firstPacket,
            Func<byte[], TlsSession> tlsFactory,
            Func<StreamOperations, QuicPeerFeatures, StreamApplication> createApplication,
            double now = 0,
            int keyUpdateInterval = KeyUpdateState.DefaultInterval,
            int encryptionLimit = PacketKeys.MaximumEncryptions,
            ulong authenticationLimit = 1UL << 52,
            QuicConnectionLimits? limits = null,
            IQuicConnectionIdSource? connectionIds = null
        )
        {
            ArgumentNullException.ThrowIfNull(tlsFactory);
            ArgumentNullException.ThrowIfNull(createApplication);
            _createApplication = createApplication;
            _limits = limits ?? new QuicConnectionLimits();
            _limits.Validate();
            _lifetime.IdleTimeout = _limits.IdleTimeoutMilliseconds;
            ArgumentOutOfRangeException.ThrowIfLessThan(encryptionLimit, 16);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(encryptionLimit, PacketKeys.MaximumEncryptions);
            ArgumentOutOfRangeException.ThrowIfLessThan(authenticationLimit, 1UL);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(authenticationLimit, 1UL << 52);
            _authenticationLimit = authenticationLimit;
            _encryptionLimit = encryptionLimit;
            _keyUpdates = new KeyUpdateState(keyUpdateInterval);
            _now = now;
            _lifetime.Authenticated(now);
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
            _destinationId = _clientId;
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
                _ownedIdIssuer = connectionIds == null ? new QuicConnectionIdIssuer() : null;
                var issuer = connectionIds
                    ?? _ownedIdIssuer
                    ?? throw new InvalidOperationException("Missing connection-ID issuer.");
                _serverId = issuer.Create();
                _localIds = new LocalConnectionIds(issuer, _serverId, delivery => QueueRepeat(delivery, false));
                _tls = tlsFactory(LocalParameters());
            }

            catch
            {
                _initialRead.Dispose();
                _initialWrite?.Dispose();
                _localIds?.Dispose();
                _ownedIdIssuer?.Dispose();
                throw;
            }
        }

        internal virtual List<byte[]> Receive(ReadOnlySpan<byte> datagram, double now)
        {
            DatagramAuthenticated = false;

            try
            {
                return ReceiveCore(datagram, now);
            }

            catch (TransportException error)
            {
                BeginClose(CloseLevel, Wire.Frame(0x1c, error.ErrorCode, 0, 0));
                return Drain();
            }

            catch (KeyUpdateException)
            {
                BeginClose(2, Wire.Frame(0x1c, 0x0e, 0, 0));
                return Drain();
            }

            catch (InvalidDataException)
            {
                BeginClose(CloseLevel, Wire.Frame(0x1c, 0x0a, 0, 0));
                return Drain();
            }
        }

        private List<byte[]> ReceiveCore(ReadOnlySpan<byte> datagram, double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lifetime.Tick(now, _recovery.KeyRetention);

            if (Closed || Phase == ConnectionPhase.Draining) return [];

            _now = now;

            if (_authenticationLimitReached) return DetectReset(datagram) ? Drain() : [];

            ReceivedBytes += datagram.Length;
            var position = 0;

            while (position < datagram.Length)
            {
                var remaining = datagram[position..];

                if (!TryHeader(remaining, out var level, out var length, out var numberOffset, out var destination))
                {
                    if (position == 0 && DetectReset(datagram)) break;

                    RejectedPackets++;
                    break;
                }

                if (_recovery.IsDiscarded(level))
                {
                    if (position == 0 && DetectReset(datagram)) break;

                    position += length;
                    continue;
                }

                var keys = level == 0 ? _initialRead : _tls.ReadKeys[level];

                if (keys == null || (level == 2 && !_tls.Established))
                {
                    if (position == 0 && DetectReset(datagram)) break;

                    RejectedPackets++;
                    position += length;
                    continue;
                }

                if (_failedAuthentications != 0 && StopAtAuthenticationLimit(level, keys)) break;

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
                    if (position == 0 && DetectReset(datagram)) break;

                    RejectedPackets++;

                    _failedAuthentications++;

                    if (StopAtAuthenticationLimit(level, keys)) break;

                    position += length;
                    continue;
                }

                DatagramAuthenticated = true;
                _lifetime.Authenticated(now);
                _peerProtectionLevel = Math.Max(_peerProtectionLevel, level);

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
                    var acknowledge = Frames(level, plaintext, number >= _keyUpdates.ReadMinimum, destination);

                    if (acknowledge && Phase == ConnectionPhase.Active) AcknowledgeReceived(level);

                    if (Phase != ConnectionPhase.Active) break;

                    try
                    {
                        PumpTls();
                    }

                    catch (InvalidDataException) when (_tls.AlertCode.HasValue)
                    {
                        // RFC 9001 section 4.8: carry TLS alerts as QUIC CRYPTO_ERROR.
                        BeginClose(level, Wire.Frame(0x1c, 0x100UL + _tls.AlertCode.Value, 6, 0));
                        break;
                    }

                    now = _now;
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
                        _localIds.Activate();

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
                            createApplication: operations => _createApplication(
                                operations,
                                new QuicPeerFeatures(_peerDatagram, _peerReset)
                            ),
                            canQueue: count => _pending.Count + _urgent.Count + (count + 899) / 900 + 1 <= 480,
                            close: code => BeginClose(2, Wire.Frame(0x1d, code, 0)),
                            inboundBidirectionalStreams: _limits.InboundBidirectionalStreams,
                            inboundUnidirectionalStreams: _limits.InboundUnidirectionalStreams
                        );
                    }
                }

                else AcknowledgeReceived(level);

                position += length;
            }

            return Drain();
        }

        private bool DetectReset(ReadOnlySpan<byte> datagram)
        {
            if (_peerIds?.MatchesReset(datagram) != true) return false;

            StatelessResetReceived = true;
            _lifetime.Drain(_now, _recovery.KeyRetention);
            _streams?.Protocol.ConnectionClosed(0, false);
            return true;
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            _now = now;
            BeginClose(CloseLevel, Wire.Frame(0x1c, 0x0a, 0, 0));
            return Drain();
        }

        internal List<byte[]> CloseApplication(ulong errorCode, double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _now = now;
            BeginClose(2, Wire.Frame(0x1d, errorCode, 0));
            return Drain();
        }

        internal List<byte[]> PumpApplications(double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _now = now;
            return Drain();
        }

        internal void CancelApplications() => _streams?.Dispose();

        private bool StopAtAuthenticationLimit(int level, PacketKeys keys)
        {
            var limit = Math.Min(
                _authenticationLimit,
                (_tls.ReadKeys[2] ?? _tls.ReadKeys[1] ?? keys).AuthenticationLimit
            );

            if (_failedAuthentications < limit) return false;

            _authenticationLimitReached = true;
            BeginClose(level, Wire.Frame(0x1c, 0x0f, 0, 0));
            return true;
        }

        internal void UseExecutionClock(Func<double> clock)
        {
            ArgumentNullException.ThrowIfNull(clock);

            if (_executionClock != null)
                throw new InvalidOperationException("Connection already has an execution clock.");

            _executionClock = clock;
        }

        private void PumpTls()
        {
            var clock = _executionClock;

            if (clock == null || _tls.Established)
            {
                _tls.Pump();
                return;
            }

            try
            {
                _tls.Pump();
            }

            finally
            {
                // Certificate selection and validation may block beyond a packet's
                // arrival time. Closing/recovery must use the completion time.
                _now = clock();
            }
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
                throw new InvalidDataException("Outbound queue budget exceeded.");

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

            if (Phase != ConnectionPhase.Active) return Drain();

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
                _peerIds?.MarkUsed();

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

        private bool TryHeader(
            ReadOnlySpan<byte> packet,
            out int level,
            out int length,
            out int numberOffset,
            out ReadOnlySpan<byte> destination
        )
        {
            level = length = numberOffset = 0;

            if (!PacketHeader.TryDestination(packet, out destination, out _)) return false;

            if (!_localIds.Contains(destination) && ((packet[0] & 128) == 0 || !destination.SequenceEqual(_originalId)))
                return false;

            return PacketHeader.TryRead(
                packet,
                _originalId,
                destination,
                _clientId,
                out level,
                out length,
                out numberOffset
            );
        }

        private bool Frames(
            int level,
            ReadOnlySpan<byte> plaintext,
            bool currentReadKeys,
            ReadOnlySpan<byte> destination
        )
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

                    var closeCode = reader.Integer();

                    if (type == 0x1c) reader.Integer();

                    reader.Take(reader.Length(4096));
                    _streams?.Protocol.ConnectionClosed(closeCode, type == 0x1d);
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

                    var offset = (type & 4) != 0 ? reader.Integer() : 0;
                    var length = (type & 2) != 0 ? reader.Length(65536) : reader.Remaining;
                    var bytes = reader.Take(length);

                    (_streams ?? throw new InvalidOperationException("Missing established stream owner.")).Receive(
                        id,
                        offset,
                        bytes.ToArray(),
                        (type & 1) != 0
                    );
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
                    ReadPeerConnectionId(ref reader);
                }

                else if (type == 0x19) RetireLocalConnectionId(ref reader, destination);

                else if (type is 0x1a or 0x1b) ReadPathControl(ref reader, type);

                else
                    throw new InvalidDataException(
                        "Unsupported QUIC frame: " + type.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    );
            }

            return acknowledge;
        }

        private void ReadPathControl(ref WireReader reader, ulong type)
        {
            ReadOnlySpan<byte> data;

            try
            {
                data = reader.Take(8);
            }

            catch (InvalidDataException error)
            {
                throw new TransportException(7, error.Message);
            }

            // No challenge is outstanding until path initiation is integrated.
            // RFC 9000 19.18 permits the existing protocol-violation policy.
            if (type == 0x1b) throw new TransportException(10, "Unmatched path response.");

            var response = new byte[9];
            response[0] = 0x1b;
            data.CopyTo(response.AsSpan(1));
            // RFC 9000 8.2.2/13.3: exactly one response per received challenge;
            // recovery may elicit fresh traffic, but must not repeat this frame.
            QueueRepeat(new Delivery(2, response, false) { PadToMinimum = true }, false);
        }

        private void RetireLocalConnectionId(ref WireReader reader, ReadOnlySpan<byte> destination)
        {
            ulong sequence;

            try
            {
                sequence = reader.Integer();
            }

            catch (InvalidDataException error)
            {
                throw new TransportException(7, error.Message);
            }

            _localIds.Retire(sequence, destination);
        }

        private void ReadPeerConnectionId(ref WireReader reader)
        {
            try
            {
                var sequence = reader.Integer();
                var retirePrior = reader.Integer();
                var size = reader.Byte();

                if (size is 0 or > 20) throw new TransportException(7, "Invalid peer connection-ID length.");

                var id = reader.Take(size);
                var token = reader.Take(16);
                _peerIds ??= new PeerConnectionIds(_clientId, RetirePeerConnectionId, SentBytes != 0);

                try
                {
                    _peerIds.Add(sequence, retirePrior, id, token);
                }

                finally
                {
                    // A valid increased watermark also applies if a subsequent
                    // retirement budget closes the connection. Never close on an old ID.
                    if (!_peerIds.Current.SequenceEqual(_destinationId)) _destinationId = _peerIds.Current.ToArray();
                }
            }

            catch (InvalidDataException error)
            {
                throw new TransportException(7, error.Message);
            }
        }

        private Delivery RetirePeerConnectionId(ulong sequence)
        {
            var delivery = new Delivery(2, Wire.Frame(0x19, sequence));
            QueueRepeat(delivery, false);
            return delivery;
        }

        private byte[] LocalParameters()
        {
            using var output = new MemoryStream();
            Wire.Parameter(output, 0, _originalId);
            Wire.Parameter(output, 1, (ulong)_limits.IdleTimeoutMilliseconds);
            Wire.Parameter(output, 2, _localIds.InitialToken);
            Wire.Parameter(output, 3, 1472);
            Wire.Parameter(output, 4, 262144);
            Wire.Parameter(output, 5, 65536);
            Wire.Parameter(output, 6, 65536);
            Wire.Parameter(output, 7, 65536);
            Wire.Parameter(output, 8, (ulong)_limits.InboundBidirectionalStreams);
            Wire.Parameter(output, 9, (ulong)_limits.InboundUnidirectionalStreams);
            Wire.Parameter(output, 12, []);
            Wire.Parameter(output, 14, 2);
            Wire.Parameter(output, 15, _serverId);

            Wire.Parameter(output, 0x20, 1200);
            Wire.Parameter(output, 0x1d, []);

            return output.ToArray();
        }

        private void ValidateParameters()
        {
            if (_parametersValidated || _tls.PeerParameters == null) return;

            try
            {
                ReadParameters();
            }

            catch (InvalidDataException error)
            {
                throw new TransportException(8, error.Message);
            }
        }

        private void ReadParameters()
        {
            var reader = new WireReader(
                _tls.PeerParameters ?? throw new InvalidOperationException("Missing peer transport parameters.")
            );
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
                        (id == 3 && (value < 1200 || value > 65527))
                        || (id == 10 && value > 20)
                        || (id == 11 && value >= 16384)
                        || (id == 14 && value < 2)
                        || (id is 8 or 9 && value > 1UL << 60)
                    )
                        throw new InvalidDataException("Invalid transport parameter value.");

                    if (id == 1 && value != 0)
                        _lifetime.IdleTimeout = Math.Min((ulong)_limits.IdleTimeoutMilliseconds, value);

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
            var packet = keys.Protect(header, numberOffset, _sentNumbers[delivery.Level], payload);
            _peerIds?.MarkUsed();
            return packet;
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
                header.WriteByte((byte)_destinationId.Length);
                header.Write(_destinationId);
                header.WriteByte((byte)_localIds.Current.Length);
                header.Write(_localIds.Current);

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
                header.Write(_destinationId);
            }

            var numberOffset = (int)header.Length;
            var number = _sentNumbers[level];
            Span<byte> encoded = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(encoded, unchecked((uint)number));
            header.Write(encoded);

            if (delivery.PadToMinimum)
            {
                var minimum = _addressValidated
                    ? 1200
                    : (int)Math.Min(1200, Math.Max(0, ReceivedBytes * 3 - SentBytes));
                Array.Resize(ref payload, Math.Max(payload.Length, minimum - (int)header.Length - 16));
            }

            return (header.ToArray(), numberOffset, payload);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _executionClock = null;
            _pending.Clear();
            _urgent.Clear();
            _recovery.Abandon();
            _peerIds?.Dispose();
            _localIds.Dispose();
            _ownedIdIssuer?.Dispose();

            try
            {
                _streams?.Dispose();
            }

            finally
            {
                try
                {
                    _tls.Dispose();
                }

                finally
                {
                    _previousRead?.Dispose();
                    _nextRead?.Dispose();
                    _initialRead.Dispose();
                    _initialWrite.Dispose();
                }
            }
        }
    }
}
