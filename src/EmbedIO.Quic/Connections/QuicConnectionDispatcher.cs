using System;
using System.Collections.Generic;
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
            string? ResetIdentity
        );

        private sealed record Output(Entry Entry, byte[] Bytes);

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
                double deadline
            )
            {
                ArgumentNullException.ThrowIfNull(dispatcher);
                ArgumentNullException.ThrowIfNull(create);
                Original = original;
                Peer = QuicConnectionEndpoints.Copy(peer);
                Local = local == null ? null : QuicConnectionEndpoints.Copy(local);
                Deadline = deadline;
                Owner = new QuicConnectionOwner(
                    dispatcher._pool,
                    (packet, now) => create(
                        packet,
                        now,
                        Owner ?? throw new InvalidOperationException("Connection owner was not initialized."),
                        new QuicConnectionEndpoints(Peer, Local)
                    ),
                    () => dispatcher.ConnectionClock(this),
                    output => dispatcher.Publish(this, output)
                );
            }

            internal string Original { get; }
            internal IPEndPoint Peer { get; }
            internal IPEndPoint? Local { get; }
            internal double Deadline { get; }
            internal QuicConnectionOwner Owner { get; }

            internal PeerGroup Group = new();
            internal Status? State;
            internal string? Id;
            internal string? ResetIdentity;
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
        private readonly int _outputPacketCapacity;
        private readonly int _outputByteCapacity;
        private readonly Channel<Output> _output;
        private readonly object _outputGate = new();
        private readonly HashSet<Entry> _entries = [];
        private readonly Dictionary<string, Entry> _issued = new(StringComparer.Ordinal);
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local, string Id), Entry> _initials = [];
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local, string Token), Entry> _resets = [];
        private readonly Dictionary<(IPEndPoint Peer, IPEndPoint? Local), PeerGroup> _peerGroups = [];
        private readonly byte[] _resetKey = RandomNumberGenerator.GetBytes(32);
        private Task? _shutdown;
        private TaskCompletionSource? _sendFinished;
        private bool _sending;
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
            int? admissionAttemptsPerSecond = null
        )
        {
            ArgumentNullException.ThrowIfNull(create);
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65536);
            ArgumentOutOfRangeException.ThrowIfLessThan(outputPacketCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(outputPacketCapacity, 65536);
            ArgumentOutOfRangeException.ThrowIfLessThan(outputByteCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(outputByteCapacity, 16777216);

            if (!double.IsFinite(handshakeTimeout) || handshakeTimeout <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));

            _create = create;
            _clock = clock;
            _capacity = capacity;
            _attemptLimit = admissionAttemptsPerSecond ?? capacity;
            ArgumentOutOfRangeException.ThrowIfNegative(_attemptLimit);
            _handshakeTimeout = handshakeTimeout;
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
        internal int EndpointGroups => _peerGroups.Count;
        internal int Accepted { get; private set; }
        internal int Retired { get; private set; }
        internal int Peak { get; private set; }
        internal int Rejected { get; private set; }
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

            var id = Convert.ToHexString(destination);

            if (!_issued.TryGetValue(id, out var entry)) _initials.TryGetValue((peer, local, id), out entry);

            QuicConnectionOwner? resetOwner = null;

            if (entry != null)
            {
                if (!entry.Peer.Equals(peer) || !Equals(entry.Local, local))
                    return RouteReset(bytes, peer, local, now) || Reject();

                if (!Synchronize(entry, now)) return RouteReset(bytes, peer, local, now) || Reject();

                // Preserve authenticated-packet priority when a randomized reset
                // header names a sibling. Only shared endpoint groups need this
                // candidate lookup; forwarding follows failed authentication.
                if (entry.Group.Count > 1)
                {
                    var candidate = FindReset(bytes, peer, local, now);

                    if (candidate != null && !ReferenceEquals(candidate, entry)) resetOwner = candidate.Owner;
                }
            }

            else
            {
                if (RouteReset(bytes, peer, local, now)) return true;

                if (!Accepting || !initial || bytes.Length is < 1200 or > 65535 || _entries.Count >= _capacity)
                    return Reject();

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

                entry = new Entry(this, _create, id, peer, local, now + _handshakeTimeout);
                var groupKey = (entry.Peer, entry.Local);

                if (_peerGroups.TryGetValue(groupKey, out var group)) entry.Group = group;

                else _peerGroups.Add(groupKey, entry.Group);

                entry.Group.Count++;
                _entries.Add(entry);
                _initials.Add((entry.Peer, entry.Local, id), entry);
                Peak = Math.Max(Peak, _entries.Count);
            }

            if (entry.Owner.Receive(bytes, resetOwner)) return true;

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

        private bool RouteReset(ReadOnlySpan<byte> bytes, IPEndPoint peer, IPEndPoint? local, double now) =>
            FindReset(bytes, peer, local, now)?.Owner.Receive(bytes) == true;

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

            while (true)
            {
                Output output;

                lock (_outputGate)
                {
                    ObjectDisposedException.ThrowIf(_closing, this);

                    if (_sending) throw new InvalidOperationException("QUIC output already has a sender.");

                    if (!_output.Reader.TryRead(out var next)) return false;

                    output = next;
                    _sending = true;
                }

                try
                {
                    if (!_entries.Contains(output.Entry) || !Synchronize(output.Entry, ReadClock())) continue;

                    await send(new OutboundDatagram(output.Entry.Peer, output.Bytes)).ConfigureAwait(false);
                    return true;
                }

                finally
                {
                    lock (_outputGate)
                    {
                        Release(output);
                        _sending = false;
                        _sendFinished?.TrySetResult();
                    }
                }
            }
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

            if (status != null && entry.Id == null)
            {
                var id = Convert.ToHexString(status.Id.Span);

                if (_initials.Keys.Any(alias => alias.Id == id) || !_issued.TryAdd(id, entry))
                {
                    Reject();
                    Close(entry);
                    return false;
                }

                entry.Id = id;
                Accepted++;
            }

            if (status != null && entry.ResetIdentity != status.ResetIdentity)
            {
                if (entry.ResetIdentity != null) _resets.Remove((entry.Peer, entry.Local, entry.ResetIdentity));

                entry.ResetIdentity = null;

                if (status.ResetIdentity != null)
                {
                    if (!_resets.TryAdd((entry.Peer, entry.Local, status.ResetIdentity), entry))
                    {
                        Reject();
                        Close(entry);
                        return false;
                    }

                    entry.ResetIdentity = status.ResetIdentity;
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
            lock (_outputGate)
            {
                if (_closing) return;

                var previous = entry.State;
                var expired = previous?.Established != true && _clock() >= entry.Deadline;

                if (
                    previous == null
                    || previous.Established != output.Established
                    || previous.Closed != output.Closed
                    || !previous.ResetToken.Equals(output.ResetToken)
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
                            output.ResetToken.Length == 16 ? ResetIdentity(output.ResetToken.Span) : null
                        )
                    );

                if (expired)
                {
                    entry.Owner.RequestClose();
                    return;
                }

                foreach (var bytes in output.Datagrams)
                {
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

                    var pending = new Output(entry, bytes);

                    if (!_output.Writer.TryWrite(pending))
                        throw new InvalidOperationException("QUIC output queue lost capacity.");

                    _pendingPackets++;
                    _pendingBytes += bytes.Length;
                    entry.Packets++;
                    entry.Bytes += bytes.Length;
                }
            }
        }

        private void Release(Output output)
        {
            _pendingPackets--;
            _pendingBytes -= output.Bytes.Length;
            output.Entry.Packets--;
            output.Entry.Bytes -= output.Bytes.Length;
        }

        private void Collect()
        {
            foreach (var entry in _entries.ToArray())
            {
                if (!entry.Owner.Completion.IsCompleted) continue;

                _entries.Remove(entry);
                entry.Group.Count--;

                if (entry.Group.Count == 0) _peerGroups.Remove((entry.Peer, entry.Local));

                _initials.Remove((entry.Peer, entry.Local, entry.Original));

                if (entry.ResetIdentity != null) _resets.Remove((entry.Peer, entry.Local, entry.ResetIdentity));

                if (entry.Id != null)
                {
                    _issued.Remove(entry.Id);
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

                if (failure == null) continue;

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
            Collect();

            var workers = _pool
                .DisposeAsync()
                .AsTask();
            await workers.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            CryptographicOperations.ZeroMemory(_resetKey);

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
