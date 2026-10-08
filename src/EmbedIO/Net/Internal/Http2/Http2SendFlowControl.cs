using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    // Reserves both connection and stream credit atomically before DATA is queued.
    // The send owner must abort on a failure after reservation; credit cannot be
    // refunded after an ambiguous partial transport write.
    internal sealed class Http2SendFlowControl
    {
        private readonly object _sync = new();
        private sealed class Window
        {
            internal Window(int credit) { Credit = credit; }
            internal int Credit;
            internal HttpPriority Priority = new(3, false);
            internal Waiter? Waiting;
        }
        private sealed class Waiter
        {
            internal Waiter(Http2SendFlowControl owner, int id, int maximum, CancellationToken token, HttpPriority priority, long sequence)
            { Owner = owner; Id = id; Maximum = maximum; Token = token; Priority = priority; Sequence = sequence; }
            internal readonly Http2SendFlowControl Owner;
            internal readonly int Id;
            internal readonly int Maximum;
            internal readonly CancellationToken Token;
            internal HttpPriority Priority;
            internal readonly long Sequence;
            internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Pending;
            internal int ReadyIndex = -1;
            internal CancellationTokenRegistration Registration;
        }
        private readonly Dictionary<int, Window> _streams = new();
        private Waiter[] _ready = Array.Empty<Waiter>();
        private int _readyCount;
        private int _pendingCount;
        private static int Compare(Waiter left, Waiter right)
        {
            var urgency = left.Priority.Urgency.CompareTo(right.Priority.Urgency);
            if (urgency != 0) return urgency;
            var incremental = left.Priority.Incremental.CompareTo(right.Priority.Incremental);
            if (incremental != 0) return incremental;
            return left.Priority.Incremental ? left.Sequence.CompareTo(right.Sequence) : left.Id.CompareTo(right.Id);
        }
        private long _sequence;
        private int _connection = 65535;
        private int _initial = 65535;
        private Exception? _failure;
        public int PendingCount { get { lock (_sync) return _pendingCount; } }

        internal void Open(int streamId)
        {
            if (streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            lock (_sync)
            {
                ThrowIfFailed();
                if (_streams.ContainsKey(streamId)) throw new InvalidOperationException("Stream is already registered.");
                _streams.Add(streamId, new Window(_initial));
            }
        }

        internal void Close(int streamId)
        {
            lock (_sync)
            {
                if (!_streams.TryGetValue(streamId, out var window)) return;
                if (window.Waiting != null) FailWaiter(window.Waiting, new IOException("HTTP/2 stream is closed."));
                _streams.Remove(streamId);
            }
        }

        internal void Abort(Exception failure)
        {
            if (failure == null) throw new ArgumentNullException(nameof(failure));
            lock (_sync)
            {
                _failure ??= failure;
                foreach (var window in _streams.Values)
                    if (window.Waiting is { } waiter) FailWaiter(waiter, new IOException("HTTP/2 connection is closed.", _failure));
                _ready = Array.Empty<Waiter>();
                _streams.Clear();
            }
        }

        internal void AdjustInitialWindow(int delta)
        {
            lock (_sync)
            {
                ThrowIfFailed();
                var next = (long)_initial + delta;
                if (next < 0 || next > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(delta));
                foreach (var window in _streams.Values)
                    if ((long)window.Credit + delta > int.MaxValue)
                        throw new Http2ProtocolException(3, "SETTINGS overflows a stream flow-control window.");
                // A reduction can make stream credit negative. No DATA may be sent
                // until subsequent WINDOW_UPDATEs restore positive credit.
                foreach (var window in _streams.Values) { window.Credit += delta; UpdateReady(window); }
                _initial = (int)next;
                Grant();
            }
        }

        internal void Update(int streamId, int increment)
        {
            if (streamId < 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            if (increment <= 0) throw new Http2ProtocolException(1, "WINDOW_UPDATE increment must be positive.", streamId);
            lock (_sync)
            {
                ThrowIfFailed();
                // Stream state determines whether an unknown ID is idle or closed;
                // the caller must perform that protocol check before this method.
                if (streamId != 0 && !_streams.ContainsKey(streamId)) return;
                var current = streamId == 0 ? _connection : _streams[streamId].Credit;
                if ((long)current + increment > int.MaxValue)
                    throw new Http2ProtocolException(3, "Flow-control window overflow.", streamId);
                if (streamId == 0) _connection += increment;
                else { var window = _streams[streamId]; window.Credit += increment; UpdateReady(window); }
                Grant();
            }
        }

        internal void SetPriority(int streamId, HttpPriority priority)
        {
            lock (_sync)
            {
                ThrowIfFailed();
                if (_streams.TryGetValue(streamId, out var window))
                {
                    window.Priority = priority;
                    if (window.Waiting is { } waiter)
                    {
                        RemoveReady(waiter);
                        waiter.Priority = priority;
                        UpdateReady(window);
                    }
                    Grant();
                }
            }
        }

        internal Task<int> ReserveAsync(int streamId, int maximumBytes, CancellationToken token)
        {
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            Waiter waiter;
            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                ThrowIfFailed();
                if (!_streams.TryGetValue(streamId, out var window)) throw new IOException("HTTP/2 stream is closed.");
                if (window.Waiting != null) throw new InvalidOperationException("A stream already has a pending DATA reservation.");
                var count = Math.Min(maximumBytes, Math.Min(_connection, window.Credit));
                // Every credit mutation drains eligible waiters under this gate.
                // Any remaining waiter is stream-blocked; do not allocate a
                // queue entry for a newly writable stream.
                if (count > 0)
                {
                    _connection -= count; window.Credit -= count;
                    return Task.FromResult(count);
                }
                waiter = new Waiter(this, streamId, maximumBytes, token, window.Priority, _sequence++);
                window.Waiting = waiter;
                waiter.Pending = true;
                _pendingCount++;
                UpdateReady(window);
                // Synchronous cancellation is safe under this reentrant gate.
                // Disposal occurs after completion, outside the gate.
                waiter.Registration = token.Register(static state =>
                {
                    var value = (Waiter)(state ?? throw new InvalidOperationException("Missing reservation owner."));
                    value.Owner.Cancel(value);
                }, waiter);
                Grant();
            }
            return CompleteAsync(waiter);
        }
        private static async Task<int> CompleteAsync(Waiter waiter)
        {
            try { return await waiter.Completion.Task.ConfigureAwait(false); }
            finally { waiter.Registration.Dispose(); }
        }
        private void Remove(Waiter waiter)
        {
            if (!waiter.Pending) return;
            RemoveReady(waiter);
            waiter.Pending = false;
            _pendingCount--;
            if (_streams.TryGetValue(waiter.Id, out var window)) window.Waiting = null;
        }
        private void Cancel(Waiter waiter)
        {
            lock (_sync)
            {
                if (!waiter.Pending) return;
                Remove(waiter); waiter.Completion.TrySetCanceled(waiter.Token);
                Grant();
            }
        }
        private void FailWaiter(Waiter waiter, Exception error)
        { Remove(waiter); waiter.Completion.TrySetException(error); }
        private void UpdateReady(Window window)
        {
            if (window.Waiting is not { } waiter) return;
            if (window.Credit > 0) AddReady(waiter);
            else RemoveReady(waiter);
        }
        private void AddReady(Waiter waiter)
        {
            if (waiter.ReadyIndex >= 0) return;
            if (_readyCount == _ready.Length) Array.Resize(ref _ready, Math.Max(8, _ready.Length * 2));
            waiter.ReadyIndex = _readyCount;
            _ready[_readyCount++] = waiter;
            SiftUp(waiter.ReadyIndex);
        }
        private void RemoveReady(Waiter waiter)
        {
            var index = waiter.ReadyIndex;
            if (index < 0) return;
            var last = _ready[--_readyCount];
            Array.Clear(_ready, _readyCount, 1);
            waiter.ReadyIndex = -1;
            if (index == _readyCount) return;
            _ready[index] = last;
            last.ReadyIndex = index;
            if (index > 0 && Compare(last, _ready[(index - 1) / 2]) < 0) SiftUp(index);
            else SiftDown(index);
        }
        private void SiftUp(int index)
        {
            var value = _ready[index];
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                var previous = _ready[parent];
                if (Compare(value, previous) >= 0) break;
                _ready[index] = previous; previous.ReadyIndex = index;
                index = parent;
            }
            _ready[index] = value; value.ReadyIndex = index;
        }
        private void SiftDown(int index)
        {
            var value = _ready[index];
            while (index * 2 + 1 < _readyCount)
            {
                var child = index * 2 + 1;
                if (child + 1 < _readyCount && Compare(_ready[child + 1], _ready[child]) < 0) child++;
                var next = _ready[child];
                if (Compare(value, next) <= 0) break;
                _ready[index] = next; next.ReadyIndex = index;
                index = child;
            }
            _ready[index] = value; value.ReadyIndex = index;
        }
        private void Grant()
        {
            while (_connection > 0 && _readyCount > 0)
            {
                var best = _ready[0];
                if (best.Token.IsCancellationRequested)
                { Remove(best); best.Completion.TrySetCanceled(best.Token); continue; }
                var window = _streams[best.Id];
                var count = Math.Min(best.Maximum, Math.Min(_connection, window.Credit));
                _connection -= count; window.Credit -= count;
                Remove(best); best.Completion.TrySetResult(count);
            }
        }
        private void ThrowIfFailed()
        {
            if (_failure != null) throw new IOException("HTTP/2 connection is closed.", _failure);
        }
    }
}
