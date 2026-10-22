using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Net.Internal.Http3
{
    internal readonly struct Http3ControlEvent
    {
        internal Http3ControlEvent(
            long type,
            long identifier,
            string? priorityFieldValue = null,
            HttpPriority? priority = null
        )
        {
            Type = type;
            Identifier = identifier;
            PriorityFieldValue = priorityFieldValue;
            Priority = priority;
        }

        public long Type { get; }
        public long Identifier { get; }
        public string? PriorityFieldValue { get; }
        public HttpPriority? Priority { get; }
    }

    // Called only after the connection has accepted the peer's unique control stream.
    // Push promise existence and cancellation are checked by the caller's push registry.
    internal sealed class Http3ControlStream
    {
        private readonly Http3FrameReader _reader;
        private readonly bool _peerIsServer;
        private readonly bool _readWebTransportSettings;
        private long _lastGoAway = QuicInteger.Maximum;
        private long _maximumPush = -1;
        private int _reading;
        private bool _failed;

        public Http3PeerSettings? Settings { get; private set; }
        internal WebTransportSettings? WebTransportSettings { get; private set; }

        internal Http3ControlStream(Stream source, bool peerIsServer) : this(source, peerIsServer, false) { }

        internal Http3ControlStream(Stream source, bool peerIsServer, bool readWebTransportSettings)
        {
            _reader = new Http3FrameReader(source);
            _peerIsServer = peerIsServer;
            _readWebTransportSettings = readWebTransportSettings;
        }

        internal async Task<Http3ControlEvent> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent control stream processing.");

            try
            {
                if (_failed) throw new IOException("HTTP/3 control stream failed.");

                while (true)
                {
                    var header = await _reader
                        .ReadHeaderAsync(token)
                        .ConfigureAwait(false)
                        ?? throw new Http3ProtocolException(0x104, "Peer closed the HTTP/3 control stream.");
                    var type = header.Type;

                    if (Settings == null && type != 4)
                        throw new Http3ProtocolException(0x10a, "SETTINGS must be the first control frame.");

                    if (type == 4)
                    {
                        if (Settings != null) throw new Http3ProtocolException(0x105, "Repeated HTTP/3 SETTINGS.");

                        var payload = await _reader
                            .ReadBufferedPayloadAsync(16384, token)
                            .ConfigureAwait(false);
                        Settings = Http3PeerSettings.Parse(payload);

                        if (_readWebTransportSettings)
                            WebTransportSettings = WebTransportSettings.Parse(payload);

                        return new Http3ControlEvent(4, 0);
                    }

                    if (IsForbidden(type))
                        throw new Http3ProtocolException(0x105, "Frame is forbidden on this HTTP/3 control stream.");

                    if (type == 0xf0700 || type == 0xf0701)
                    {
                        if (_peerIsServer)
                            throw new Http3ProtocolException(0x105, "A server cannot send PRIORITY_UPDATE.");

                        return PriorityUpdate(
                            type,
                            await _reader
                                .ReadBufferedPayloadAsync(16384, token)
                                .ConfigureAwait(false)
                        );
                    }

                    if (type == 3 || type == 7 || type == 13)
                    {
                        if (header.Length == 0 || header.Length > 8)
                            throw new Http3ProtocolException(0x106, "Invalid HTTP/3 control identifier length.");

                        return ControlIdentifier(
                            type,
                            await _reader
                                .ReadBufferedPayloadAsync(8, token)
                                .ConfigureAwait(false)
                        );
                    }

                    await _reader
                        .SkipPayloadAsync(token)
                        .ConfigureAwait(false);
                }
            }

            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                _failed = true;
                throw;
            }

            finally
            {
                Volatile.Write(ref _reading, 0);
            }
        }

        // DATA, HEADERS, CANCEL_PUSH on a server's stream, PUSH_PROMISE and the
        // reserved types never travel on a control stream.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsForbidden(long type) =>
            type == 0
                || type == 1
                || type == 2
                || type == 5
                || type == 6
                || type == 8
                || type == 9
                || (type == 13 && _peerIsServer);

        // PRIORITY_UPDATE: a prioritized element identifier, then an ASCII priority field.
        private static Http3ControlEvent PriorityUpdate(long type, byte[] payload)
        {
            var offset = 0;
            var id = ReadIdentifier(payload, ref offset, "Truncated priority identifier.");

            if (type == 0xf0700 && (id & 3) != 0)
                throw new Http3ProtocolException(0x108, "Priority target is not a request stream.");

            for (var i = offset; i < payload.Length; i++)
                if (payload[i] > 127) throw new Http3ProtocolException(0x101, "Priority field is not ASCII.");

            // Preserve empty fields: they reset defaults. Scheduling and
            // promised-push ownership belong to the connection.
            var field = Encoding.ASCII.GetString(payload, offset, payload.Length - offset);

            if (!HttpPriority.TryParse(field, out var priority))
                throw new Http3ProtocolException(0x101, "Malformed priority field dictionary.");

            return new Http3ControlEvent(type, id, field, priority);
        }

        // CANCEL_PUSH, GOAWAY and MAX_PUSH_ID carry exactly one identifier; GOAWAY
        // identifiers may only fall and MAX_PUSH_ID may only rise.
        private Http3ControlEvent ControlIdentifier(long type, byte[] payload)
        {
            var offset = 0;
            var id = ReadIdentifier(payload, ref offset, "Truncated control identifier.");

            if (offset != payload.Length)
                throw new Http3ProtocolException(0x106, "Extra bytes after control identifier.");

            if (type == 7)
            {
                if (id > _lastGoAway || (_peerIsServer && (id & 3) != 0))
                    throw new Http3ProtocolException(0x108, "Invalid GOAWAY identifier.");

                _lastGoAway = id;
            }

            else if (type == 13)
            {
                if (id < _maximumPush) throw new Http3ProtocolException(0x108, "MAX_PUSH_ID cannot decrease.");

                _maximumPush = id;
            }

            return new Http3ControlEvent(type, id);
        }

        private static long ReadIdentifier(byte[] payload, ref int offset, string truncatedMessage)
        {
            try
            {
                return QuicInteger.Read(payload, ref offset, payload.Length);
            }

            catch (EndOfStreamException)
            {
                throw new Http3ProtocolException(0x106, truncatedMessage);
            }
        }
    }
}
