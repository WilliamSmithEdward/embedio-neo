using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EmbedIO.Quic
{
    internal sealed record QuicConnectionOutput(
        ReadOnlyMemory<byte> ConnectionId,
        IReadOnlyList<byte[]> Datagrams,
        bool Established,
        bool Closed,
        ReadOnlyMemory<byte> ResetToken = default,
        IReadOnlyList<ReadOnlyMemory<byte>>? IssuedConnectionIds = null,
        IReadOnlyList<QuicPathRoute?>? OutputPaths = null,
        IReadOnlyList<QuicPathRoute>? ActivePaths = null,
        IReadOnlyList<ReadOnlyMemory<byte>>? ResetTokens = null
    );

    // The mailbox owns copied input until its turn completes. Factory, packet,
    // timer, application and cleanup work all use the same serialized registration.
    internal sealed class QuicConnectionOwner : IAsyncDisposable
    {
        private readonly object _gate = new();

        // Copies are rented and cleared before they are returned.
        private readonly Queue<(
            byte[] Bytes,
            int Length,
            QuicConnectionOwner? ResetOwner,
            QuicConnectionEndpoints? Endpoints
        )> _packets = new();
        private readonly QuicExecutionPool.Registration _execution;
        private readonly QuicConnectionFactory _create;
        private readonly Func<double> _clock;
        private readonly Action<QuicConnectionOutput> _publish;
        private readonly int _packetCapacity;
        private readonly int _byteCapacity;
        private QuicConnection? _connection;
        private byte[] _id = [];
        private ReadOnlyMemory<byte> _resetToken;
        private IReadOnlyList<ReadOnlyMemory<byte>>? _issuedIds;
        private bool _established;
        private IReadOnlyList<QuicPathRoute>? _activePaths;
        private IReadOnlyList<ReadOnlyMemory<byte>>? _resetTokens;
        private bool _reported;
        private bool _closing;
        private bool _tick;
        private int _pendingBytes;
        private int _pendingPackets;
        private double _now;

        internal QuicConnectionOwner(
            QuicExecutionPool pool,
            QuicConnectionFactory create,
            Func<double> clock,
            Action<QuicConnectionOutput> publish,
            int packetCapacity = 64,
            int byteCapacity = 262144,
            IQuicConnectionIdSource? connectionIds = null,
            QuicRetry? retry = null,
            Action? completed = null
        )
        {
            ArgumentNullException.ThrowIfNull(pool);
            ArgumentNullException.ThrowIfNull(create);
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentNullException.ThrowIfNull(publish);
            ArgumentOutOfRangeException.ThrowIfLessThan(packetCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(packetCapacity, 4096);
            ArgumentOutOfRangeException.ThrowIfLessThan(byteCapacity, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCapacity, 16777216);
            ConnectionIds = connectionIds;
            Retry = retry;
            _create = create;
            _clock = clock;
            _publish = publish;
            _packetCapacity = packetCapacity;
            _byteCapacity = byteCapacity;
            _execution = pool.Register(Execute, Cleanup, completed);
        }

        internal IQuicConnectionIdSource? ConnectionIds { get; }

        // The redeemed Retry token of the connection's first Initial, or null without Retry.
        internal QuicRetry? Retry { get; }
        internal Task Completion => _execution.Completion;
        internal Exception? CleanupFailure { get; private set; }

        internal int PendingBytes
        {
            get
            {
                lock (_gate) return _pendingBytes;
            }
        }

        internal int PendingPackets
        {
            get
            {
                lock (_gate) return _pendingPackets;
            }
        }

        internal bool Receive(
            ReadOnlySpan<byte> bytes,
            QuicConnectionOwner? resetOwner = null,
            QuicConnectionEndpoints? endpoints = null
        )
        {
            lock (_gate)
            {
                if (_closing || _execution.Closing) return false;

                if (bytes.Length is < 1 or > 65535) return false;

                if (_pendingPackets >= _packetCapacity || bytes.Length > _byteCapacity - _pendingBytes) return false;

                var copy = ArrayPool<byte>.Shared.Rent(bytes.Length);
                bytes.CopyTo(copy);
                _packets.Enqueue((copy, bytes.Length, resetOwner, endpoints));
                _pendingPackets++;
                _pendingBytes += bytes.Length;
            }

            return _execution.Wake();
        }

        private static void Return(byte[] packet, int length)
        {
            packet
                .AsSpan(0, length)
                .Clear();
            ArrayPool<byte>.Shared.Return(packet);
        }

        internal void Notify() => _execution.Wake();

        internal void Tick()
        {
            lock (_gate)
            {
                if (_closing) return;

                _tick = true;
            }

            _execution.Wake();
        }

        private double ReadClock()
        {
            var now = _clock();

            if (!double.IsFinite(now) || now < _now)
                throw new InvalidOperationException("QUIC owner requires a monotonic clock.");

            _now = now;
            return now;
        }

        // Queued datagrams one turn processes before other connections' turns run.
        private const int PacketsPerTurn = 16;

        private void Execute()
        {
            var processed = 0;

            while (processed < PacketsPerTurn && Process(processed == 0)) processed++;

            bool pending;

            lock (_gate) pending = _packets.Count != 0 || _tick;

            if (pending) _execution.Wake();
        }

        // Processes the next queued datagram. With none queued, only a turn's first call runs
        // the timer or application work instead. Returns whether it processed a datagram and
        // the connection is still open.
        private bool Process(bool first)
        {
            byte[]? packet;
            int length;
            QuicConnectionOwner? resetOwner;
            QuicConnectionEndpoints? endpoints;
            bool tick;

            lock (_gate)
            {
                if (_packets.TryDequeue(out var pending))
                {
                    packet = pending.Bytes;
                    length = pending.Length;
                    resetOwner = pending.ResetOwner;
                    endpoints = pending.Endpoints;
                }

                else if (!first) return false;

                else
                {
                    packet = null;
                    length = 0;
                    resetOwner = null;
                    endpoints = null;
                }

                tick = _tick;
                _tick = false;
            }

            try
            {
                if (packet != null && _connection == null)
                {
                    _connection = _create(packet.AsSpan(0, length), ReadClock())
                        ?? throw new InvalidOperationException("QUIC factory returned no connection.");
                    _connection.UseExecutionClock(ReadClock);
                    _id = _connection.ServerId.ToArray();
                }

                var connection = _connection;

                if (connection == null) return false;

                connection.BeginRouting(endpoints);
                var output = packet == null
                    ? tick ? connection.Tick(ReadClock()) : connection.PumpApplications(ReadClock())
                    : connection.Receive(packet.AsSpan(0, length), ReadClock());

                // A shared-endpoint token candidate is forwarded only after the
                // header owner fails authentication. Its bounded mailbox performs
                // the final comparison; forwarding never loops or touches routes.
                if (
                    packet != null
                    && resetOwner != null
                    && !connection.DatagramAuthenticated
                    && !connection.StatelessResetReceived
                )
                    resetOwner.Receive(packet.AsSpan(0, length), endpoints: endpoints);

                if (packet != null && tick && !connection.Closed) output.AddRange(connection.Tick(ReadClock()));

                var resetToken = connection.ResetToken;
                var issuedIds = connection.IssuedConnectionIds;
                var activePaths = connection.ActivePaths;
                var resetTokens = connection.ResetTokens;

                if (
                    !_execution.Closing
                    && (!_reported
                        || output.Count != 0
                        || connection.Established != _established
                        || connection.Closed
                        || !resetToken.Equals(_resetToken)
                        || !ReferenceEquals(issuedIds, _issuedIds)
                        || !ReferenceEquals(activePaths, _activePaths)
                        || !ReferenceEquals(resetTokens, _resetTokens))
                )
                {
                    _reported = true;
                    _established = connection.Established;
                    _resetToken = resetToken;
                    _issuedIds = issuedIds;
                    _activePaths = activePaths;
                    _resetTokens = resetTokens;
                    _publish(
                        new QuicConnectionOutput(
                            _id,
                            output,
                            _established,
                            connection.Closed,
                            _resetToken,
                            _issuedIds,
                            connection.OutputPaths,
                            _activePaths,
                            _resetTokens
                        )
                    );
                }

                if (connection.Closed)
                {
                    RequestClose();
                    return false;
                }

                return packet != null;
            }

            finally
            {
                if (packet != null)
                {
                    lock (_gate)
                    {
                        _pendingBytes -= length;
                        _pendingPackets--;
                    }

                    Return(packet, length);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            RequestClose();
            return _execution.DisposeAsync();
        }

        internal void RequestClose()
        {
            lock (_gate) _closing = true;

            _execution.RequestClose();
        }

        private void Cleanup()
        {
            lock (_gate)
            {
                _closing = true;

                while (_packets.TryDequeue(out var packet)) Return(packet.Bytes, packet.Length);

                _pendingBytes = 0;
                _pendingPackets = 0;
                _tick = false;
            }

            try
            {
                _connection?.Dispose();
            }

            catch (Exception error) when (
                error is not OutOfMemoryException
                    and not StackOverflowException
                    and not AccessViolationException
            )
            {
                CleanupFailure = error;
                throw;
            }

            finally
            {
                _connection = null;
            }
        }
    }
}
