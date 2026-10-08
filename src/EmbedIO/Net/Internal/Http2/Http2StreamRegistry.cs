using System;
using System.Collections.Generic;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2StreamState
    {
        internal Http2StreamState(int id, bool remoteEnded, Http2RequestHeaders headers)
        { Id = id; RemoteEnded = remoteEnded; RequestHeaders = headers; }
        public Http2RequestHeaders RequestHeaders { get; }
        public int Id { get; }
        internal volatile bool RemoteEnded;
        internal volatile bool LocalEnded;
        internal volatile bool Reset;
    }

    // This server-side registry currently owns client-initiated streams only.
    // Closed IDs are represented by the high-water mark, not retained objects.
    // RFC 9113 permits minimal processing/discard for all closed streams; callers
    // must still decode their HPACK blocks and account for discarded DATA bytes.
    internal sealed class Http2StreamRegistry
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, Http2StreamState> _active = new();
        private readonly int _maximum;
        private int _highest;
        private bool _stopped;
        internal Http2StreamRegistry(int maximum = 128)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
            _maximum = maximum;
        }
        public int ActiveCount { get { lock (_sync) return _active.Count; } }
        public int LastStreamId { get { lock (_sync) return _highest; } }

        internal Http2StreamState? Receive(Http2Frame frame)
        {
            lock (_sync)
            {
                if (_stopped) throw new ObjectDisposedException(nameof(Http2StreamRegistry));
                frame.ValidateShape();
                if (frame.Type == 5) throw new Http2ProtocolException(1, "Clients cannot send PUSH_PROMISE.");
                if (frame.Type == 2)
                {
                    if ((Http2PeerSettings.ReadUInt32(frame.Payload, 0) & 0x7fffffff) == frame.StreamId)
                        throw new Http2ProtocolException(1, "Stream depends on itself.", frame.StreamId);
                    return null;
                }
                if (frame.Type == 1 || frame.Type == 9)
                {
                    var block = frame.HeaderBlock;
                    if (block == null) return null;
                    if ((block.StreamId & 1) == 0) throw new Http2ProtocolException(1, "Client stream ID must be odd.");
                    if (!_active.TryGetValue(block.StreamId, out var stream))
                    {
                        if (block.StreamId <= _highest) return null;
                        _highest = block.StreamId;
                        if (_active.Count >= _maximum) throw new Http2ProtocolException(7, "Concurrent stream limit exceeded.", block.StreamId);
                        if (block.StreamError != 0) throw new Http2ProtocolException(block.StreamError, "Invalid stream headers.", block.StreamId);
                        stream = new Http2StreamState(block.StreamId, block.EndStream, Http2RequestHeaders.Parse(block));
                        _active.Add(block.StreamId, stream);
                        return stream;
                    }
                    if (stream.RemoteEnded) throw new Http2ProtocolException(5, "Request stream already ended.", stream.Id);
                    if (block.StreamError != 0) throw new Http2ProtocolException(block.StreamError, "Invalid stream headers.", stream.Id);
                    if (!block.EndStream) throw new Http2ProtocolException(1, "Request trailers must end the stream.", stream.Id);
                    Http2RequestHeaders.ValidateTrailers(block);
                    stream.RemoteEnded = true;
                    Retire(stream);
                    return stream;
                }
                if (frame.Type != 0 && frame.Type != 3 && frame.Type != 8) return null;
                if (frame.StreamId == 0) return null; // Connection WINDOW_UPDATE.
                if (!_active.TryGetValue(frame.StreamId, out var current))
                {
                    if ((frame.StreamId & 1) == 0 || frame.StreamId > _highest)
                        throw new Http2ProtocolException(1, "Frame received on an idle stream.");
                    return null;
                }
                if (frame.Type == 3)
                {
                    current.Reset = true;
                    _active.Remove(current.Id);
                }
                else if (frame.Type == 0)
                {
                    if (current.RemoteEnded) throw new Http2ProtocolException(5, "DATA after request end.", current.Id);
                    if ((frame.Flags & 1) != 0) { current.RemoteEnded = true; Retire(current); }
                }
                return current;
            }
        }

        internal void EndLocal(int id)
        {
            lock (_sync)
            {
                if (!_active.TryGetValue(id, out var stream)) return;
                stream.LocalEnded = true;
                Retire(stream);
            }
        }

        internal void Reset(int id)
        {
            lock (_sync)
            {
                if (!_active.TryGetValue(id, out var stream)) return;
                stream.Reset = true;
                _active.Remove(id);
            }
        }

        internal void Abort()
        {
            lock (_sync)
            {
                _stopped = true;
                foreach (var stream in _active.Values) stream.Reset = true;
                _active.Clear();
            }
        }

        private void Retire(Http2StreamState stream)
        {
            if (stream.LocalEnded && stream.RemoteEnded) _active.Remove(stream.Id);
        }
    }
}
