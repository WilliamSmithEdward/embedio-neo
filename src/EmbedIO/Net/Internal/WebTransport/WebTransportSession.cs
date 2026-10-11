using System;
using System.Collections.Generic;

namespace EmbedIO.Net.Internal.WebTransport
{
    internal enum WebTransportSessionState
    {
        // The CONNECT request arrived; the 2xx response is not sent yet.
        Pending,
        Established,
        Closed,
    }

    internal enum WebTransportCapsuleEffect
    {
        None,
        SessionClosed,
        DrainRequested,
        CreditIncreased,
    }

    internal delegate void WebTransportStreamHandler(WebTransportSession session, WebTransportStreamHandle stream);
    internal delegate void WebTransportDatagramHandler(WebTransportSession session, byte[] bytes, int offset, int count);

    // Bounds for one session's pre-establishment buffers and its advertised
    // receive windows. Windows apply only when flow control is negotiated.
    internal sealed class WebTransportSessionLimits
    {
        public int MaximumBufferedStreams { get; set; } = 8;
        public int MaximumBufferedDatagrams { get; set; } = 16;
        public int MaximumBufferedDatagramBytes { get; set; } = 16384;
    }

    // One WebTransport session: the CONNECT stream's identity, the streams and
    // datagrams associated with it, the capsules exchanged on the CONNECT stream
    // and the session-level flow control of draft-ietf-webtrans-http3-16
    // section 5. The transport owns every QUIC stream; this class only decides
    // which streams belong here, when they must be reset and which capsules to
    // write next. Handlers run outside the session lock.
    internal sealed class WebTransportSession
    {
        private readonly object _gate = new();
        private readonly WebTransportSessionLimits _limits;
        private readonly WebTransportStreamHandler _streamHandler;
        private readonly WebTransportDatagramHandler _datagramHandler;
        private readonly Dictionary<long, WebTransportStreamHandle> _streams = new();
        private readonly Queue<WebTransportStreamHandle> _bufferedStreams = new();
        private readonly Queue<byte[]> _bufferedDatagrams = new();
        private readonly Queue<byte[]> _outgoingCapsules = new();
        private readonly long _initialLocalMaxData;
        private readonly long _initialLocalMaxStreamsBidirectional;
        private readonly long _initialLocalMaxStreamsUnidirectional;
        private int _bufferedDatagramBytes;
        private long _localMaxData;
        private long _localMaxStreamsBidirectional;
        private long _localMaxStreamsUnidirectional;
        private long _peerMaxData;
        private long _peerMaxStreamsBidirectional;
        private long _peerMaxStreamsUnidirectional;
        private long _incomingData;
        private long _consumedData;
        private long _outgoingData;
        private long _incomingStreamsBidirectional;
        private long _incomingStreamsUnidirectional;
        private long _finishedIncomingBidirectional;
        private long _finishedIncomingUnidirectional;
        private long _outgoingStreamsBidirectional;
        private long _outgoingStreamsUnidirectional;
        private long _blockedStreamsBidirectionalAt = -1;
        private long _blockedStreamsUnidirectionalAt = -1;
        private long _blockedDataAt = -1;
        private bool _drainSent;

        internal WebTransportSession(long id, bool flowControlEnabled, WebTransportSettings local, WebTransportSettings peer,
            WebTransportSessionLimits limits, WebTransportStreamHandler streamHandler, WebTransportDatagramHandler datagramHandler)
        {
            if (!WebTransportProtocol.IsSessionId(id)) throw new ArgumentOutOfRangeException(nameof(id));
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (peer == null) throw new ArgumentNullException(nameof(peer));
            Id = id;
            FlowControlEnabled = flowControlEnabled;
            _limits = limits ?? throw new ArgumentNullException(nameof(limits));
            _streamHandler = streamHandler ?? throw new ArgumentNullException(nameof(streamHandler));
            _datagramHandler = datagramHandler ?? throw new ArgumentNullException(nameof(datagramHandler));
            _initialLocalMaxData = _localMaxData = local.InitialMaxData;
            _initialLocalMaxStreamsBidirectional = _localMaxStreamsBidirectional = local.InitialMaxStreamsBidirectional;
            _initialLocalMaxStreamsUnidirectional = _localMaxStreamsUnidirectional = local.InitialMaxStreamsUnidirectional;
            _peerMaxData = peer.InitialMaxData;
            _peerMaxStreamsBidirectional = peer.InitialMaxStreamsBidirectional;
            _peerMaxStreamsUnidirectional = peer.InitialMaxStreamsUnidirectional;
        }

