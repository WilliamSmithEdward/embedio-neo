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
        public WebTransportSessionLimits Session { get; } = new();
    }

    // Per-connection association of streams and datagrams with sessions. The
    // transport reports negotiated settings, every client request stream it
    // starts to process, each WebTransport stream header and each datagram; the
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
        private long _highestRequestStreamId = -1;
        private bool _shutdown;

        internal WebTransportSessionRegistry(WebTransportSettings local, WebTransportSettings peer, bool transportParametersMet,
            WebTransportRegistryLimits limits, WebTransportStreamHandler streamHandler, WebTransportDatagramHandler datagramHandler)
        {
            _local = local ?? throw new ArgumentNullException(nameof(local));
            _peer = peer ?? throw new ArgumentNullException(nameof(peer));
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
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

        // Every client-initiated bidirectional stream the connection starts to
        // process, WebTransport or not. A session identifier at or below this
        // point that has no live session belongs to a closed session or to an
        // ordinary request, so its streams are gone rather than early.
        internal void NoteRequestStream(long streamId)
        {
            if (!WebTransportProtocol.IsSessionId(streamId)) throw new ArgumentOutOfRangeException(nameof(streamId));
            lock (_gate) { if (streamId > _highestRequestStreamId) _highestRequestStreamId = streamId; }
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
                if (connectStreamId > _highestRequestStreamId) _highestRequestStreamId = connectStreamId;
                if (!Negotiated || _shutdown) return WebTransportAdmission.NotNegotiated;
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
        // reset with WT_SESSION_GONE because its identifier is already noted.
        internal void SessionEnded(WebTransportSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            lock (_gate) _sessions.Remove(session.Id);
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
                else if (_shutdown || sessionId <= _highestRequestStreamId) reset = WebTransportProtocol.SessionGone;
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
                else if (_shutdown || sessionId <= _highestRequestStreamId) return false;
                else if (_bufferedDatagramCount >= _limits.MaximumBufferedDatagrams || _bufferedDatagramBytes + count > _limits.MaximumBufferedDatagramBytes) return false;
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
