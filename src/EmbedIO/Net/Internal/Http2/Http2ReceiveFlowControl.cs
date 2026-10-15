using System;
using System.Collections.Generic;

namespace EmbedIO.Net.Internal.Http2
{
    internal readonly struct Http2WindowUpdates
    {
        internal Http2WindowUpdates(int connection, int stream) { Connection = connection; Stream = stream; }
        public int Connection { get; }
        public int Stream { get; }
    }

    // DATA payload length includes padding. Credit is returned only when the body
    // consumer releases bytes (or a reset discards them), not when frames arrive.
    // The caller must enqueue the returned WINDOW_UPDATEs and abort on write failure.
    internal sealed class Http2ReceiveFlowControl
    {
        private sealed class Window
        {
            internal Window(int size) { Available = size; }
            internal int Available;
            internal int Outstanding;
            internal int Consumed;
        }
        private readonly object _sync = new();
        private readonly Dictionary<int, Window> _streams = new();
        private readonly int _initial;
        private int _connection = 65535;
        private int _consumed;
        private bool _aborted;

        internal Http2ReceiveFlowControl(int initialWindow = 65535)
        {
            if (initialWindow < 1) throw new ArgumentOutOfRangeException(nameof(initialWindow));
            _initial = initialWindow;
        }

        internal void Open(int streamId)
        {
            if (streamId <= 0) throw new ArgumentOutOfRangeException(nameof(streamId));
            lock (_sync)
            {
                ThrowIfAborted();
                if (_streams.ContainsKey(streamId)) throw new InvalidOperationException("Stream already registered.");
                _streams.Add(streamId, new Window(_initial));
            }
        }

        internal void Receive(int streamId, int payloadBytes)
        {
            if (payloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            lock (_sync)
            {
                ThrowIfAborted();
                if (!_streams.TryGetValue(streamId, out var window)) throw new InvalidOperationException("Stream is not registered.");
                if (payloadBytes > _connection) throw new Http2ProtocolException(3, "Connection receive window exceeded.");
                _connection -= payloadBytes;
                window.Outstanding += payloadBytes;
                window.Available -= payloadBytes;
                // Even DATA that violates a stream window consumes connection
                // credit. Close releases it after the stream is reset.
                if (window.Available < 0) throw new Http2ProtocolException(3, "Stream receive window exceeded.", streamId);
            }
        }

        internal Http2WindowUpdates Consume(int streamId, int bytes, bool flush = false)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            lock (_sync)
            {
                ThrowIfAborted();
                // A body read can race reset. Close has already returned its bytes.
                if (!_streams.TryGetValue(streamId, out var window)) return default;
                if (bytes > window.Outstanding) throw new InvalidOperationException("Cannot consume unreceived bytes.");
                window.Outstanding -= bytes;
                window.Consumed += bytes;
                _consumed += bytes;
                var streamUpdate = 0;
                if (flush || window.Available <= _initial / 2)
                {
                    streamUpdate = window.Consumed;
                    window.Available += streamUpdate;
                    window.Consumed = 0;
                }
                return new Http2WindowUpdates(ReleaseConnection(flush), streamUpdate);
            }
        }

        internal int Close(int streamId)
        {
            lock (_sync)
            {
                ThrowIfAborted();
                if (!_streams.TryGetValue(streamId, out var window)) return 0;
                _streams.Remove(streamId);
                _consumed += window.Outstanding;
                return ReleaseConnection(false);
            }
        }

        // Closed-stream DATA can still arrive after a locally sent reset. Stream
        // state decides whether to ignore it; connection accounting still applies.
        internal int Discard(int payloadBytes)
        {
            if (payloadBytes < 0) throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            lock (_sync)
            {
                ThrowIfAborted();
                if (payloadBytes > _connection) throw new Http2ProtocolException(3, "Connection receive window exceeded.");
                _connection -= payloadBytes;
                _consumed += payloadBytes;
                return ReleaseConnection(false);
            }
        }

        internal void Abort()
        {
            lock (_sync) { _aborted = true; _streams.Clear(); }
        }

        private int ReleaseConnection(bool flush)
        {
            if (!flush && _connection > 65535 / 2) return 0;
            var increment = _consumed;
            _connection += increment;
            _consumed = 0;
            return increment;
        }

        private void ThrowIfAborted()
        {
            if (_aborted) throw new ObjectDisposedException(nameof(Http2ReceiveFlowControl));
        }
    }
}
