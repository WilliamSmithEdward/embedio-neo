using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2StreamState
    {
        internal Http2StreamState(int id, bool remoteEnded, Http2RequestHeaders headers)
        {
            Id = id; RemoteEnded = remoteEnded; InitialEndStream = remoteEnded; RequestHeaders = headers;
            HttpPriority.TryParse(headers.Headers["priority"] ?? "", out var priority);
            SetPriority(priority);
        }
        private int _priority;
        public HttpPriority Priority
        { get { var value = Volatile.Read(ref _priority); return new HttpPriority(value & 7, (value & 8) != 0); } }
        internal void SetPriority(HttpPriority value) => Volatile.Write(ref _priority, value.Urgency | (value.Incremental ? 8 : 0));
        public Http2RequestHeaders RequestHeaders { get; }
        public bool InitialEndStream { get; }
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
        private readonly Dictionary<int, HttpPriority> _priorities = new();
        private readonly int _maximum;
        private int _highest;
        private bool _stopped;
        internal bool ExtendedConnectEnabled { get; set; }
        internal Http2StreamRegistry(int maximum = 128)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));
            _maximum = maximum;
        }
        public int ActiveCount { get { lock (_sync) return _active.Count; } }
        public int PendingPriorityCount { get { lock (_sync) return _priorities.Count; } }
        public int LastStreamId { get { lock (_sync) return _highest; } }

        internal Http2StreamState? Receive(Http2Frame frame)
        {
            lock (_sync)
            {
                if (_stopped) throw new ObjectDisposedException(nameof(Http2StreamRegistry));
                frame.ValidateShape();
                if (frame.Type == 5) throw new Http2ProtocolException(1, "Clients cannot send PUSH_PROMISE.");
                if (frame.Type == 16) return UpdatePriority(frame);
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
                        var prioritized = _priorities.TryGetValue(block.StreamId, out var priority);
                        // Opening a higher client stream implicitly closes lower
                        // idle IDs, including their retained advisory signals.
                        if (_priorities.Count != 0)
                        {
                            var closed = new List<int>();
                            foreach (var id in _priorities.Keys) if (id <= _highest) closed.Add(id);
                            foreach (var id in closed) _priorities.Remove(id);
                        }
                        if (_active.Count + _priorities.Count >= _maximum) throw new Http2ProtocolException(7, "Concurrent stream limit exceeded.", block.StreamId);
                        if (block.StreamError != 0) throw new Http2ProtocolException(block.StreamError, "Invalid stream headers.", block.StreamId);
                        stream = new Http2StreamState(block.StreamId, block.EndStream, Http2RequestHeaders.Parse(block, ExtendedConnectEnabled));
                        if (prioritized) stream.SetPriority(priority);
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

        private Http2StreamState? UpdatePriority(Http2Frame frame)
        {
            var id = (int)(Http2PeerSettings.ReadUInt32(frame.Payload, 0) & 0x7fffffff);
            // This server has never opened a push stream: an even target is idle.
            if (id == 0 || (id & 1) == 0) throw new Http2ProtocolException(1, "Invalid priority-update target.");
            for (var i = 4; i < frame.Payload.Length; i++)
                if (frame.Payload[i] > 127) throw new Http2ProtocolException(1, "Priority field is not ASCII.");
            if (!HttpPriority.TryParse(Encoding.ASCII.GetString(frame.Payload, 4, frame.Payload.Length - 4), out var priority))
                throw new Http2ProtocolException(1, "Malformed priority dictionary.");
            if (_active.TryGetValue(id, out var stream))
            { if (stream.LocalEnded) return null; stream.SetPriority(priority); return stream; }
            if (id <= _highest) return null;
            if (!_priorities.ContainsKey(id) && _active.Count + _priorities.Count >= _maximum)
                throw new Http2ProtocolException(1, "Idle priority targets and active streams exceed the concurrency limit.");
            _priorities[id] = priority;
            return null;
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
                _priorities.Clear();
            }
        }

        private void Retire(Http2StreamState stream)
        {
            if (stream.LocalEnded && stream.RemoteEnded) _active.Remove(stream.Id);
        }
    }
}
