using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
        internal int DeferredDeliveries => _deferred.Count;
        internal int ReceiveCreditUpdates => _streams?.ReceiveCreditUpdates ?? 0;
        internal ulong StreamBytesReceived => _streams?.ReceivedData ?? 0;
        internal StreamApplication? Application => _streams?.Protocol;
        internal TlsSession Tls => _tls;

        private readonly byte[] _originalId;
        private readonly byte[] _clientId;
        private readonly QuicRetry? _retry;
        private byte[] _destinationId;
        private PeerConnectionIds? _peerIds;
        private IReadOnlyList<QuicPathRoute>? _retirementPaths;
        private bool _retireUnusedPeerIds;
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
        private readonly Queue<Delivery> _deferred = new();
        private const int MaximumReadyDeliveries = 512;
        private const int MaximumDeferredDeliveries = Recovery.MaximumActivePackets + MaximumReadyDeliveries;
        private readonly Recovery _recovery;
        private readonly ConnectionLifetime _lifetime = new();
        private byte[]? _closePayload;
        private int _closeLevel;

        internal int ClosePackets { get; private set; }
        internal ConnectionPhase Phase => _lifetime.Phase;
        internal bool IdleExpired => _lifetime.IdleExpired;

        private double _now;
        private Func<double>? _executionClock;
        private QuicPathValidation? _paths;
        private QuicConnectionEndpoints? _incomingEndpoints;
        private QuicPath? _incomingPath;
        private List<QuicPathRoute?>? _outputPaths;
        private IPAddress? _congestionAddress;

        internal IReadOnlyList<QuicPathRoute?>? OutputPaths => _outputPaths;
        internal IReadOnlyList<QuicPathRoute>? ActivePaths => _paths?.Snapshot;
        internal IReadOnlyList<ReadOnlyMemory<byte>> ResetTokens => _peerIds?.ResetTokens ?? [];

        internal void BeginRouting(QuicConnectionEndpoints? endpoints)
        {
            if (_paths == null && endpoints != null)
            {
                _paths = new QuicPathValidation(endpoints);
                _congestionAddress = endpoints.Peer.Address;

                // A redeemed Retry token already proved this address (RFC 9000 8.1.2), so the
                // initial path is not held to three times what the client sent.
                if (_retry != null) _paths.ValidateInitial();
            }

            _incomingEndpoints = endpoints;
            _incomingPath = null;
            _outputPaths = [];
        }

        private void ObserveValidatedPath()
        {
            if (
                _paths?.Current is not { AddressValidated: true } path
                || Equals(_congestionAddress, path.Route.Endpoints.Peer.Address)
            ) return;

            _congestionAddress = path.Route.Endpoints.Peer.Address;
            _recovery.ChangePath(_now);
        }

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
            IQuicConnectionIdSource? connectionIds = null,
            QuicRetry? retry = null
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
            // After a Retry this Initial's destination is the Retry's source ID; the token
            // carried the first Initial's destination, and the client proved its address.
            _retry = retry;
            _addressValidated = retry != null;
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

            // Packets are decrypted in place, in a copy the connection owns and clears.
            var buffer = ArrayPool<byte>.Shared.Rent(datagram.Length);
            var owned = buffer.AsSpan(0, datagram.Length);
            datagram.CopyTo(owned);

            try
            {
                return ReceiveCore(owned, now);
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

            finally
            {
                owned.Clear();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private List<byte[]> ReceiveCore(Span<byte> datagram, double now)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_paths != null && _incomingEndpoints != null && !_handshakeDone && !_paths.Contains(_incomingEndpoints))
                return [];

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

                Span<byte> plaintext;
                ulong number;

                try
                {
                    if (level == 2) plaintext = UnprotectApplication(remaining[..length], numberOffset, out number);

                    else
                    {
                        var packet = remaining[..length];
                        var headerLength = keys.Unmask(packet, numberOffset, _nextReceived[level], out number);
                        plaintext = keys.Open(packet, headerLength, number, false);
                    }
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

                var largestBefore = _received[level].Largest;
                var fresh = _received[level].Add(number);

                if (fresh && level == 2 && (largestBefore is not { } largest || number > largest))
                    _largestReceivedAt = now;

                if (_paths != null && _incomingEndpoints != null && _incomingPath == null)
                {
                    if (!fresh && !_paths.Contains(_incomingEndpoints))
                    {
                        position += length;
                        continue;
                    }

                    _incomingPath = _paths.Receive(
                        _incomingEndpoints,
                        destination,
                        datagram.Length,
                        _handshakeDone && Phase == ConnectionPhase.Active,
                        _now
                    );

                    if (_incomingPath == null)
                    {
                        position += length;
                        continue;
                    }
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

                _nextReceived[level] = Math.Max(_nextReceived[level], number + 1);

                if (level == 1)
                {
                    _addressValidated = true;
                    _paths?.ValidateInitial();
                    _recovery.Discard(0);
                }

                if (fresh)
                {
                    var acknowledge = Frames(
                        level,
                        plaintext,
                        number >= _keyUpdates.ReadMinimum,
                        destination,
                        out var nonProbing
                    );

                    if (level == 2 && _paths != null && _incomingPath != null)
                        _paths.Observe(_incomingPath, number, nonProbing, _now, _recovery.KeyRetention / 3);

                    if (acknowledge && Phase == ConnectionPhase.Active)
                    {
                        if (level == 2 && _limits.SharePackets) AcknowledgeLater(number, largestBefore);

                        else AcknowledgeReceived(level);
                    }

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
                            canQueue: count => _deferred.Count == 0
                                && _pending.Count + _urgent.Count + (count + 899) / 900 + 1 <= 480,
                            close: code => BeginClose(2, Wire.Frame(0x1d, code, 0)),
                            inboundBidirectionalStreams: _limits.InboundBidirectionalStreams,
                            inboundUnidirectionalStreams: _limits.InboundUnidirectionalStreams,
                            deferCredit: _limits.SharePackets
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
            if (_paths != null && _incomingEndpoints != null)
            {
                var associated = _paths.Snapshot.FirstOrDefault(
                    route => QuicPathValidation.Same(route.Endpoints, _incomingEndpoints)
                );

                if (associated == null || _paths.Find(associated.Id)?.Sent == 0) return false;
            }

            if (_peerIds?.MatchesReset(datagram) != true) return false;

            StatelessResetReceived = true;
            _lifetime.Drain(_now, _recovery.KeyRetention);
            _streams?.Protocol.ConnectionClosed(0, false);
            return true;
        }

        // Key updates keep the header protection key, so the current keys unmask every phase.
        private Span<byte> UnprotectApplication(Span<byte> packet, int offset, out ulong number)
        {
            var current = _tls.ReadKeys[2] ?? throw new InvalidOperationException("Missing application keys.");
            var headerLength = current.Unmask(packet, offset, _nextReceived[2], out number);
            var phase = (packet[0] & 4) != 0;

            if (phase == _keyUpdates.ReadPhase)
            {
                var currentPayload = current.Open(packet, headerLength, number, phase);
                _keyUpdates.ReadCurrent(number);
                return currentPayload;
            }

            if (number < _keyUpdates.ReadMinimum)
            {
                if (_previousRead == null || _now > _previousExpires)
                    throw new CryptographicException("Previous packet keys expired.");

                var previous = _previousRead.Open(packet, headerLength, number, phase);
                _keyUpdates.ReadPrevious(number);
                return previous;
            }

            // Next keys are prepared when TLS installs the application keys and
            // after an authenticated transition, not on a forged phase change.
            var next = _nextRead ?? throw new CryptographicException("Next packet keys unavailable.");
            var plaintext = next.Open(packet, headerLength, number, phase);

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
            _deferred.Clear();
            _closeLevel = level;
            _closePayload = payload;
            _lifetime.Close(_now, _recovery.KeyRetention);
        }

        // Initial and Handshake packets are acknowledged at once. When packets are shared, 1-RTT
        // ones (RFC 9000 sections 13.2.1 and 13.2.2) wait for a second ack-eliciting packet, for
        // a packet of ours to share, or for AcknowledgmentDelay; one out of order is acknowledged
        // at once. The frame reports how long the largest packet waited, in units of 2^3
        // microseconds (the default ack_delay_exponent).
        private void AcknowledgeReceived(int level)
        {
            var delay = 0UL;

            if (level == 2)
            {
                _unacknowledged = 0;
                _acknowledgeBy = double.PositiveInfinity;
                delay = (ulong)Math.Max(0, (_now - _largestReceivedAt) * 1000) >> 3;
            }

            Send(level, _received[level].AckFrame(delay), false, false);
        }

        private void AcknowledgeLater(ulong number, ulong? largestBefore)
        {
            if (number != (largestBefore is { } previous ? previous + 1 : 0) || ++_unacknowledged >= 2)
            {
                AcknowledgeReceived(2);
                return;
            }

            if (double.IsPositiveInfinity(_acknowledgeBy)) _acknowledgeBy = _now + AcknowledgmentDelay;
        }

        // A due acknowledgment leaves with the owner's next timer tick (every 2 ms on the
        // listener's dispatcher), well inside the 25 ms max_ack_delay peers assume of this
        // endpoint, which does not advertise one.
        private const double AcknowledgmentDelay = 1;

        // Set by the owner for a turn that waited long for a worker. The tick that sends a
        // waiting acknowledgment runs in a later turn, which could wait as long and take it
        // past max_ack_delay, so this turn sends it instead.
        internal bool AcknowledgeAtOnce { get; set; }

        private int _unacknowledged;
        private double _acknowledgeBy = double.PositiveInfinity;

        // When the largest 1-RTT packet number so far arrived.
        private double _largestReceivedAt;

        internal void QueueRepeat(Delivery delivery, bool probe)
        {
            if (Phase != ConnectionPhase.Active || delivery.Acknowledged || _recovery.IsDiscarded(delivery.Level))
                return;

            if (delivery.Queued)
            {
                if (probe && !delivery.ProbePending)
                {
                    if (_urgent.Count < 128)
                    {
                        delivery.ProbePending = true;
                        _urgent.Enqueue(delivery);
                    }
                }

                return;
            }

            if ((probe || !delivery.Eliciting) && _urgent.Count < 128)
            {
                delivery.Queued = true;
                delivery.ProbePending = probe;
                _urgent.Enqueue(delivery);
                return;
            }

            var defer = _deferred.Count != 0 || _pending.Count + _urgent.Count >= MaximumReadyDeliveries;

            if (defer && _deferred.Count >= MaximumDeferredDeliveries)
                throw new InvalidDataException("Deferred outbound queue budget exceeded.");

            delivery.Queued = true;
            delivery.ProbePending = probe;

            (defer ? _deferred : _pending).Enqueue(delivery);
        }

        private void PromoteDeferred()
        {
            while (_deferred.Count != 0 && _pending.Count + _urgent.Count < MaximumReadyDeliveries)
            {
                var delivery = _deferred.Dequeue();

                if (!delivery.Queued || delivery.Acknowledged || _recovery.IsDiscarded(delivery.Level))
                {
                    delivery.Queued = false;
                    delivery.ProbePending = false;
                    continue;
                }

                var urgent = (delivery.ProbePending || !delivery.Eliciting) && _urgent.Count < 128;
                (urgent ? _urgent : _pending).Enqueue(delivery);
            }
        }

        // The frames that share the packet Drain is building; empty between packets.
        private readonly List<Delivery> _joined = [];

        // Moves deliveries from the front of queue into the packet first leads while they fit
        // and match it: same level, no path of their own and no padding. An ordinary eliciting
        // frame also needs the congestion controller's room; the urgent queue's probes and
        // acknowledgments do not, as they would not alone.
        private void Join(
            Queue<Delivery> queue,
            ref int visits,
            Delivery first,
            bool urgent,
            int headerLength,
            int room,
            ref int frameBytes,
            ref bool eliciting,
            List<Delivery> joined
        )
        {
            while (visits != 0 && queue.Count != 0)
            {
                var next = queue.Peek();

                if (!next.Queued || next.Acknowledged)
                {
                    queue.Dequeue();
                    visits--;
                    next.Queued = false;
                    next.ProbePending = false;
                    continue;
                }

                if (
                    next.Level != first.Level
                    || next.Path != null
                    || next.PadToMinimum
                    || next.PreferValidatedPath != first.PreferValidatedPath
                    || frameBytes + next.Bytes.Length > room
                    || (next.Eliciting
                        && !urgent
                        && !_recovery.CanSend(headerLength + 1 + frameBytes + next.Bytes.Length + 16, _now, false))
                )
                    return;

                queue.Dequeue();
                visits--;
                next.Queued = false;
                next.ProbePending = false;
                frameBytes += next.Bytes.Length;
                eliciting |= next.Eliciting;
                joined.Add(next);
            }
        }

        private List<byte[]> Drain()
        {
            var output = new List<byte[]>();
            Span<byte> header = stackalloc byte[MaximumHeader];

            if (_paths?.Tick(_now) == true && _handshakeDone && Phase == ConnectionPhase.Active)
                AcknowledgeReceived(2);

            ObserveValidatedPath();

            if (
                Phase == ConnectionPhase.Active
                && _paths != null
                && _peerIds != null
                && (_retireUnusedPeerIds || !ReferenceEquals(_retirementPaths, _paths.Snapshot))
            )
            {
                try
                {
                    Span<ulong> sequences = stackalloc ulong[QuicPathValidation.Capacity];
                    var count = _paths.CopyPeerSequences(sequences);
                    _peerIds.RetireUnused(sequences[..count]);
                    _retirementPaths = _paths.Snapshot;
                    _retireUnusedPeerIds = false;
                }

                catch (TransportException error)
                {
                    BeginClose(CloseLevel, Wire.Frame(0x1c, error.ErrorCode, 0, 0));
                }

                catch (InvalidDataException)
                {
                    BeginClose(CloseLevel, Wire.Frame(0x1c, 1, 0, 0));
                }
            }

            if (Phase != ConnectionPhase.Active)
            {
                var fallback = _paths?.Fallback;
                var closingPath = _paths?.Current;

                if (closingPath is { Allowance: < 1200 }) closingPath = fallback;

                _pending.Clear();
                _urgent.Clear();
                _deferred.Clear();
                _recovery.Abandon();
                _streams?.Dispose();

                if (
                    Phase == ConnectionPhase.Closing
                    && _closePayload != null
                    && (closingPath != null
                        ? closingPath.Allowance >= 1200
                        : _paths == null && (_addressValidated || SentBytes + 1200 <= ReceivedBytes * 3))
                    && _lifetime.TakeCloseResponse(_now)
                )
                {
                    var keys = _closeLevel == 0 ? _initialWrite : _tls.WriteKeys[_closeLevel];

                    if (keys == null || !keys.CanEncrypt || _sentNumbers[_closeLevel] > PacketNumberWindow.Maximum)
                    {
                        _lifetime.Drain(_now, _recovery.KeyRetention);
                        return output;
                    }

                    var close = new Delivery(_closeLevel, _closePayload, false, false);
                    var path = closingPath;

                    if (!ResolveDestination(path, out var destination, out var sequence))
                    {
                        if (
                            fallback == null
                            || ReferenceEquals(path, fallback)
                            || fallback.Allowance < 1200
                            || !ResolveDestination(fallback, out destination, out sequence)
                        ) return output;

                        path = fallback;
                    }

                    var packet = Protect(close, path, destination);
                    MarkDestination(sequence);

                    if (path != null) QuicPathValidation.Sent(path, close, packet.Length, _now);

                    _sentNumbers[_closeLevel]++;
                    SentBytes += packet.Length;
                    ClosePackets++;
                    output.Add(packet);
                    _outputPaths?.Add(path?.Route);
                }

                return output;
            }

            PromoteDeferred();
            _streams?.Prune();

            if (Phase != ConnectionPhase.Active) return Drain();

            if (_paths != null)
                foreach (var probe in _paths.Probes(_now)) QueueRepeat(probe, true);

            // A waiting acknowledgment and freed stream credit go with anything else queued.
            if (
                _unacknowledged != 0
                && (_pending.Count + _urgent.Count != 0 || _now >= _acknowledgeBy || AcknowledgeAtOnce)
            )
                AcknowledgeReceived(2);

            _streams?.AdvertiseCredit(_pending.Count + _urgent.Count != 0);

            var urgentVisits = _urgent.Count;
            var pendingVisits = _pending.Count;

            while (urgentVisits + pendingVisits != 0)
            {
                var queue = urgentVisits != 0 ? _urgent : _pending;

                if (urgentVisits != 0) urgentVisits--;

                else pendingVisits--;

                var delivery = queue.Peek();
                var level = delivery.Level;

                if (!delivery.Queued || delivery.Acknowledged || _recovery.IsDiscarded(level))
                {
                    queue.Dequeue();
                    delivery.Queued = false;
                    delivery.ProbePending = false;
                    continue;
                }

                if (!PrepareKeys(level, delivery.Eliciting))
                {
                    output.AddRange(Drain());
                    return output;
                }

                var path = delivery.Path == null
                    ? delivery.PreferValidatedPath ? _paths?.Fallback : _paths?.Current
                    : _paths?.Find(delivery.Path.Id);

                if (delivery.Path != null && path == null)
                {
                    queue.Dequeue();
                    delivery.Queued = false;
                    delivery.ProbePending = false;
                    continue;
                }

                if (!ResolveDestination(path, out var destination, out var sequence))
                {
                    if (delivery.Path == null && (_paths?.Count ?? 0) <= 1) break;

                    queue.Enqueue(queue.Dequeue());
                    continue;
                }

                var headerLength = Prepare(
                    level,
                    delivery.Bytes.Length,
                    delivery.PadToMinimum,
                    path,
                    destination,
                    header,
                    out var numberOffset,
                    out var payloadLength
                );
                var size = headerLength + payloadLength + 16;
                var allowance = path?.Allowance ?? (_addressValidated ? long.MaxValue : ReceivedBytes * 3 - SentBytes);

                if (size > allowance || (delivery.Eliciting && !_recovery.CanSend(size, _now, queue == _urgent)))
                {
                    if (path == null || (delivery.Path == null && queue == _pending && (_paths?.Count ?? 0) <= 1))
                        break;

                    queue.Enqueue(queue.Dequeue());
                    continue;
                }

                queue.Dequeue();
                delivery.Queued = false;
                delivery.ProbePending = false;
                var joined = _joined;
                var eliciting = delivery.Eliciting;

                // Frames queued behind this one share its packet while they fit. A long header's
                // length field may grow by one byte as the payload does.
                if (_limits.SharePackets && delivery.Path == null && !delivery.PadToMinimum)
                {
                    var frameBytes = delivery.Bytes.Length;
                    var room = (int)Math.Min(Recovery.MaximumPacket, allowance)
                        - headerLength
                        - (level < 2 ? 1 : 0)
                        - 16;
                    Join(
                        _urgent,
                        ref urgentVisits,
                        delivery,
                        true,
                        headerLength,
                        room,
                        ref frameBytes,
                        ref eliciting,
                        joined
                    );
                    Join(
                        _pending,
                        ref pendingVisits,
                        delivery,
                        false,
                        headerLength,
                        room,
                        ref frameBytes,
                        ref eliciting,
                        joined
                    );

                    if (joined.Count != 0)
                    {
                        if (eliciting && !delivery.Eliciting && !PrepareKeys(level, true))
                        {
                            joined.Clear();
                            output.AddRange(Drain());
                            return output;
                        }

                        headerLength = Prepare(
                            level,
                            frameBytes,
                            false,
                            path,
                            destination,
                            header,
                            out numberOffset,
                            out payloadLength
                        );
                    }
                }

                var keys = (level == 0 ? _initialWrite : _tls.WriteKeys[level])
                    ?? throw new InvalidOperationException("Packet keys unavailable.");
                var packet = Seal(keys, level, header[..headerLength], numberOffset, payloadLength, delivery, joined);
                MarkDestination(sequence);
                _recovery.SentPacket(
                    level,
                    _sentNumbers[level]++,
                    packet.Length,
                    _now,
                    delivery,
                    level == 0,
                    _pending.Count == 0,
                    joined.Count == 0 ? null : [.. joined]
                );
                joined.Clear();
                SentBytes += packet.Length;

                if (path != null) QuicPathValidation.Sent(path, delivery, packet.Length, _now);

                if (eliciting) _lifetime.SentEliciting(_now);

                output.Add(packet);
                _outputPaths?.Add(path?.Route);
            }

            PromoteDeferred();
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

        // Frame types RFC 9000 defines (0x00 to 0x1e), RESET_STREAM_AT (0x24) and DATAGRAM (0x30
        // and 0x31). Any other type is a FRAME_ENCODING_ERROR in a packet of any level.
        private static bool KnownFrameType(ulong type) => type <= 0x1e || type is 0x24 or 0x30 or 0x31;

        private bool Frames(
            int level,
            ReadOnlySpan<byte> plaintext,
            bool currentReadKeys,
            ReadOnlySpan<byte> destination,
            out bool nonProbing
        )
        {
            // RFC 9000 section 12.4: a packet carries at least one frame. A frame that ends early or
            // runs past its packet is badly formatted, a FRAME_ENCODING_ERROR (section 20.1).
            if (plaintext.IsEmpty) throw new TransportException(0x0a, "Packet without frames.");

            nonProbing = false;
            var reader = new WireReader(plaintext, 0x07);
            var acknowledge = false;

            while (reader.Remaining != 0)
            {
                var start = reader.Remaining;
                var type = reader.Integer();

                if (type == 0) continue;

                if (!KnownFrameType(type))
                    throw new TransportException(
                        0x07,
                        "Unknown QUIC frame type "
                            + type.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "."
                    );

                if (type is not (0x1a or 0x1b or 0x18)) nonProbing = true;

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
                        bytes,
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
                    _streams.Reset(id, error, final, reliable);
                }

                else if (_streams != null && type == 5) _streams.Stop(reader.Integer(), reader.Integer());

                else if (_streams != null && type is 0x30 or 0x31)
                {
                    var length = type == 0x31 ? reader.Integer() : (ulong)reader.Remaining;

                    // RFC 9221 section 3: a frame larger than the max_datagram_frame_size the server
                    // advertised, which counts the type and length fields, is a PROTOCOL_VIOLATION.
                    if ((ulong)(start - reader.Remaining) + length > MaximumDatagramFrame)
                        throw new TransportException(0x0a, "DATAGRAM frame above the advertised size.");

                    _streams.Datagram(
                        reader
                            .Take((int)length)
                            .ToArray()
                    );
                }

                else if (type == 0x14) reader.Integer();

                // RFC 9000 section 19.14: a stream count above 2^60 cannot be encoded as a stream ID.
                else if (type is 0x16 or 0x17)
                {
                    if (reader.Integer() > StreamCountWindow.MaximumCount)
                        throw new TransportException(0x07, "Stream count above 2^60.");
                }

                else if (type == 0x15)
                {
                    var id = reader.Integer();
                    reader.Integer();
                    _streams?.DataBlocked(id);
                }

                else if (type == 0x18)
                {
                    ReadPeerConnectionId(ref reader);
                }

                else if (type == 0x19) RetireLocalConnectionId(ref reader, destination);

                else if (type is 0x1a or 0x1b) ReadPathControl(ref reader, type);

                // NEW_TOKEN and HANDSHAKE_DONE, which a server must not receive (RFC 9000 sections
                // 19.7 and 19.20).
                else
                    throw new TransportException(
                        0x0a,
                        "A client cannot send QUIC frame type "
                            + type.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + "."
                    );
            }

            return acknowledge;
        }

        private void ReadPathControl(ref WireReader reader, ulong type)
        {
            var data = reader.Take(8);

            if (type == 0x1b)
            {
                if (_paths == null) throw new TransportException(10, "Unmatched path response.");

                _paths.Respond(data, _now);
                ObserveValidatedPath();
                return;
            }

            var response = new byte[9];
            response[0] = 0x1b;
            data.CopyTo(response.AsSpan(1));
            // RFC 9000 8.2.2/13.3: exactly one response per received challenge;
            // recovery may elicit fresh traffic, but must not repeat this frame.
            QueueRepeat(
                new Delivery(2, response, false)
                {
                    PadToMinimum = true,
                    Path = _incomingPath?.Route
                },
                true
            );
        }

        private void RetireLocalConnectionId(ref WireReader reader, ReadOnlySpan<byte> destination)
        {
            var sequence = reader.Integer();
            _localIds.Retire(sequence, destination);
        }

        private void ReadPeerConnectionId(ref WireReader reader)
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
                _retireUnusedPeerIds = true;
            }

            finally
            {
                // A valid increased watermark also applies if a subsequent
                // retirement budget closes the connection. Never close on an old ID.
                if (!_peerIds.Current.SequenceEqual(_destinationId)) _destinationId = _peerIds.Current.ToArray();
            }
        }

        private Delivery RetirePeerConnectionId(ulong sequence)
        {
            var delivery = new Delivery(2, Wire.Frame(0x19, sequence)) { PreferValidatedPath = true };
            QueueRepeat(delivery, false);
            return delivery;
        }

        private byte[] LocalParameters()
        {
            using var output = new MemoryStream();
            // RFC 9000 7.3: after a Retry, the first Initial's destination and the Retry's source.
            Wire.Parameter(output, 0, _retry?.OriginalDestinationId ?? _originalId);

            if (_retry != null) Wire.Parameter(output, 16, _originalId);

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

            Wire.Parameter(output, 0x20, MaximumDatagramFrame);
            Wire.Parameter(output, ReliableResetParameter, []);
            Wire.Parameter(output, LegacyReliableResetParameter, []);

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

        // reset_stream_at (draft-ietf-quic-reliable-stream-reset-08 and later) and its earlier
        // codepoint (drafts 07 and before), which WebTransport draft-14 cites and Safari 27 sends.
        // The RESET_STREAM_AT frame is the same in both, so either enables it.
        private const ulong ReliableResetParameter = 0x1d;
        private const ulong LegacyReliableResetParameter = 0x17f7586d2cb571;

        // The max_datagram_frame_size the server advertises (RFC 9221 section 3).
        private const ulong MaximumDatagramFrame = 1200;

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

                else if (id is ReliableResetParameter or LegacyReliableResetParameter)
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

                    // RFC 9000 section 18.2 sets no upper bound on max_udp_payload_size.
                    if (
                        (id == 3 && value < 1200)
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

        private bool ResolveDestination(QuicPath? path, out ReadOnlyMemory<byte> destination, out ulong? sequence)
        {
            sequence = null;
            destination = _destinationId;

            if (_peerIds?.ActiveCount == 0) return false;

            if (path == null) return true;

            if (_peerIds == null) return !path.RequireUnusedId;

            if (path.PeerSequence is not { } preferred || !_peerIds.IsActive(preferred))
                path.PeerSequence = _paths?.SharedPeerSequence(path, _peerIds);

            if (!_peerIds.Resolve(path.PeerSequence, out var selected, out destination))
                return false;

            if (path.PeerSequence != selected) _retireUnusedPeerIds = true;

            sequence = path.PeerSequence = selected;
            return true;
        }

        private void MarkDestination(ulong? sequence)
        {
            if (sequence.HasValue) _peerIds?.MarkUsed(sequence.Value);

            else _peerIds?.MarkUsed();
        }

        private byte[] Protect(Delivery delivery, QuicPath? path = null, ReadOnlyMemory<byte>? destination = null)
        {
            Span<byte> header = stackalloc byte[MaximumHeader];
            var length = Prepare(
                delivery.Level,
                delivery.Bytes.Length,
                delivery.PadToMinimum,
                path,
                destination,
                header,
                out var numberOffset,
                out var payloadLength
            );
            var keys = (delivery.Level == 0 ? _initialWrite : _tls.WriteKeys[delivery.Level])
                ?? throw new InvalidOperationException("Packet keys are unavailable.");
            return Seal(keys, delivery.Level, header[..length], numberOffset, payloadLength, delivery, null);
        }

        // A long header with two 20-byte IDs, an empty token and a four-byte length and
        // packet number is 56 bytes.
        private const int MaximumHeader = 64;

        // Writes the header of a packet whose frames take frameBytes into header and returns
        // its length. payloadLength is frameBytes plus the padding the packet needs.
        private int Prepare(
            int level,
            int frameBytes,
            bool padToMinimum,
            QuicPath? path,
            ReadOnlyMemory<byte>? destination,
            Span<byte> header,
            out int numberOffset,
            out int payloadLength
        )
        {
            var target = destination ?? _destinationId;
            var length = 0;
            payloadLength = frameBytes;

            if (level < 2)
            {
                header[length++] = level == 0 ? (byte)0xc3 : (byte)0xe3;
                BinaryPrimitives.WriteUInt32BigEndian(header[length..], 1);
                length += 4;
                header[length++] = (byte)target.Length;
                target.Span.CopyTo(header[length..]);
                length += target.Length;
                var source = _localIds.Current;
                header[length++] = (byte)source.Length;
                source.CopyTo(header[length..]);
                length += source.Length;

                if (level == 0)
                {
                    header[length++] = 0;
                    // Initial length always uses two bytes at the required 1200-byte size.
                    payloadLength = Math.Max(payloadLength, 1200 - length - 2 - 4 - 16);
                }

                length += Wire.Integer(header[length..], (ulong)(4 + payloadLength + 16));
            }

            else
            {
                header[length++] = _keyUpdates.WritePhase ? (byte)0x47 : (byte)0x43;
                target.Span.CopyTo(header[length..]);
                length += target.Length;
            }

            numberOffset = length;
            BinaryPrimitives.WriteUInt32BigEndian(header[length..], unchecked((uint)_sentNumbers[level]));
            length += 4;

            if (padToMinimum)
            {
                var minimum = path != null
                    ? (int)Math.Min(1200, path.Allowance)
                    : _addressValidated ? 1200 : (int)Math.Min(1200, Math.Max(0, ReceivedBytes * 3 - SentBytes));
                payloadLength = Math.Max(payloadLength, minimum - length - 16);
            }

            return length;
        }

        // The packet holds the header, first's frame and then the joined ones, zero PADDING up
        // to payloadLength, and the tag; it is encrypted where it was assembled.
        private byte[] Seal(
            PacketKeys keys,
            int level,
            ReadOnlySpan<byte> header,
            int numberOffset,
            int payloadLength,
            Delivery first,
            List<Delivery>? joined
        )
        {
            var packet = new byte[header.Length + payloadLength + 16];
            header.CopyTo(packet);
            var offset = header.Length;
            first.Bytes.CopyTo(packet, offset);
            offset += first.Bytes.Length;

            if (joined != null)
                foreach (var delivery in joined)
                {
                    delivery.Bytes.CopyTo(packet, offset);
                    offset += delivery.Bytes.Length;
                }

            keys.Seal(packet, header.Length, numberOffset, _sentNumbers[level]);
            return packet;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _executionClock = null;
            _pending.Clear();
            _urgent.Clear();
            _deferred.Clear();
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