        public long Id { get; }
        public bool FlowControlEnabled { get; }
        public WebTransportSessionState State { get; private set; }
        public bool DrainRequested { get; private set; }
        public bool ClosedByPeer { get; private set; }
        public uint CloseErrorCode { get; private set; }
        public string CloseMessage { get; private set; } = string.Empty;
        // Set when this core terminated the session itself: the CONNECT stream must
        // be reset with this HTTP/3 error code.
        public long? FaultErrorCode { get; private set; }
        // Set after a local WT_CLOSE_SESSION is queued: the sender must finish the
        // CONNECT stream right after writing it (section 6).
        public bool OutputMustFinish { get; private set; }
        public int ActiveStreamCount { get { lock (_gate) return _streams.Count; } }
        public int BufferedStreamCount { get { lock (_gate) return _bufferedStreams.Count; } }
        public int BufferedDatagramCount { get { lock (_gate) return _bufferedDatagrams.Count; } }
        public int PendingCapsuleCount { get { lock (_gate) return _outgoingCapsules.Count; } }
        public long PeerMaxData { get { lock (_gate) return _peerMaxData; } }
        public long PeerMaxStreamsBidirectional { get { lock (_gate) return _peerMaxStreamsBidirectional; } }
        public long PeerMaxStreamsUnidirectional { get { lock (_gate) return _peerMaxStreamsUnidirectional; } }
        public long LocalMaxData { get { lock (_gate) return _localMaxData; } }
        public long LocalMaxStreamsBidirectional { get { lock (_gate) return _localMaxStreamsBidirectional; } }
        public long LocalMaxStreamsUnidirectional { get { lock (_gate) return _localMaxStreamsUnidirectional; } }

        // Section 3.2: the server's session exists once it sends a 2xx response.
        // Streams and datagrams that arrived first are delivered now, in order.
        internal void Establish()
        {
            List<WebTransportStreamHandle> streams;
            List<byte[]> datagrams;
            lock (_gate)
            {
                if (State != WebTransportSessionState.Pending) throw new InvalidOperationException("The session is not pending.");
                State = WebTransportSessionState.Established;
                streams = new List<WebTransportStreamHandle>(_bufferedStreams);
                datagrams = new List<byte[]>(_bufferedDatagrams);
                _bufferedStreams.Clear();
                _bufferedDatagrams.Clear();
                _bufferedDatagramBytes = 0;
            }
            foreach (var stream in streams) AttachIncomingStream(stream);
            foreach (var datagram in datagrams) _datagramHandler(this, datagram, 0, datagram.Length);
        }

        // An incoming stream whose header named this session. Returns false when
        // the stream was reset instead of delivered.
        internal bool AttachIncomingStream(WebTransportStreamHandle stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            long? reset = null;
            List<WebTransportStreamHandle>? aborted = null;
            lock (_gate)
            {
                switch (State)
                {
                    case WebTransportSessionState.Closed:
                        reset = WebTransportProtocol.SessionGone;
                        break;
                    case WebTransportSessionState.Pending:
                        if (_bufferedStreams.Count >= _limits.MaximumBufferedStreams) reset = WebTransportProtocol.BufferedStreamRejected;
                        else { _bufferedStreams.Enqueue(stream); return true; }
                        break;
                    default:
                        if (FlowControlEnabled)
                        {
                            var opened = stream.Bidirectional ? _incomingStreamsBidirectional : _incomingStreamsUnidirectional;
                            var limit = stream.Bidirectional ? _localMaxStreamsBidirectional : _localMaxStreamsUnidirectional;
                            if (opened >= limit)
                            {
                                aborted = TerminateCore(WebTransportProtocol.FlowControlError);
                                aborted.Add(stream);
                                break;
                            }
                        }
                        if (stream.Bidirectional) _incomingStreamsBidirectional++; else _incomingStreamsUnidirectional++;
                        _streams[stream.Id] = stream;
                        break;
                }
            }
            if (aborted != null)
            {
                AbortAll(aborted, WebTransportProtocol.SessionGone);
                throw new WebTransportException(WebTransportProtocol.FlowControlError, "Peer exceeded the advertised WebTransport stream limit.");
            }
            if (reset.HasValue) { stream.Abort(reset.Value); return false; }
            _streamHandler(this, stream);
            return true;
        }

