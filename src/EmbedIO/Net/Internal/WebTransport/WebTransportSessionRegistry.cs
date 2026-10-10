using System;
using System.Collections.Generic;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    internal enum WebTransportAdmission
    {
        Accepted,
        // Section 5.1: a second session without negotiated flow control, or a
        // session beyond the configured bound. Reset the CONNECT stream with
        // H3_REQUEST_REJECTED or answer 429 (section 5.2); the caller chooses.
        TooManySessions,
        // Section 3.1 requirements are not met for this connection.
        NotNegotiated,
    }

    // Connection-wide bounds. Buffers hold streams and datagrams that name a
    // session the connection has not seen yet (section 4.6).
    internal sealed class WebTransportRegistryLimits
    {
        public int MaximumSessions { get; set; } = 8;
        public int MaximumBufferedStreams { get; set; } = 16;
        public int MaximumBufferedDatagrams { get; set; } = 32;
        public int MaximumBufferedDatagramBytes { get; set; } = 65536;
        public int MaximumTrackedRequestRanges { get; set; } = 128;
        public WebTransportSessionLimits Session { get; } = new();
    }

    // Per-connection association of streams and datagrams with sessions. The
    // transport reports negotiated settings, exact requests classified as
    // non-WebTransport, each WebTransport stream header and each datagram; the
    // registry answers with the session, a reset code or a buffer decision.
    internal sealed class WebTransportSessionRegistry
    {
        private readonly object _gate = new();
        private readonly WebTransportSettings _local;
        private readonly WebTransportSettings _peer;
        private readonly WebTransportRegistryLimits _limits;
        private readonly WebTransportStreamHandler _streamHandler;
        private readonly WebTransportDatagramHandler _datagramHandler;
        private readonly Dictionary<long, WebTransportSession> _sessions = new();
        private readonly Dictionary<long, Queue<WebTransportStreamHandle>> _bufferedStreams = new();
        private readonly Dictionary<long, Queue<byte[]>> _bufferedDatagrams = new();
        private int _bufferedStreamCount;
        private int _bufferedDatagramCount;
        private int _bufferedDatagramBytes;
        private readonly List<RequestRange> _unavailable = new();
        private readonly struct RequestRange
        {
            internal readonly long First;
            internal readonly long Last;
            internal RequestRange(long first, long last) { First = first; Last = last; }
        }
        private int FindRange(long ordinal)
        {
            var low = 0; var high = _unavailable.Count;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_unavailable[middle].Last < ordinal) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        private bool IsUnavailable(long streamId)
        {
            var ordinal = streamId >> 2;
            var index = FindRange(ordinal);
            return index < _unavailable.Count && _unavailable[index].First <= ordinal;
        }
        private void MarkUnavailable(long streamId)
        {
            var ordinal = streamId >> 2;
            var index = FindRange(ordinal);
            if (index < _unavailable.Count && _unavailable[index].First <= ordinal) return;
            var left = index > 0 && _unavailable[index - 1].Last == ordinal - 1;
            var right = index < _unavailable.Count && _unavailable[index].First == ordinal + 1;
            if (left && right)
            {
                _unavailable[index - 1] = new RequestRange(_unavailable[index - 1].First, _unavailable[index].Last);
                _unavailable.RemoveAt(index);
            }
            else if (left) _unavailable[index - 1] = new RequestRange(_unavailable[index - 1].First, ordinal);
            else if (right) _unavailable[index] = new RequestRange(ordinal, _unavailable[index].Last);
            else
            {
                if (_unavailable.Count >= _limits.MaximumTrackedRequestRanges)
                    throw new Http3ProtocolException(WebTransportProtocol.H3ExcessiveLoad, "Too many disjoint classified request ranges.");
                _unavailable.Insert(index, new RequestRange(ordinal, ordinal));
            }
        }
        private bool _shutdown;

        internal WebTransportSessionRegistry(WebTransportSettings local, WebTransportSettings peer, bool transportParametersMet,
            WebTransportRegistryLimits limits, WebTransportStreamHandler streamHandler, WebTransportDatagramHandler datagramHandler)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            if (limits.MaximumTrackedRequestRanges <= 0) throw new ArgumentOutOfRangeException(nameof(limits));
            _streamHandler = streamHandler ?? throw new ArgumentNullException(nameof(streamHandler));
            _datagramHandler = datagramHandler ?? throw new ArgumentNullException(nameof(datagramHandler));
            Negotiated = local.Enabled && peer.SettingsRequirementsMet && transportParametersMet;
            FlowControlEnabled = WebTransportSettings.FlowControlEnabled(local, peer);
        }

        public bool Negotiated { get; }
        public bool FlowControlEnabled { get; }
        public int ActiveSessionCount { get { lock (_gate) return _sessions.Count; } }
        public int BufferedStreamCount { get { lock (_gate) return _bufferedStreamCount; } }
        public int BufferedDatagramCount { get { lock (_gate) return _bufferedDatagramCount; } }

        // Call only after this exact request is classified as non-WebTransport
        // or its CONNECT is rejected. Seeing a higher request says nothing about
        // an unseen lower CONNECT; HTTP/3 streams can arrive out of order.
        internal void NoteRequestStream(long streamId)
        {
            if (!WebTransportProtocol.IsSessionId(streamId)) throw new ArgumentOutOfRangeException(nameof(streamId));
            Queue<WebTransportStreamHandle>? streams = null;
            lock (_gate)
            {
                if (_sessions.ContainsKey(streamId)) throw new InvalidOperationException("An active WebTransport session owns this request.");
                MarkUnavailable(streamId);
                if (_bufferedStreams.TryGetValue(streamId, out streams))
                { _bufferedStreams.Remove(streamId); _bufferedStreamCount -= streams.Count; }
                if (_bufferedDatagrams.TryGetValue(streamId, out var datagrams))
                {
                    _bufferedDatagrams.Remove(streamId); _bufferedDatagramCount -= datagrams.Count;
                    foreach (var datagram in datagrams) _bufferedDatagramBytes -= datagram.Length;
                }
            }
            if (streams != null) foreach (var stream in streams) stream.Abort(WebTransportProtocol.SessionGone);
        }

        // An extended CONNECT with :protocol=webtransport-h3 arrived on connectStreamId.
        internal WebTransportAdmission TryCreateSession(long connectStreamId, out WebTransportSession? session)
        {
            if (!WebTransportProtocol.IsSessionId(connectStreamId)) throw new ArgumentOutOfRangeException(nameof(connectStreamId));
            session = null;
            Queue<WebTransportStreamHandle>? streams = null;
            Queue<byte[]>? datagrams = null;
            lock (_gate)
            {
                if (!Negotiated || _shutdown) return WebTransportAdmission.NotNegotiated;
                if (IsUnavailable(connectStreamId)) throw new InvalidOperationException("This request was already classified or its session ended.");
                if (_sessions.ContainsKey(connectStreamId)) throw new InvalidOperationException("A session already uses this CONNECT stream.");
                var bound = FlowControlEnabled ? _limits.MaximumSessions : 1;
                if (_sessions.Count >= bound) return WebTransportAdmission.TooManySessions;
                session = new WebTransportSession(connectStreamId, FlowControlEnabled, _local, _peer, _limits.Session, _streamHandler, _datagramHandler);
                _sessions.Add(connectStreamId, session);
                if (_bufferedStreams.TryGetValue(connectStreamId, out streams))
                { _bufferedStreams.Remove(connectStreamId); _bufferedStreamCount -= streams.Count; }
                if (_bufferedDatagrams.TryGetValue(connectStreamId, out datagrams))
                {
                    _bufferedDatagrams.Remove(connectStreamId);
                    _bufferedDatagramCount -= datagrams.Count;
                    foreach (var datagram in datagrams) _bufferedDatagramBytes -= datagram.Length;
                }
            }
            // The session is still pending, so these land in its own bounded buffers.
            if (streams != null) foreach (var stream in streams) session.AttachIncomingStream(stream);
            if (datagrams != null) foreach (var datagram in datagrams) session.AcceptDatagram(datagram, 0, datagram.Length);
            return WebTransportAdmission.Accepted;
        }

        // The session ended in any way; forget it. Later streams naming it are
        // reset with WT_SESSION_GONE because this exact identifier is recorded.
        internal void SessionEnded(WebTransportSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session.Id, out var owned)) return;
                if (!ReferenceEquals(owned, session)) throw new InvalidOperationException("Another session owns this identifier.");
                MarkUnavailable(session.Id);
                _sessions.Remove(session.Id);
            }
        }

        internal bool TryGetSession(long sessionId, out WebTransportSession? session)
        {
            lock (_gate) return _sessions.TryGetValue(sessionId, out session);
        }

        // A stream whose header named sessionId. Returns false when the stream was
        // reset; buffered streams return true and are delivered later.
        internal bool RouteIncomingStream(long sessionId, WebTransportStreamHandle stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!WebTransportProtocol.IsSessionId(sessionId)) throw new Http3ProtocolException(WebTransportProtocol.H3IdError, "WebTransport session identifier is not a client-initiated bidirectional stream.");
            WebTransportSession? session = null;
            long? reset = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var found)) session = found;
                else if (_shutdown || IsUnavailable(sessionId)) reset = WebTransportProtocol.SessionGone;
                else if (_bufferedStreamCount >= _limits.MaximumBufferedStreams) reset = WebTransportProtocol.BufferedStreamRejected;
                else
                {
                    if (!_bufferedStreams.TryGetValue(sessionId, out var queue)) _bufferedStreams.Add(sessionId, queue = new Queue<WebTransportStreamHandle>());
                    queue.Enqueue(stream);
                    _bufferedStreamCount++;
                    return true;
                }
            }
            if (reset.HasValue) { stream.Abort(reset.Value); return false; }
            return session != null && session.AttachIncomingStream(stream);
        }

        // A datagram whose quarter stream identifier named sessionId. Returns false
        // when the datagram was dropped.
        internal bool RouteDatagram(long sessionId, byte[] bytes, int offset, int count)
        {
            WebTransportProtocol.ValidateBuffer(bytes, offset, count);
            if (!WebTransportProtocol.IsSessionId(sessionId)) throw new Http3ProtocolException(WebTransportProtocol.H3IdError, "HTTP datagram names a stream that is not a client-initiated bidirectional stream.");
            WebTransportSession? session = null;
            lock (_gate)
            {
                if (_sessions.TryGetValue(sessionId, out var found)) session = found;
                else if (_shutdown || IsUnavailable(sessionId)) return false;
                else if (_bufferedDatagramCount >= _limits.MaximumBufferedDatagrams || count > _limits.MaximumBufferedDatagramBytes - _bufferedDatagramBytes) return false;
                else
                {
                    if (!_bufferedDatagrams.TryGetValue(sessionId, out var queue)) _bufferedDatagrams.Add(sessionId, queue = new Queue<byte[]>());
                    var copy = new byte[count];
                    Array.Copy(bytes, offset, copy, 0, count);
                    queue.Enqueue(copy);
                    _bufferedDatagramCount++;
                    _bufferedDatagramBytes += count;
                    return true;
                }
            }
            return session != null && session.AcceptDatagram(bytes, offset, count);
        }

        // The connection is ending: terminate every session, reset buffered
        // streams and drop buffered datagrams.
        internal void Shutdown()
        {
            List<WebTransportSession> sessions;
            List<WebTransportStreamHandle> streams = new();
            lock (_gate)
            {
                if (_shutdown) return;
                _shutdown = true;
                sessions = new List<WebTransportSession>(_sessions.Values);
                _sessions.Clear();
                foreach (var queue in _bufferedStreams.Values) streams.AddRange(queue);
                _bufferedStreams.Clear();
                _bufferedDatagrams.Clear();
                _bufferedStreamCount = 0;
                _bufferedDatagramCount = 0;
                _bufferedDatagramBytes = 0;
            }
            foreach (var session in sessions) session.Terminate(null);
            foreach (var stream in streams) stream.Abort(WebTransportProtocol.SessionGone);
        }
    }
}
