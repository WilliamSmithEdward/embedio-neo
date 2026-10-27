using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Quic
{
    internal sealed record OutboundDatagram(IPEndPoint Peer, byte[] Bytes);

    internal delegate QuicConnection QuicConnectionFactory(ReadOnlySpan<byte> firstPacket, double now);

    internal delegate QuicConnection ScheduledQuicConnectionFactory(
        ReadOnlySpan<byte> firstPacket,
        double now,
        QuicConnectionOwner owner,
        QuicConnectionEndpoints endpoints
    );

    // Receive, Tick and SendOneAsync are driven by one socket-loop owner. Native
    // work runs on the execution pool; workers publish immutable state and owned
    // packet arrays without touching the routing dictionaries.
    internal sealed class QuicConnectionDispatcher : IAsyncDisposable
    {
        private sealed record Status(
            ReadOnlyMemory<byte> Id,
            bool Established,
            bool Closed,
            bool Expired,
            ReadOnlyMemory<byte> ResetToken,
            string? ResetIdentity,
            IReadOnlyList<ReadOnlyMemory<byte>> IssuedIds,
            IReadOnlyList<QuicPathRoute> Paths,
            IReadOnlyList<ReadOnlyMemory<byte>> Tokens,
            string[] TokenIdentities
        );

        // A struct, so queueing a datagram allocates nothing beyond the datagram.
        private readonly record struct Output(
            Entry? Entry,
            byte[] Bytes,
            IPEndPoint? Peer = null,
            ulong? PathId = null,
            bool Refusal = false
        );

        private sealed class PeerGroup
        {
            internal int Count;
        }

        private sealed class Entry
        {
            internal Entry(
                QuicConnectionDispatcher dispatcher,
                ScheduledQuicConnectionFactory create,
                string original,
                IPEndPoint peer,
                IPEndPoint? local,
                double deadline,
                QuicRetry? retry
            )
            {
                ArgumentNullException.ThrowIfNull(dispatcher);
                ArgumentNullException.ThrowIfNull(create);
                Original = original;
                Peer = QuicConnectionEndpoints.Copy(peer);
                Local = local == null ? null : QuicConnectionEndpoints.Copy(local);
                Deadline = deadline;
                Endpoints = new QuicConnectionEndpoints(Peer, Local);
                Owner = new QuicConnectionOwner(
                    dispatcher._pool,
                    (packet, now) =>
                    {
                        try
                        {
                            return create(
                                packet,
                                now,
                                Owner ?? throw new InvalidOperationException("Connection owner was not initialized."),
                                new QuicConnectionEndpoints(Peer, Local)
                            );
                        }

                        finally
                        {
                            FinishAdmission(dispatcher);
                        }
                    },
                    () => dispatcher.ConnectionClock(this),
                    output => dispatcher.Publish(this, output),
                    connectionIds: dispatcher._connectionIds,
                    retry: retry,
                    completed: () => Interlocked.Increment(ref dispatcher._uncollected)
                );
            }

            // The entry counts in the dispatcher's Admitting from its admission until its first
            // turn has created the connection, or until it retires without one.
            private int _uncreated = 1;

            internal void FinishAdmission(QuicConnectionDispatcher dispatcher)
            {
                if (Interlocked.Exchange(ref _uncreated, 0) == 1) Interlocked.Decrement(ref dispatcher._admitting);
            }

            internal string Original { get; }
            internal IPEndPoint Peer { get; }
            internal IPEndPoint? Local { get; }
            internal QuicConnectionEndpoints Endpoints { get; }
            internal double Deadline { get; }
            internal QuicConnectionOwner Owner { get; }

            internal PeerGroup Group = new();
            internal Status? State;
            internal string? Id;
            internal string[] Aliases = [];
            internal IReadOnlyList<ReadOnlyMemory<byte>>? IssuedIds;
            internal IReadOnlyList<QuicPathRoute>? Paths;
            internal IReadOnlyList<ReadOnlyMemory<byte>>? Tokens;
            internal (IPEndPoint Peer, IPEndPoint? Local, string Token)[] ResetBindings = [];
            internal readonly HashSet<(IPEndPoint Peer, IPEndPoint? Local)> Groups = [];
            internal bool Closing;
            internal bool Expired;
            internal TimeoutException? ExpiryFailure;
            internal int Packets;
            internal int Bytes;
        }

        private readonly QuicExecutionPool _pool;
        private readonly ScheduledQuicConnectionFactory _create;
        private readonly Func<double> _clock;
        private readonly int _capacity;
        private readonly int _attemptLimit;
        private readonly double _handshakeTimeout;
        private readonly bool _refuse;
        private readonly double _queueDelayBound;
        private readonly RetryTokens? _tokens;
        private readonly int _outputPacketCapacity;
        private readonly int _outputByteCapacity;
        private readonly Channel<Output> _output;
        private readonly object _outputGate = new();
        private readonly HashSet<Entry> _entries = [];
        private readonly List<Entry> _collected = [];

        // Owners that are completing or have completed and are not yet collected. Each is
        // counted before its Completion finishes, so Collect scans only while one may be found.
        private int _uncollected;
        private readonly Dictionary<string, Entry> _issued = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Entry>.AlternateLookup<ReadOnlySpan<char>> _issuedLookup;
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local, string Id), Entry> _initials = [];
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local, string Token), Entry> _resets = [];
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local), PeerGroup> _peerGroups = [];
        private readonly byte[] _resetKey = RandomNumberGenerator.GetBytes(32);
        private readonly QuicConnectionIdIssuer _connectionIds = new();
        private int _pendingResets;
        private int _resetAttempts;
        private double _resetWindow;
        private int _pendingRefusals;
        private int _admitting;
        private volatile Func<bool>? _saturated;
        private int _refusalAttempts;
        private double _refusalWindow;
        private int _retryAttempts;
        private double _retryWindow;
        private Task? _shutdown;
        private TaskCompletionSource? _sendFinished;
        private bool _sending;
        private Output? _taken;
        private readonly Action? _outputReady;
        private volatile bool _closing;
        private int _pendingPackets;
        private int _pendingBytes;
        private int _attempts;
        private double _window;
        private double _now;

        internal QuicConnectionDispatcher(
            ScheduledQuicConnectionFactory create,
            Func<double> clock,
            int capacity = 16,
            int workers = 2,
            double handshakeTimeout = 3000,
            int outputPacketCapacity = 256,
            int outputByteCapacity = 1048576,
            int? admissionAttemptsPerSecond = null,
            bool refuseConnections = false,
            double queueDelayBound = 0,
            bool validateAddresses = false,
            Action? outputReady = null
        )
        {
            ArgumentNullException.ThrowIfNull(create);
            ArgumentNullException.ThrowIfNull(clock);
            _outputReady = outputReady;
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);
            ArgumentOutOfRangeException.ThrowIfLessThan(outputPacketCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(outputPacketCapacity, 65536);
            ArgumentOutOfRangeException.ThrowIfLessThan(outputByteCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(outputByteCapacity, 16777216);

            if (!double.IsFinite(handshakeTimeout) || handshakeTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

            if (!double.IsFinite(queueDelayBound) || queueDelayBound < 0 || (queueDelayBound > 0 && !refuseConnections))
                throw new ArgumentOutOfRangeException(nameof(queueDelayBound));

            _create = create;
            _clock = clock;
            _issuedLookup = _issued.GetAlternateLookup<ReadOnlySpan<char>>();
            _capacity = capacity;
            _attemptLimit = admissionAttemptsPerSecond ?? capacity;
            ArgumentOutOfRangeException.ThrowIfNegative(_attemptLimit);
            _handshakeTimeout = handshakeTimeout;
            _refuse = refuseConnections;
            _queueDelayBound = queueDelayBound;
            _tokens = validateAddresses ? new RetryTokens() : null;
            _outputPacketCapacity = outputPacketCapacity;
            _outputByteCapacity = outputByteCapacity;
            _output = Channel.CreateBounded<Output>(new BoundedChannelOptions(outputPacketCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _pool = new QuicExecutionPool(workers, capacity);
        }

        internal bool Accepting { get; set; } = true;
        internal int Active => _entries.Count;
        internal int ResetRoutes => _resets.Count;
        internal int IssuedRoutes => _issued.Count;
        internal int EndpointGroups => _peerGroups.Count;
        internal int Accepted { get; private set; }
        internal int Retired { get; private set; }
        internal int Peak { get; private set; }
        internal int Rejected { get; private set; }

        // New connections answered with CONNECTION_REFUSED.
        internal int Refused { get; private set; }

        // Why new connections were refused, whether or not the answer was sent: the endpoint
        // was full, queued work waited past the bound, or the application layer had no room.
        internal int RefusedAtCapacity { get; private set; }
        internal int RefusedForQueueDelay { get; private set; }
        internal int RefusedForApplication { get; private set; }

        // Connections admitted whose first turn has not created them yet. The application
        // layer is handed a connection only once it exists, so it cannot count these itself.
        internal int Admitting => Volatile.Read(ref _admitting);

        // Set by the application layer: true while it has no room for another connection, so
        // a new one is refused before any connection state or TLS work starts.
        internal Func<bool>? Saturated
        {
            get => _saturated;
            set => _saturated = value;
        }

        // Refusals not sent because their per-second budget or the output queue was full.
        internal int UnsentRefusals { get; private set; }
        internal TimeSpan QueueDelay => _pool.QueueDelay;

        // Retry packets sent, and Initials closed because their token failed validation.
        internal int RetriesSent { get; private set; }
        internal int InvalidTokens { get; private set; }
        private bool Overloaded => _queueDelayBound > 0 && _pool.QueueDelay.TotalMilliseconds > _queueDelayBound;
        internal int ExpiredHandshakes { get; private set; }
        internal int FailedOwners { get; private set; }
        internal Exception? LastOwnerFailure { get; private set; }
        internal Exception? LastRejectedFailure { get; private set; }
        internal int DroppedOutput { get; private set; }

        internal int PendingOutputBytes
        {
            get
            {
                lock (_outputGate) return _pendingBytes;
            }
        }

        internal int PendingOutputPackets
        {
            get
            {
                lock (_outputGate) return _pendingPackets;
            }
        }

        internal bool Receive(ReadOnlySpan<byte> bytes, IPEndPoint peer, IPEndPoint? local = null)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            ArgumentNullException.ThrowIfNull(peer);
            var now = ReadClock();
            Collect();

            if (!PacketHeader.TryDestination(bytes, out var destination, out var initial))
                return RouteReset(bytes, peer, local, now) || Reject();

            // An issued ID is found without a string; a connection still on its client's
            // Initial ID needs one. IDs are at most 20 bytes.
            Span<char> hex = stackalloc char[40];
            Convert.TryToHexString(destination, hex, out var written);
            hex = hex[..written];
            string? id = null;

            if (!_issuedLookup.TryGetValue(hex, out var entry))
            {
                id = new string(hex);
                _initials.TryGetValue((peer, local, id), out entry);
            }

            QuicConnectionOwner? resetOwner = null;

            if (entry != null)
            {
                if (!Synchronize(entry, now)) return RouteReset(bytes, peer, local, now) || Reject();

                if (
                    (!entry.Peer.Equals(peer) && (entry.State?.Established != true || (bytes[0] & 128) != 0))
                    || !Equals(entry.Local, local)
                )
                    return RouteReset(bytes, peer, local, now) || Reject();

                // Preserve authenticated-packet priority when a randomized reset
                // header names a sibling. Only shared endpoint groups need this
                // candidate lookup; forwarding follows failed authentication.
                if (_peerGroups.TryGetValue((peer, local), out var peers) && peers.Count > 1)
                {
                    var candidate = FindReset(bytes, peer, local, now);

                    if (candidate != null && !ReferenceEquals(candidate, entry)) resetOwner = candidate.Owner;
                }
            }

            else
            {
                id ??= new string(hex);

                if (RouteReset(bytes, peer, local, now)) return true;

                if (!initial) return SendReset(bytes, peer, now) || Reject();

                if (!Accepting || !initial || bytes.Length is < 1200 or > 65535) return Reject();

                // Past capacity, while queued work waits longer than the bound, or while the
                // application layer has no room, no connection state or TLS work starts for a
                // new Initial.
                if (Refuses())
                {
                    if (_refuse) SendRefusal(bytes, peer, now);

                    return Reject();
                }

                QuicRetry? retry = null;

                // RFC 9000 8.1.2: an Initial without a token, or with one this listener did not
                // seal, gets a Retry and no state. Its own Retry token that fails validation gets
                // CONNECTION_CLOSE(INVALID_TOKEN) instead (8.1.3), since the client will not accept
                // a second Retry. A redeemed token names the first Initial's destination and
                // proves the client's address.
                if (_tokens != null)
                {
                    if (!PacketHeader.TryInitialToken(bytes, out var source, out var token)) return Reject();

                    byte[]? original = null;
                    var outcome = token.IsEmpty
                        ? RetryTokens.Outcome.Unknown
                        : _tokens.Redeem(token, peer.Address, destination, now, out original);

                    if (outcome == RetryTokens.Outcome.Unknown)
                    {
                        SendRetry(destination, source, peer, now);
                        return Reject();
                    }

                    if (outcome != RetryTokens.Outcome.Redeemed || original == null)
                    {
                        InvalidTokens++;
                        SendRefusal(bytes, peer, now, ConnectionRefusal.InvalidToken);
                        return Reject();
                    }

                    retry = new QuicRetry(original);
                }

                if (_attemptLimit != 0)
                {
                    if (now >= _window + 1000)
                    {
                        _window = now;
                        _attempts = 0;
                    }

                    if (_attempts >= _attemptLimit) return Reject();

                    _attempts++;
                }

                entry = new Entry(this, _create, id, peer, local, now + _handshakeTimeout, retry);
                var groupKey = (entry.Peer, entry.Local);

                if (_peerGroups.TryGetValue(groupKey, out var group)) entry.Group = group;

                else _peerGroups.Add(groupKey, entry.Group);

                entry.Group.Count++;
                entry.Groups.Add(groupKey);
                _entries.Add(entry);
                Interlocked.Increment(ref _admitting);
                _initials.Add((entry.Peer, entry.Local, id), entry);
                Peak = Math.Max(Peak, _entries.Count);
            }

            if (entry.Owner.Receive(bytes, resetOwner, Endpoints(entry, peer, local))) return true;

            return Reject();
        }

        // RFC 9000 10.3.1 permits keyed transforms instead of raw token comparison.
        // Unknown destinations and shared endpoints pay this cost; workers publish immutable token
        // snapshots and the socket owner alone maintains this bounded routing index.
        private string ResetIdentity(ReadOnlySpan<byte> token) =>
            Convert.ToHexString(HMACSHA256.HashData(_resetKey, token));

        private Entry? FindReset(ReadOnlySpan<byte> bytes, IPEndPoint peer, IPEndPoint? local, double now)
        {
            if (bytes.Length is < 21 or > 65535 || _resets.Count == 0) return null;

            var key = (peer, local, ResetIdentity(bytes[^16..]));
            return _resets.TryGetValue(key, out var entry) && Synchronize(entry, now) ? entry : null;
        }

        private static QuicConnectionEndpoints Endpoints(Entry entry, IPEndPoint peer, IPEndPoint? local)
        {
            if (entry.Peer.Equals(peer) && Equals(entry.Local, local)) return entry.Endpoints;

            var paths = Volatile.Read(ref entry.State)?.Paths;

            if (paths != null)
                foreach (var path in paths)
                    if (path.Endpoints.Peer.Equals(peer) && Equals(path.Endpoints.Local, local)) return path.Endpoints;

            return new QuicConnectionEndpoints(peer, local);
        }

        private bool RouteReset(ReadOnlySpan<byte> bytes, IPEndPoint peer, IPEndPoint? local, double now)
        {
            var entry = FindReset(bytes, peer, local, now);
            return entry?.Owner.Receive(bytes, endpoints: Endpoints(entry, peer, local)) == true;
        }

        private bool SendReset(ReadOnlySpan<byte> bytes, IPEndPoint peer, double now)
        {
            if (!QuicConnectionIdIssuer.CanReset(bytes)) return false;

            if (now - _resetWindow >= 1000)
            {
                _resetWindow = now;
                _resetAttempts = 0;
            }

            if (_resetAttempts >= 64) return false;

            _resetAttempts++;

            lock (_outputGate)
            {
                if (_closing) return false;

                if (
                    _pendingResets >= Math.Min(16, Math.Max(1, _outputPacketCapacity / 16))
                    || _pendingPackets >= _outputPacketCapacity
                    || Math.Min(43, bytes.Length - 1) > _outputByteCapacity - _pendingBytes
                )
                {
                    DroppedOutput++;
                    return false;
                }

                var reset = _connectionIds.Reset(bytes)
                    ?? throw new InvalidOperationException("Invalid reset candidate.");
                var output = new Output(null, reset, QuicConnectionEndpoints.Copy(peer));

                if (!_output.Writer.TryWrite(output))
                    throw new InvalidOperationException("QUIC reset queue lost capacity.");

                _pendingPackets++;
                _pendingBytes += reset.Length;
                _pendingResets++;
            }

            _outputReady?.Invoke();
            return true;
        }

        // RFC 9000 5.2.2: a server that refuses a connection answers its Initial with
        // CONNECTION_CLOSE(CONNECTION_REFUSED), so the client fails at once instead of
        // retransmitting until its handshake times out. A per-second budget bounds the
        // key derivation and sealing this costs the receive loop under a flood, and the
        // reply is no larger than the Initial it answers.
        private void SendRefusal(
            ReadOnlySpan<byte> bytes,
            IPEndPoint peer,
            double now,
            ulong error = ConnectionRefusal.ConnectionRefused
        )
        {
            if (now - _refusalWindow >= 1000)
            {
                _refusalWindow = now;
                _refusalAttempts = 0;
            }

            if (_refusalAttempts >= RefusalsPerSecond)
            {
                UnsentRefusals++;
                return;
            }

            var refusal = ConnectionRefusal.Create(bytes, error);

            if (refusal == null) return;

            _refusalAttempts++;

            lock (_outputGate)
            {
                if (_closing) return;

                if (
                    _pendingRefusals >= RefusalQueueLimit
                    || _pendingPackets >= _outputPacketCapacity
                    || refusal.Length > _outputByteCapacity - _pendingBytes
                )
                {
                    UnsentRefusals++;
                    DroppedOutput++;
                    return;
                }

                if (
                    !_output.Writer.TryWrite(
                        new Output(null, refusal, QuicConnectionEndpoints.Copy(peer), Refusal: true)
                    )
                )
                    throw new InvalidOperationException("QUIC refusal queue lost capacity.");

                _pendingPackets++;
                _pendingBytes += refusal.Length;
                _pendingRefusals++;
                Refused++;
            }

            _outputReady?.Invoke();
        }

        // Refusals queue behind established connections' packets; an eighth of the output
        // queue, at most 512, holds them so that a burst is answered rather than dropped.
        private int RefusalQueueLimit => Math.Min(512, Math.Max(1, _outputPacketCapacity / 8));

        private const int RefusalsPerSecond = 4096;

        // While addresses are validated every new connection costs one Retry. Retries share the
        // refusals' queue limit; past the budget the Initial is dropped and the client
        // retransmits it.
        private void SendRetry(ReadOnlySpan<byte> original, ReadOnlySpan<byte> source, IPEndPoint peer, double now)
        {
            var tokens = _tokens ?? throw new InvalidOperationException("Address validation is off.");

            if (now - _retryWindow >= 1000)
            {
                _retryWindow = now;
                _retryAttempts = 0;
            }

            if (_retryAttempts >= RetriesPerSecond) return;

            _retryAttempts++;
            Span<byte> retryId = stackalloc byte[QuicConnectionIdIssuer.Length];
            RandomNumberGenerator.Fill(retryId);
            var packet = RetryPacket.Create(
                source,
                retryId,
                tokens.Issue(original, retryId, peer.Address, now),
                original
            );

            lock (_outputGate)
            {
                if (_closing) return;

                if (
                    _pendingRefusals >= RefusalQueueLimit
                    || _pendingPackets >= _outputPacketCapacity
                    || packet.Length > _outputByteCapacity - _pendingBytes
                )
                {
                    DroppedOutput++;
                    return;
                }

                if (
                    !_output.Writer.TryWrite(
                        new Output(null, packet, QuicConnectionEndpoints.Copy(peer), Refusal: true)
                    )
                )
                    throw new InvalidOperationException("QUIC retry queue lost capacity.");

                _pendingPackets++;
                _pendingBytes += packet.Length;
                _pendingRefusals++;
                RetriesSent++;
            }

            _outputReady?.Invoke();
        }

        private const int RetriesPerSecond = 4096;

        private bool Refuses()
        {
            if (_entries.Count >= _capacity) RefusedAtCapacity++;

            else if (Overloaded) RefusedForQueueDelay++;

            else if (_saturated?.Invoke() == true) RefusedForApplication++;

            else return false;

            return true;
        }

        internal void Tick()
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var now = ReadClock();
            Collect();

            foreach (var entry in _entries)
                if (Synchronize(entry, now)) entry.Owner.Tick();
        }

        internal ValueTask<bool> WaitForOutputAsync(CancellationToken token) => _output.Reader.WaitToReadAsync(token);

        internal async ValueTask<bool> SendOneAsync(Func<OutboundDatagram, ValueTask> send)
        {
            ArgumentNullException.ThrowIfNull(send);

            if (!TryTakeOutput(out var peer, out var bytes)) return false;

            try
            {
                await send(new OutboundDatagram(peer, bytes)).ConfigureAwait(false);
                return true;
            }

            finally
            {
                FinishOutput();
            }
        }

        // Takes the next datagram to send, skipping output of retired connections and
        // paths. It counts against the output limits until FinishOutput, which the socket
        // owner calls once the send has completed or failed, or once it has copied the
        // datagram to send later.
        internal bool TryTakeOutput([NotNullWhen(true)] out IPEndPoint? peer, [NotNullWhen(true)] out byte[]? bytes)
        {
            while (true)
            {
                Output output;

                lock (_outputGate)
                {
                    ObjectDisposedException.ThrowIf(_closing, this);

                    if (_sending) throw new InvalidOperationException("QUIC output already has a sender.");

                    if (!_output.Reader.TryRead(out var next))
                    {
                        peer = null;
                        bytes = null;
                        return false;
                    }

                    output = next;
                    _sending = true;
                }

                try
                {
                    var entry = output.Entry;

                    if (
                        entry == null
                        || (_entries.Contains(entry)
                            && Synchronize(entry, ReadClock())
                            && (!output.PathId.HasValue || HasPath(entry, output.PathId.Value)))
                    )
                    {
                        peer = output.Peer ?? entry?.Peer
                            ?? throw new InvalidOperationException("QUIC output has no peer.");
                        bytes = output.Bytes;
                        _taken = output;
                        return true;
                    }
                }

                catch
                {
                    Finish(output);
                    throw;
                }

                Finish(output);
            }
        }

        internal void FinishOutput()
        {
            var output = _taken ?? throw new InvalidOperationException("No QUIC output is being sent.");
            _taken = null;
            Finish(output);
        }

        private void Finish(Output output)
        {
            lock (_outputGate)
            {
                Release(output);
                _sending = false;
                _sendFinished?.TrySetResult();
            }
        }

        private static bool HasPath(Entry entry, ulong id)
        {
            var paths = entry.State?.Paths;

            if (paths == null) return false;

            for (var index = 0; index < paths.Count; index++)
                if (paths[index].Id == id) return true;

            return false;
        }

        private double ReadClock()
        {
            var now = _clock();

            if (!double.IsFinite(now) || now < _now)
                throw new InvalidOperationException("QUIC dispatcher requires a monotonic clock.");

            _now = now;
            return now;
        }

        private double ConnectionClock(Entry entry)
        {
            var now = _clock();

            if (now >= entry.Deadline && Volatile.Read(ref entry.State)?.Established != true)
            {
                var failure = new TimeoutException("QUIC handshake expired before execution.");
                entry.ExpiryFailure = failure;
                throw failure;
            }

            return now;
        }

        private bool Reject()
        {
            Rejected++;
            return false;
        }

        private bool Synchronize(Entry entry, double now)
        {
            if (entry.Closing) return false;

            var status = Volatile.Read(ref entry.State);

            if (status?.Expired == true || (now >= entry.Deadline && status?.Established != true))
            {
                entry.Expired = true;
                ExpiredHandshakes++;
                Close(entry);
                return false;
            }

            if (status != null && !ReferenceEquals(entry.IssuedIds, status.IssuedIds))
            {
                var aliases = status.IssuedIds
                    .Select(static id => Convert.ToHexString(id.Span))
                    .ToArray();

                if (
                    aliases.Length is < 1
                        or > LocalConnectionIds.ActiveLimit
                            || aliases
                                .Distinct(StringComparer.Ordinal)
                                .Count()
                                != aliases.Length
                            || aliases.Any(
                                id => _initials.Keys.Any(alias => alias.Id == id)
                                    || (_issued.TryGetValue(id, out var owner) && !ReferenceEquals(owner, entry))
                            )
                )
                {
                    Reject();
                    Close(entry);
                    return false;
                }

                foreach (var alias in entry.Aliases)
                    if (!aliases.Contains(alias, StringComparer.Ordinal)) _issued.Remove(alias);

                foreach (var alias in aliases)
                    if (!entry.Aliases.Contains(alias, StringComparer.Ordinal)) _issued.Add(alias, entry);

                entry.Aliases = aliases;
                entry.IssuedIds = status.IssuedIds;
            }

            if (status != null && entry.Id == null)
            {
                entry.Id = Convert.ToHexString(status.Id.Span);
                Accepted++;
            }

            if (
                status != null
                && (!ReferenceEquals(entry.Paths, status.Paths) || !ReferenceEquals(entry.Tokens, status.Tokens))
            )
            {
                if (
                    status.Paths.Count > QuicPathValidation.Capacity
                    || status.Tokens.Count > PeerConnectionIds.ActiveLimit
                )
                {
                    Reject();
                    Close(entry);
                    return false;
                }

                var endpoints = status.Paths.Count == 0
                    ? [entry.Endpoints]
                    : status.Paths
                        .Select(static path => path.Endpoints)
                        .ToArray();
                var bindings = endpoints
                    .SelectMany(
                        endpoint => status.TokenIdentities.Select(
                            token => (endpoint.Peer, endpoint.Local, Token: token)
                        )
                    )
                    .Distinct()
                    .ToArray();

                if (bindings.Any(key => _resets.TryGetValue(key, out var owner) && !ReferenceEquals(owner, entry)))
                {
                    Reject();
                    Close(entry);
                    return false;
                }

                foreach (var key in entry.ResetBindings) _resets.Remove(key);

                foreach (var key in bindings) _resets.Add(key, entry);

                entry.ResetBindings = bindings;
                entry.Paths = status.Paths;
                entry.Tokens = status.Tokens;
                var groups = endpoints
                    .Select(static endpoint => (endpoint.Peer, endpoint.Local))
                    .ToHashSet();
                groups.Add((entry.Peer, entry.Local));

                foreach (var key in entry.Groups
                    .Where(key => !groups.Contains(key))
                    .ToArray())
                {
                    if (--_peerGroups[key].Count == 0) _peerGroups.Remove(key);

                    entry.Groups.Remove(key);
                }

                foreach (var key in groups)
                    if (entry.Groups.Add(key))
                    {
                        if (!_peerGroups.TryGetValue(key, out var group)) _peerGroups.Add(key, group = new PeerGroup());

                        group.Count++;
                    }
            }

            if (status?.Closed == true)
            {
                Close(entry);
                return false;
            }

            return true;
        }

        private static void Close(Entry entry)
        {
            entry.Closing = true;
            entry.Owner.RequestClose();
        }

        private void Publish(Entry entry, QuicConnectionOutput output)
        {
            if (Queue(entry, output)) _outputReady?.Invoke();
        }

        // Returns whether any datagram was queued.
        private bool Queue(Entry entry, QuicConnectionOutput output)
        {
            var queued = false;

            lock (_outputGate)
            {
                if (_closing) return false;

                var previous = entry.State;
                var issuedIds = output.IssuedConnectionIds ?? previous?.IssuedIds ?? [output.ConnectionId];
                var expired = previous?.Established != true && _clock() >= entry.Deadline;
                var paths = output.ActivePaths ?? previous?.Paths ?? [];
                var tokens = output.ResetTokens
                    ?? (previous != null && previous.ResetToken.Equals(output.ResetToken)
                        ? previous.Tokens
                        : output.ResetToken.Length == 16 ? [output.ResetToken] : Array.Empty<ReadOnlyMemory<byte>>());

                if (
                    previous == null
                    || previous.Established != output.Established
                    || previous.Closed != output.Closed
                    || !previous.ResetToken.Equals(output.ResetToken)
                    || !ReferenceEquals(previous.IssuedIds, issuedIds)
                    || !ReferenceEquals(previous.Paths, paths)
                    || !ReferenceEquals(previous.Tokens, tokens)
                    || expired
                )
                    Volatile.Write(
                        ref entry.State,
                        new Status(
                            output.ConnectionId,
                            output.Established,
                            output.Closed,
                            expired,
                            output.ResetToken,
                            output.ResetToken.Length == 16 ? ResetIdentity(output.ResetToken.Span) : null,
                            issuedIds,
                            paths,
                            tokens,
                            ReferenceEquals(previous?.Tokens, tokens)
                                ? previous.TokenIdentities
                                : [.. tokens.Select(token => ResetIdentity(token.Span))]
                        )
                    );

                if (expired)
                {
                    entry.Owner.RequestClose();
                    return false;
                }

                for (var index = 0; index < output.Datagrams.Count; index++)
                {
                    var bytes = output.Datagrams[index];
                    var path = output.OutputPaths != null && index < output.OutputPaths.Count
                        ? output.OutputPaths[index]
                        : null;

                    if (
                        _pendingPackets >= _outputPacketCapacity
                        || bytes.Length > _outputByteCapacity - _pendingBytes
                        || entry.Packets >= 64
                        || bytes.Length > 262144 - entry.Bytes
                    )
                    {
                        DroppedOutput++;
                        continue;
                    }

                    var pending = new Output(entry, bytes, path?.Endpoints.Peer, path?.Id);

                    if (!_output.Writer.TryWrite(pending))
                        throw new InvalidOperationException("QUIC output queue lost capacity.");

                    _pendingPackets++;
                    _pendingBytes += bytes.Length;
                    entry.Packets++;
                    entry.Bytes += bytes.Length;
                    queued = true;
                }
            }

            return queued;
        }

        private void Release(Output output)
        {
            _pendingPackets--;
            _pendingBytes -= output.Bytes.Length;

            if (output.Refusal) _pendingRefusals--;

            else if (output.Entry == null) _pendingResets--;

            else
            {
                output.Entry.Packets--;
                output.Entry.Bytes -= output.Bytes.Length;
            }
        }

        private void Collect()
        {
            if (Volatile.Read(ref _uncollected) != 0) CollectCompleted();
        }

        private void CollectCompleted()
        {
            foreach (var entry in _entries)
                if (entry.Owner.Completion.IsCompleted) _collected.Add(entry);

            try
            {
                foreach (var entry in _collected) Retire(entry);
            }

            finally
            {
                _collected.Clear();
            }
        }

        private void Retire(Entry entry)
        {
            _entries.Remove(entry);
            Interlocked.Decrement(ref _uncollected);
            entry.FinishAdmission(this);

            foreach (var key in entry.Groups)
                if (--_peerGroups[key].Count == 0) _peerGroups.Remove(key);

            entry.Groups.Clear();

            _initials.Remove((entry.Peer, entry.Local, entry.Original));

            foreach (var key in entry.ResetBindings) _resets.Remove(key);

            if (entry.Id != null)
            {
                foreach (var alias in entry.Aliases) _issued.Remove(alias);

                Retired++;
            }

            var failure = entry.Owner.Completion.Exception;

            if (
                !entry.Expired
                && (Volatile.Read(ref entry.State)?.Expired == true || failure
                    ?.Flatten()
                    .InnerExceptions.Any(error => ReferenceEquals(error, entry.ExpiryFailure))
                    == true)
            )
            {
                entry.Expired = true;
                ExpiredHandshakes++;
            }

            if (failure == null) return;

            if (
                entry.Owner.CleanupFailure == null
                && failure
                    .Flatten()
                    .InnerExceptions.All(
                        error => error is CryptographicException
                            or InvalidDataException
                            || ReferenceEquals(error, entry.ExpiryFailure)
                    )
            )
            {
                Rejected++;
                // Keep only the latest rejection; callers inspect this after
                // shutdown rather than logging on the packet-processing path.
                LastRejectedFailure = failure;
            }

            else
            {
                FailedOwners++;
                LastOwnerFailure = failure;
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (_outputGate)
            {
                if (_shutdown != null) return new ValueTask(_shutdown);

                _closing = true;

                if (_sending)
                    _sendFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                _shutdown = StopAsync([.. _entries], _sendFinished?.Task ?? Task.CompletedTask);
                return new ValueTask(_shutdown);
            }
        }

        private async Task StopAsync(Entry[] entries, Task sending)
        {
            await Task.Yield();
            var owners = Task.WhenAll(entries.Select(entry => entry.Owner
                    .DisposeAsync()
                    .AsTask()));
            await owners.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await sending.ConfigureAwait(false);
            CollectCompleted();

            var workers = _pool
                .DisposeAsync()
                .AsTask();
            await workers.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            CryptographicOperations.ZeroMemory(_resetKey);
            _connectionIds.Dispose();
            _tokens?.Dispose();

            lock (_outputGate)
            {
                _output.Writer.TryComplete();

                while (_output.Reader.TryRead(out var output)) Release(output);
            }

            var failures = new List<Exception>();

            if (workers.Exception != null) failures.AddRange(workers.Exception.InnerExceptions);

            if (LastOwnerFailure != null) failures.Add(LastOwnerFailure);

            if (failures.Count != 0) throw new AggregateException("QUIC connection ownership failed.", failures);
        }
    }
}
