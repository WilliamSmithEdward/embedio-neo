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
            Id = id;
            RemoteEnded = remoteEnded;
            InitialEndStream = remoteEnded;
            RequestHeaders = headers;

            if (!HttpPriority.TryParse(headers.Headers["priority"] ?? "", out var priority))
                priority = new HttpPriority(3, false);

            SetPriority(priority);
        }

        private int _priority;

        public HttpPriority Priority
        {
            get
            {
                var value = Volatile.Read(ref _priority);
                return new HttpPriority(value & 7, (value & 8) != 0);
            }
        }

        internal void SetPriority(HttpPriority value) =>
            Volatile.Write(ref _priority, value.Urgency | (value.Incremental ? 8 : 0));

        public Http2RequestHeaders RequestHeaders { get; }
        public bool InitialEndStream { get; }
        public int Id { get; }

        internal volatile bool RemoteEnded;
        internal volatile bool LocalEnded;
        internal volatile bool Reset;
        internal bool Tunnel;
    }

    // This server-side registry currently owns client-initiated streams only.
    // Closed IDs are represented by the high-water mark, not retained objects.
    // RFC 9113 permits minimal processing/discard for all closed streams; callers
    // must still decode their HPACK blocks and account for discarded DATA bytes.
    internal sealed class Http2StreamRegistry
    {
        // A stream leaves _active as soon as it is reset or both directions end,
        // but its application can keep running. Admission counts those
        // applications too (RFC 9113 5.1.2), so resetting streams cannot grow
        // concurrent handler work past the advertised limit (CVE-2023-44487).
        // A client that keeps opening streams the server must refuse, more than
        // one full limit's worth within this window, gets GOAWAY
        // ENHANCE_YOUR_CALM (RFC 9113 5.4.1, 7). A client that waits for
        // refused streams to be retried stays well below it.
        private static readonly long RefusalWindow = System.Diagnostics.Stopwatch.Frequency;
        private const uint RefusedStream = 7, EnhanceYourCalm = 11;
        private readonly object _sync = new();
        private readonly Dictionary<int, Http2StreamState> _active = [];
        private readonly Dictionary<int, HttpPriority> _priorities = [];
        private readonly int _maximum;
        private int _highest;
        private bool _stopped;
        private long _refusalWindowStart;
        private int _refusals;

        internal bool ExtendedConnectEnabled { get; set; }

        // Applications still running, including those of streams already
        // removed from _active. Supplied by the dispatcher that starts them.
        internal Func<int>? RunningApplications { get; set; }
        internal Func<long> Timestamp { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp;

        internal Http2StreamRegistry(int maximum = 128)
        {
            if (maximum < 1) throw new ArgumentOutOfRangeException(nameof(maximum));

            _maximum = maximum;
        }

        public int ActiveCount
        {
            get
            {
                lock (_sync) return _active.Count;
            }
        }

        public int PendingPriorityCount
        {
            get
            {
                lock (_sync) return _priorities.Count;
            }
        }

        public int LastStreamId
        {
            get
            {
                lock (_sync) return _highest;
            }
        }

        internal Http2StreamState? Receive(Http2Frame frame)
        {
            lock (_sync)
            {
                if (_stopped) throw new ObjectDisposedException(nameof(Http2StreamRegistry));

                frame.ValidateShape();

                if (frame.Type == 5) throw new Http2ProtocolException(1, "Clients cannot send PUSH_PROMISE.");

                if (frame.Type == 16) return UpdatePriority(frame);

                // The server advertises NO_RFC7540_PRIORITIES. Frame shape still
                // matters, but deprecated dependency/weight semantics are ignored.
                if (frame.Type == 2) return null;

                if (frame.Type == 1 || frame.Type == 9)
                {
                    var block = frame.HeaderBlock;

                    if (block == null) return null;

                    if ((block.StreamId & 1) == 0) throw new Http2ProtocolException(1, "Client stream ID must be odd.");

                    if (!_active.TryGetValue(block.StreamId, out var stream))
                    {
                        if (block.StreamId <= _highest)
                        {
                            // Closed-stream trailers can be discarded after HPACK
                            // processing. A new request cannot reuse or open an
                            // identifier below the established high-water mark.
                            foreach (var field in block.Fields)
                                if (field.Name == ":method")
                                    throw new Http2ProtocolException(
                                        1,
                                        "A new request requires a higher stream identifier."
                                    );

                            return null;
                        }

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

                        if (Math.Max(_active.Count, RunningApplications?.Invoke() ?? 0) + _priorities.Count >= _maximum)
                            throw Refuse(block.StreamId);

                        if (block.StreamError != 0)
                            throw new Http2ProtocolException(
                                block.StreamError,
                                "Invalid stream headers.",
                                block.StreamId
                            );

                        stream = new Http2StreamState(
                            block.StreamId,
                            block.EndStream,
                            Http2RequestHeaders.Parse(block, ExtendedConnectEnabled)
                        );

                        if (prioritized) stream.SetPriority(priority);

                        _active.Add(block.StreamId, stream);
                        return stream;
                    }

                    if (stream.RemoteEnded)
                        throw new Http2ProtocolException(5, "Request stream already ended.", stream.Id);

                    if (stream.Tunnel)
                        throw new Http2ProtocolException(
                            1,
                            "HEADERS are not permitted on a connected stream.",
                            stream.Id
                        );

                    if (block.StreamError != 0)
                        throw new Http2ProtocolException(block.StreamError, "Invalid stream headers.", stream.Id);

                    if (!block.EndStream)
                        throw new Http2ProtocolException(1, "Request trailers must end the stream.", stream.Id);

                    Http2RequestHeaders.ValidateTrailers(block);
                    stream.RemoteEnded = true;
                    Retire(stream);
                    return stream;
                }

                if (frame.Type != 0 && frame.Type != 3 && frame.Type != 8) return null;

                if (frame.StreamId == 0)
                    return null; // Connection WINDOW_UPDATE.

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

                    if ((frame.Flags & 1) != 0)
                    {
                        current.RemoteEnded = true;
                        Retire(current);
                    }
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

            if (
                !HttpPriority.TryParse(
                    Encoding.ASCII.GetString(frame.Payload, 4, frame.Payload.Length - 4),
                    out var priority
                )
            )
                throw new Http2ProtocolException(1, "Malformed priority dictionary.");

            if (_active.TryGetValue(id, out var stream))
            {
                if (stream.LocalEnded) return null;

                stream.SetPriority(priority);
                return stream;
            }

            if (id <= _highest) return null;

            if (!_priorities.ContainsKey(id) && _active.Count + _priorities.Count >= _maximum)
                throw new Http2ProtocolException(
                    1,
                    "Idle priority targets and active streams exceed the concurrency limit."
                );

            _priorities[id] = priority;
            return null;
        }

        internal void EnterTunnel(int id)
        {
            lock (_sync)
            {
                if (!_active.TryGetValue(id, out var stream) || stream.Reset)
                    throw new Http2ProtocolException(5, "Tunnel stream is no longer available.", id);

                stream.Tunnel = true;
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
                _priorities.Clear();
            }
        }

        // Called under _sync for each stream refused at the concurrency limit.
        private Http2ProtocolException Refuse(int streamId)
        {
            var now = Timestamp();

            if (_refusals == 0 || now - _refusalWindowStart > RefusalWindow)
            {
                _refusalWindowStart = now;
                _refusals = 0;
            }

            return ++_refusals > _maximum
                ? new Http2ProtocolException(EnhanceYourCalm, "Too many streams refused at the concurrency limit.")
                : new Http2ProtocolException(RefusedStream, "Concurrent stream limit exceeded.", streamId);
        }

        private void Retire(Http2StreamState stream)
        {
            if (stream.LocalEnded && stream.RemoteEnded) _active.Remove(stream.Id);
        }
    }
}