        // Section 5.6.2: open only within the peer's limit, otherwise queue one
        // WT_STREAMS_BLOCKED for the current limit and refuse.
        internal bool TryOpenOutgoingStream(bool bidirectional)
        {
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return false;
                if (FlowControlEnabled)
                {
                    var opened = bidirectional ? _outgoingStreamsBidirectional : _outgoingStreamsUnidirectional;
                    var limit = bidirectional ? _peerMaxStreamsBidirectional : _peerMaxStreamsUnidirectional;
                    if (opened >= limit)
                    {
                        ref var blockedAt = ref (bidirectional ? ref _blockedStreamsBidirectionalAt : ref _blockedStreamsUnidirectionalAt);
                        if (blockedAt != limit)
                        {
                            blockedAt = limit;
                            _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeLimit(
                                bidirectional ? WebTransportProtocol.StreamsBlockedBidirectionalCapsule : WebTransportProtocol.StreamsBlockedUnidirectionalCapsule, limit));
                        }
                        return false;
                    }
                }
                if (bidirectional) _outgoingStreamsBidirectional++; else _outgoingStreamsUnidirectional++;
                return true;
            }
        }

        // Called after TryOpenOutgoingStream succeeded and the transport opened the stream.
        internal void RegisterOutgoingStream(WebTransportStreamHandle stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) { stream.Abort(WebTransportProtocol.SessionGone); return; }
                _streams[stream.Id] = stream;
            }
        }

        // Section 5.6.4: stream body bytes received across the session.
        internal void RecordIncomingData(long count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            List<WebTransportStreamHandle>? aborted = null;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return;
                _incomingData += count;
                if (FlowControlEnabled && _incomingData > _localMaxData) aborted = TerminateCore(WebTransportProtocol.FlowControlError);
            }
            if (aborted == null) return;
            AbortAll(aborted, WebTransportProtocol.SessionGone);
            throw new WebTransportException(WebTransportProtocol.FlowControlError, "Peer exceeded the advertised WebTransport data limit.");
        }

        // The application consumed received bytes; advertise more credit once half
        // of the initial window has been released (section 5.6, MAX before BLOCKED).
        internal void ReleaseIncomingData(long count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed || !FlowControlEnabled) return;
                _consumedData += count;
                var target = _consumedData + _initialLocalMaxData;
                if (target - _localMaxData < Math.Max(1, _initialLocalMaxData / 2)) return;
                _localMaxData = Math.Min(target, long.MaxValue >> 2);
                _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeLimit(WebTransportProtocol.MaxDataCapsule, _localMaxData));
            }
        }

        // Section 5.6.4 from the sending side: reserve credit or queue one
        // WT_DATA_BLOCKED for the current limit.
        internal bool TryReserveOutgoingData(long count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return false;
                if (FlowControlEnabled && _outgoingData + count > _peerMaxData)
                {
                    if (_blockedDataAt != _peerMaxData)
                    {
                        _blockedDataAt = _peerMaxData;
                        _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeLimit(WebTransportProtocol.DataBlockedCapsule, _peerMaxData));
                    }
                    return false;
                }
                _outgoingData += count;
                return true;
            }
        }

        // A stream ended in either direction. Closed incoming streams still count
        // toward the cumulative limit, so new credit is advertised as they finish.
        internal void StreamFinished(WebTransportStreamHandle stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            lock (_gate)
            {
                if (!_streams.Remove(stream.Id) || !FlowControlEnabled || State == WebTransportSessionState.Closed) return;
                var incoming = (stream.Id & 1) == 0;
                if (!incoming) return;
                if (stream.Bidirectional)
                {
                    _finishedIncomingBidirectional++;
                    Advertise(ref _localMaxStreamsBidirectional, _finishedIncomingBidirectional, _initialLocalMaxStreamsBidirectional, WebTransportProtocol.MaxStreamsBidirectionalCapsule);
                }
                else
                {
                    _finishedIncomingUnidirectional++;
                    Advertise(ref _localMaxStreamsUnidirectional, _finishedIncomingUnidirectional, _initialLocalMaxStreamsUnidirectional, WebTransportProtocol.MaxStreamsUnidirectionalCapsule);
                }
            }
        }

        private void Advertise(ref long advertised, long finished, long initial, long capsuleType)
        {
            var target = Math.Min(finished + initial, WebTransportProtocol.MaximumStreamCount);
            if (target - advertised < Math.Max(1, initial / 2)) return;
            advertised = target;
            _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeLimit(capsuleType, advertised));
        }

        // A datagram whose quarter stream identifier named this session. Datagrams
        // for a closed session are dropped; datagrams before establishment are
        // copied into a bounded buffer and the newest are dropped past the bound.
        internal bool AcceptDatagram(byte[] bytes, int offset, int count)
        {
            WebTransportProtocol.ValidateBuffer(bytes, offset, count);
            lock (_gate)
            {
                switch (State)
                {
                    case WebTransportSessionState.Closed: return false;
                    case WebTransportSessionState.Pending:
                        if (_bufferedDatagrams.Count >= _limits.MaximumBufferedDatagrams || _bufferedDatagramBytes + count > _limits.MaximumBufferedDatagramBytes) return false;
                        var copy = new byte[count];
                        Array.Copy(bytes, offset, copy, 0, count);
                        _bufferedDatagrams.Enqueue(copy);
                        _bufferedDatagramBytes += count;
                        return true;
                }
            }
            _datagramHandler(this, bytes, offset, count);
            return true;
        }

        // A capsule read from the CONNECT stream. Flow control capsules are
        // ignored when flow control is not enabled (section 5.1).
        internal WebTransportCapsuleEffect ProcessCapsule(WebTransportCapsule capsule)
        {
            List<WebTransportStreamHandle>? aborted = null;
            WebTransportException? failure = null;
            var effect = WebTransportCapsuleEffect.None;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed)
                {
                    // Section 6: stream data after WT_CLOSE_SESSION resets the CONNECT stream.
                    if (ClosedByPeer) failure = new WebTransportException(WebTransportProtocol.H3MessageError, "Data after WT_CLOSE_SESSION.");
                }
                else
                {
                    try
                    {
                        switch (capsule.Type)
                        {
                            case WebTransportProtocol.CloseSessionCapsule:
                                CloseErrorCode = WebTransportCapsuleCodec.DecodeClose(capsule.Payload, out var message);
                                CloseMessage = message;
                                ClosedByPeer = true;
                                aborted = TerminateCore(null);
                                effect = WebTransportCapsuleEffect.SessionClosed;
                                break;
                            case WebTransportProtocol.DrainSessionCapsule:
                                WebTransportCapsuleCodec.ValidateDrain(capsule.Payload);
                                DrainRequested = true;
                                effect = WebTransportCapsuleEffect.DrainRequested;
                                break;
                            case WebTransportProtocol.ProhibitedMaxStreamDataCapsule:
                            case WebTransportProtocol.ProhibitedStreamDataBlockedCapsule:
                                if (!FlowControlEnabled) break;
                                throw new WebTransportException(WebTransportProtocol.FlowControlError, "Per-stream flow control capsules are prohibited over HTTP/3.");
                            case WebTransportProtocol.MaxDataCapsule:
                                effect = Raise(ref _peerMaxData, capsule, "WT_MAX_DATA did not increase the limit.");
                                break;
                            case WebTransportProtocol.MaxStreamsBidirectionalCapsule:
                                effect = Raise(ref _peerMaxStreamsBidirectional, capsule, "WT_MAX_STREAMS did not increase the limit.");
                                break;
                            case WebTransportProtocol.MaxStreamsUnidirectionalCapsule:
                                effect = Raise(ref _peerMaxStreamsUnidirectional, capsule, "WT_MAX_STREAMS did not increase the limit.");
                                break;
                            case WebTransportProtocol.DataBlockedCapsule:
                            case WebTransportProtocol.StreamsBlockedBidirectionalCapsule:
                            case WebTransportProtocol.StreamsBlockedUnidirectionalCapsule:
                                // Informational: validated for shape and bounds, then ignored.
                                if (FlowControlEnabled) WebTransportCapsuleCodec.DecodeLimit(capsule.Type, capsule.Payload);
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(capsule), "Not a WebTransport session capsule.");
                        }
                    }
                    catch (WebTransportException error)
                    {
                        failure = error;
                        aborted = TerminateCore(error.ErrorCode);
                    }
                }
            }
            if (aborted != null) AbortAll(aborted, WebTransportProtocol.SessionGone);
            if (failure != null) throw failure;
            return effect;
        }

        private WebTransportCapsuleEffect Raise(ref long limit, WebTransportCapsule capsule, string message)
        {
            var value = WebTransportCapsuleCodec.DecodeLimit(capsule.Type, capsule.Payload);
            if (!FlowControlEnabled) return WebTransportCapsuleEffect.None;
            if (value <= limit) throw new WebTransportException(WebTransportProtocol.FlowControlError, message);
            limit = value;
            return WebTransportCapsuleEffect.CreditIncreased;
        }

        // Section 6: a clean CONNECT stream end without a close capsule is a close
        // with code 0 and an empty message.
        internal void PeerFinished()
        {
            List<WebTransportStreamHandle>? aborted = null;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return;
                ClosedByPeer = true;
                aborted = TerminateCore(null);
            }
            AbortAll(aborted, WebTransportProtocol.SessionGone);
        }

        // The CONNECT stream was reset or the request failed before completion.
        internal void PeerAborted()
        {
            List<WebTransportStreamHandle>? aborted = null;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return;
                ClosedByPeer = true;
                aborted = TerminateCore(null);
            }
            AbortAll(aborted, WebTransportProtocol.SessionGone);
        }

        // Local close: queue WT_CLOSE_SESSION, require the CONNECT stream to finish
        // and reset every associated stream with WT_SESSION_GONE.
        internal bool Close(uint applicationErrorCode, string message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            List<WebTransportStreamHandle>? aborted;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return false;
                CloseErrorCode = applicationErrorCode;
                CloseMessage = WebTransportCapsuleCodec.TruncateCloseMessage(message);
                _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeClose(applicationErrorCode, CloseMessage));
                OutputMustFinish = true;
                aborted = TerminateCore(null);
            }
            AbortAll(aborted, WebTransportProtocol.SessionGone);
            return true;
        }

        // Section 4.7: ask the peer to finish; the session stays usable.
        internal bool Drain()
        {
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed || _drainSent) return false;
                _drainSent = true;
                _outgoingCapsules.Enqueue(WebTransportCapsuleCodec.EncodeDrain());
                return true;
            }
        }

        // Terminate without a close capsule, for connection shutdown or a fault
        // detected outside capsule processing. The CONNECT stream must be reset
        // with errorCode when one is given.
        internal void Terminate(long? errorCode)
        {
            List<WebTransportStreamHandle>? aborted;
            lock (_gate)
            {
                if (State == WebTransportSessionState.Closed) return;
                aborted = TerminateCore(errorCode);
            }
            AbortAll(aborted, WebTransportProtocol.SessionGone);
        }

        internal bool TryDequeueOutgoingCapsule(out byte[] capsule)
        {
            lock (_gate)
            {
                if (_outgoingCapsules.Count == 0) { capsule = Array.Empty<byte>(); return false; }
                capsule = _outgoingCapsules.Dequeue();
                return true;
            }
        }

        private List<WebTransportStreamHandle> TerminateCore(long? faultErrorCode)
        {
            State = WebTransportSessionState.Closed;
            if (faultErrorCode.HasValue) FaultErrorCode = faultErrorCode;
            var aborted = new List<WebTransportStreamHandle>(_streams.Count + _bufferedStreams.Count);
            aborted.AddRange(_streams.Values);
            aborted.AddRange(_bufferedStreams);
            _streams.Clear();
            _bufferedStreams.Clear();
            _bufferedDatagrams.Clear();
            _bufferedDatagramBytes = 0;
            return aborted;
        }

        private static void AbortAll(List<WebTransportStreamHandle>? streams, long errorCode)
        {
            if (streams == null) return;
            foreach (var stream in streams) stream.Abort(errorCode);
        }
    }
}
