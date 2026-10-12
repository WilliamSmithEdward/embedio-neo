using System;
using System.Threading.Tasks;

namespace EmbedIO.WebSockets.Internal
{
    // These are internal preparation limits, not a new public policy or default.
    internal sealed class WebSocketDeliveryLimits
    {
        internal WebSocketDeliveryLimits(int? connectionMessages, long? connectionBytes, int? moduleMessages, long? moduleBytes)
        {
            if (connectionMessages < 1) throw new ArgumentOutOfRangeException(nameof(connectionMessages));
            if (connectionBytes < 0) throw new ArgumentOutOfRangeException(nameof(connectionBytes));
            if (moduleMessages < 1) throw new ArgumentOutOfRangeException(nameof(moduleMessages));
            if (moduleBytes < 0) throw new ArgumentOutOfRangeException(nameof(moduleBytes));
            ConnectionMessages = connectionMessages;
            ConnectionBytes = connectionBytes;
            ModuleMessages = moduleMessages;
            ModuleBytes = moduleBytes;
        }

        internal int? ConnectionMessages { get; }
        internal long? ConnectionBytes { get; }
        internal int? ModuleMessages { get; }
        internal long? ModuleBytes { get; }
    }

    // One gate admits connection and module ownership together. Bytes are logical
    // payload bytes, not allocator sizes or memory retained by application code.
    internal sealed class WebSocketDeliveryBudget
    {
        private readonly object _gate = new();
        private readonly WebSocketDeliveryLimits _limits;
        private long _messages;
        private long _bytes;

        internal WebSocketDeliveryBudget(WebSocketDeliveryLimits limits)
            => _limits = limits ?? throw new ArgumentNullException(nameof(limits));

        internal Connection CreateConnection() => new(this);
        internal Snapshot Outstanding { get { lock (_gate) return new Snapshot(_messages, _bytes); } }

        internal readonly struct Snapshot
        {
            internal Snapshot(long messages, long bytes) { Messages = messages; Bytes = bytes; }
            internal long Messages { get; }
            internal long Bytes { get; }
        }

        internal sealed class Connection
        {
            private readonly WebSocketDeliveryBudget _owner;
            private long _messages;
            private long _bytes;
            private bool _stopped;

            internal Connection(WebSocketDeliveryBudget owner) => _owner = owner;
            internal Snapshot Outstanding { get { lock (_owner._gate) return new Snapshot(_messages, _bytes); } }

            internal bool TryReserve(out Lease? lease)
            {
                lock (_owner._gate)
                {
                    lease = null;
                    if (_stopped || _messages >= (_owner._limits.ConnectionMessages ?? long.MaxValue)
                        || _owner._messages >= (_owner._limits.ModuleMessages ?? long.MaxValue)) return false;
                    // Allocate before publishing counters so allocation failure
                    // cannot consume admission without an owner to release it.
                    lease = new Lease(_owner, this);
                    _messages++;
                    _owner._messages++;
                    return true;
                }
            }

            // Closing admission does not mean callbacks or queued delivery ended.
            // The queue owner separately disposes messages it will not deliver.
            internal void StopAdmission() { lock (_owner._gate) _stopped = true; }

            internal void AddBytes(long bytes) => _bytes += bytes;
            internal void Release(long bytes) { _messages--; _bytes -= bytes; }
        }

        internal sealed class Lease : IDisposable
        {
            private const int Assembling = 0, Queued = 1, CallbackOwned = 2, Released = 3;
            private readonly WebSocketDeliveryBudget _owner;
            private readonly Connection _connection;
            private long _bytes;
            private int _state;

            internal Lease(WebSocketDeliveryBudget owner, Connection connection)
            { _owner = owner; _connection = connection; }

            // Call before allocating/copying the next fragment. Failed growth
            // changes neither ledger; the caller owns rejection and disposal.
            internal bool TryGrow(long bytes)
            {
                if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
                lock (_owner._gate)
                {
                    if (_state != Assembling) throw new InvalidOperationException("Message assembly has ended.");
                    var connection = _connection.Outstanding;
                    if (bytes > (_owner._limits.ConnectionBytes ?? long.MaxValue) - connection.Bytes
                        || bytes > (_owner._limits.ModuleBytes ?? long.MaxValue) - _owner._bytes) return false;
                    _bytes += bytes;
                    _connection.AddBytes(bytes);
                    _owner._bytes += bytes;
                    return true;
                }
            }

            internal void CompleteMessage()
            {
                lock (_owner._gate)
                {
                    if (_state != Assembling) throw new InvalidOperationException("Message assembly has ended.");
                    _state = Queued;
                }
            }

            // Callback invocation is outside the ledger gate and remains
            // overlapping with other callbacks. Await its actual returned Task,
            // not a token/timeout race, before returning message ownership.
            internal async Task DeliverAsync(Func<Task> callback)
            {
                if (callback == null) throw new ArgumentNullException(nameof(callback));
                lock (_owner._gate)
                {
                    if (_state != Queued) throw new InvalidOperationException("Message is not queued for delivery.");
                    _state = CallbackOwned;
                }
                try
                {
                    await (callback() ?? throw new InvalidOperationException("Message callback returned no Task.")).ConfigureAwait(false);
                }
                finally
                {
                    lock (_owner._gate) ReleaseUnderGate();
                }
            }

            public void Dispose()
            {
                lock (_owner._gate)
                {
                    // A transport/queue cleanup cannot retire application work
                    // that still owns this delivery. Its Task's finally does so.
                    if (_state != CallbackOwned) ReleaseUnderGate();
                }
            }

            private void ReleaseUnderGate()
            {
                if (_state == Released) return;
                _state = Released;
                _connection.Release(_bytes);
                _owner._messages--;
                _owner._bytes -= _bytes;
            }
        }
    }
}
