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
        private readonly Dictionary<int, int> _streams = new();
        private int _connection = 65535;
        private int _initial = 65535;
        private Exception? _failure;
        private TaskCompletionSource<bool>? _changed;

        internal void Open(int streamId)
        {
            if (streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            lock (_sync)
            {
                ThrowIfFailed();
                if (_streams.ContainsKey(streamId)) throw new InvalidOperationException("Stream is already registered.");
                _streams.Add(streamId, _initial);
            }
        }

        internal void Close(int streamId)
        {
            lock (_sync) if (_streams.Remove(streamId)) Pulse();
        }

        internal void Abort(Exception failure)
        {
            if (failure == null) throw new ArgumentNullException(nameof(failure));
            lock (_sync)
            {
                _failure ??= failure;
                _streams.Clear();
                Pulse();
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
                    if ((long)window + delta > int.MaxValue)
                        throw new Http2ProtocolException(3, "SETTINGS overflows a stream flow-control window.");
                // A reduction can make stream credit negative. No DATA may be sent
                // until subsequent WINDOW_UPDATEs restore positive credit.
                var ids = new int[_streams.Count];
                _streams.Keys.CopyTo(ids, 0);
                foreach (var id in ids) _streams[id] += delta;
                _initial = (int)next;
                Pulse();
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
                var current = streamId == 0 ? _connection : _streams[streamId];
                if ((long)current + increment > int.MaxValue)
                    throw new Http2ProtocolException(3, "Flow-control window overflow.", streamId);
                if (streamId == 0) _connection += increment;
                else _streams[streamId] += increment;
                Pulse();
            }
        }

        internal async Task<int> ReserveAsync(int streamId, int maximumBytes, CancellationToken token)
        {
            if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            while (true)
            {
                Task changed;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    ThrowIfFailed();
                    if (!_streams.TryGetValue(streamId, out var window)) throw new IOException("HTTP/2 stream is closed.");
                    var count = Math.Min(maximumBytes, Math.Min(_connection, window));
                    if (count > 0)
                    {
                        _connection -= count;
                        _streams[streamId] = window - count;
                        return count;
                    }
                    changed = (_changed ??= NewSignal()).Task;
                }
                await WaitAsync(changed, token).ConfigureAwait(false);
            }
        }

        private static async Task WaitAsync(Task changed, CancellationToken token)
        {
            if (!token.CanBeCanceled) { await changed.ConfigureAwait(false); return; }
            var canceled = NewSignal();
            using (token.Register(state => ((TaskCompletionSource<bool>)(state ?? throw new InvalidOperationException("Missing cancellation signal."))).TrySetResult(true), canceled))
            {
                await Task.WhenAny(changed, canceled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
        }

        private void ThrowIfFailed()
        {
            if (_failure != null) throw new IOException("HTTP/2 connection is closed.", _failure);
        }

        private void Pulse()
        {
            var previous = _changed;
            _changed = null;
            previous?.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
