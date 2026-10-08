using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    internal readonly struct Http3ControlEvent
    {
        internal Http3ControlEvent(long type, long identifier) { Type = type; Identifier = identifier; }
        public long Type { get; }
        public long Identifier { get; }
    }

    // Called only after the connection has accepted the peer's unique control stream.
    // Push promise existence and cancellation are checked by the caller's push registry.
    internal sealed class Http3ControlStream
    {
        private readonly Http3FrameReader _reader;
        private readonly bool _peerIsServer;
        private long _lastGoAway = QuicInteger.Maximum;
        private long _maximumPush = -1;
        private int _reading;
        private bool _failed;
        public Http3PeerSettings? Settings { get; private set; }

        internal Http3ControlStream(Stream source, bool peerIsServer)
        { _reader = new Http3FrameReader(source); _peerIsServer = peerIsServer; }

        internal async Task<Http3ControlEvent> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Concurrent control stream processing.");
            try
            {
                if (_failed) throw new IOException("HTTP/3 control stream failed.");
                while (true)
                {
                    var header = await _reader.ReadHeaderAsync(token).ConfigureAwait(false)
                        ?? throw new Http3ProtocolException(0x104, "Peer closed the HTTP/3 control stream.");
                    var type = header.Type;
                    if (Settings == null && type != 4) throw new Http3ProtocolException(0x10a, "SETTINGS must be the first control frame.");
                    if (type == 4)
                    {
                        if (Settings != null) throw new Http3ProtocolException(0x105, "Repeated HTTP/3 SETTINGS.");
                        Settings = Http3PeerSettings.Parse(await _reader.ReadBufferedPayloadAsync(16384, token).ConfigureAwait(false));
                        return new Http3ControlEvent(4, 0);
                    }
                    if (type == 0 || type == 1 || type == 2 || type == 5 || type == 6 || type == 8 || type == 9
                        || (type == 13 && _peerIsServer))
                        throw new Http3ProtocolException(0x105, "Frame is forbidden on this HTTP/3 control stream.");
                    if (type == 3 || type == 7 || type == 13)
                    {
                        if (header.Length == 0 || header.Length > 8) throw new Http3ProtocolException(0x106, "Invalid HTTP/3 control identifier length.");
                        var payload = await _reader.ReadBufferedPayloadAsync(8, token).ConfigureAwait(false);
                        var offset = 0;
                        long id;
                        try { id = QuicInteger.Read(payload, ref offset, payload.Length); }
                        catch (EndOfStreamException) { throw new Http3ProtocolException(0x106, "Truncated control identifier."); }
                        if (offset != payload.Length) throw new Http3ProtocolException(0x106, "Extra bytes after control identifier.");
                        if (type == 7)
                        {
                            if (id > _lastGoAway || (_peerIsServer && (id & 3) != 0)) throw new Http3ProtocolException(0x108, "Invalid GOAWAY identifier.");
                            _lastGoAway = id;
                        }
                        else if (type == 13)
                        {
                            if (id < _maximumPush) throw new Http3ProtocolException(0x108, "MAX_PUSH_ID cannot decrease.");
                            _maximumPush = id;
                        }
                        return new Http3ControlEvent(type, id);
                    }
                    await _reader.SkipPayloadAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _failed = true; throw; }
            finally { Volatile.Write(ref _reading, 0); }
        }
    }
}
