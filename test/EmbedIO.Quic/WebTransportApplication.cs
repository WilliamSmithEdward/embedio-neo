using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.Http3;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Quic
{
    // HTTP/3 adapter, compiled with the existing codecs/session core.
    // All callbacks run on the connection owner. No production listener uses it.
    internal sealed class WebTransportApplication : StreamApplication
    {
        private sealed class Input
        {
            internal byte[] Bytes { get; } = new byte[65536];

            internal int Length;
            internal int Cursor;
            internal long Type = -1;
            internal long SessionId = -1;
            internal int Accounted;
            internal bool Fin;
            internal bool Finished;
            internal WebTransportSession? Session;
            internal WebTransportStreamHandle? Handle;
            internal ulong? OutputId;
        }

        private readonly Dictionary<ulong, Input> _inputs = [];
        private readonly Dictionary<ulong, (
            WebTransportSession Session,
            WebTransportStreamHandle Handle
        )> _outputs = [];

        internal int RetainedInputs => _inputs.Count;
        internal int RetainedOutputs => _outputs.Count;

        private readonly Dictionary<long, WebTransportSession> _sessions = [];
        private readonly Dictionary<long, Input> _capsules = [];
        private readonly QpackDecoderTable _qpack = new(0);
        private readonly Action<ulong, byte[], bool> _write;
        private readonly Action<byte[]> _datagram;
        private readonly Action<ulong, byte[]> _prefix;
        private readonly Action<ulong, long> _abort;
        private readonly Action<ulong, int> _consume;
        private readonly Func<IReadOnlyDictionary<string, string>, bool> _authorize;
        private readonly WebTransportSettings _local = WebTransportSettings.Local(true, 65536, 8, 8);
        private WebTransportSessionRegistry? _registry;
        private bool _settings;
        private bool _control;
        private bool _encoder;
        private bool _decoder;
        private readonly bool _parametersMet;
        private ulong _nextUni = 15;

        internal int AcceptedSessions { get; private set; }
        internal int RejectedSessions { get; private set; }
        internal int StreamsDelivered { get; private set; }
        internal int DatagramsDelivered { get; private set; }
        internal int ResetsDelivered { get; private set; }
        internal int ActiveSessions => _registry?.ActiveSessionCount ?? 0;

        internal WebTransportApplication(
            bool parametersMet,
            StreamOperations operations,
            Func<IReadOnlyDictionary<string, string>, bool> authorize
        )
        {
            _parametersMet = parametersMet;
            _write = operations.Write;
            _prefix = operations.ReliablePrefix;
            _datagram = operations.Datagram;
            _abort = operations.Abort;
            _consume = operations.Consume;
            _authorize = authorize;
            using var settings = new MemoryStream();
            settings.Write(Wire.Frame(8, 1, 0x33, 1, 6, 8192));
            settings.Write(_local.EncodeLocal());
            _write(
                3,
                [
                    0,
                    .. Frame(4, settings.ToArray())
                ],
                false
            );
            _write(7, [2], false);
            _write(11, [3], false);
        }

        internal override void Stream(ulong id, byte[] bytes, bool fin)
        {
            if (!_inputs.TryGetValue(id, out var input))
            {
                if (_inputs.Count >= 32) throw new InvalidDataException("HTTP/3 stream budget exceeded.");

                _inputs.Add(id, input = new Input());
            }

            if (bytes.Length > input.Bytes.Length - input.Length)
                throw new InvalidDataException("HTTP/3 input budget exceeded.");

            bytes.CopyTo(input.Bytes, input.Length);
            input.Length += bytes.Length;
            input.Fin |= fin;
            ProcessSafely(id, input);
        }

        private void ProcessSafely(ulong id, Input input)
        {
            try
            {
                Process(id, input);
            }

            catch (WebTransportException error)
            {
                var session = input.Session ?? _sessions.GetValueOrDefault((long)id);

                if (session == null) throw;

                session.Terminate(error.ErrorCode);
                _abort((ulong)session.Id, error.ErrorCode);
                _registry?.SessionEnded(session);
                _sessions.Remove(session.Id);

                foreach (var entry in _inputs.Where(
                    pair => pair.Key == (ulong)session.Id || pair.Value.Session == session
                ))
                    entry.Value.Finished = true;
            }

            Compact(id, input);
        }

        private void Compact(ulong id, Input input)
        {
            var consumed = input.Finished ? input.Length : input.Cursor;

            if (consumed == 0) return;

            Array.Copy(input.Bytes, consumed, input.Bytes, 0, input.Length - consumed);
            Array.Clear(input.Bytes, input.Length - consumed, consumed);
            input.Length -= consumed;
            input.Accounted = Math.Max(0, input.Accounted - consumed);
            input.Cursor = 0;
            _consume(id, consumed);
        }

        private void Process(ulong id, Input input)
        {
            if (input.Finished) return;

            if (input.Type < 0)
            {
                var cursor = 0;

                if (!Integer(input, ref cursor, out var type)) return;

                if ((id & 2) == 0)
                {
                    if (type == 0x41) input.Type = 0x54;

                    else
                    {
                        input.Type = 0x100;
                        cursor = 0;
                    }
                }

                else input.Type = (long)type;

                if (input.Type == 0x54)
                {
                    if (!Integer(input, ref cursor, out var session))
                    {
                        input.Type = -1;
                        return;
                    }

                    if (!WebTransportProtocol.IsSessionId((long)session))
                        throw new InvalidDataException("Invalid session ID.");

                    input.SessionId = (long)session;
                    input.Accounted = cursor;
                    input.Handle = new WebTransportStreamHandle(
                        (long)id,
                        (id & 2) == 0,
                        error => AbortInput(id, input, error)
                    );
                }

                else if ((id & 2) != 0)
                {
                    ref var seen = ref (type == 0 ? ref _control : ref (type == 2 ? ref _encoder : ref _decoder));

                    if (type is 0 or 2 or 3)
                    {
                        if (seen) throw new InvalidDataException("Duplicate critical HTTP/3 stream.");

                        seen = true;
                    }
                }

                input.Cursor = cursor;
            }

            if (input.Type == 0x54)
            {
                if (input.Session == null && input.Handle != null && _registry != null)
                {
                    var handle = input.Handle;
                    input.Handle = null;

                    if (!_registry.RouteIncomingStream(input.SessionId, handle)) input.Finished = true;
                }

                Deliver(id, input);
                return;
            }

            if (input.Type is 2 or 3)
            {
                if (input.Length != input.Cursor)
                    throw new InvalidDataException("Dynamic QPACK is disabled for this profile.");

                if (input.Fin) throw new InvalidDataException("Critical HTTP/3 stream closed.");

                return;
            }

            if (input.Type is not (0 or 0x100))
            {
                input.Cursor = input.Length;
                input.Finished = input.Fin;
                return;
            }

            while (input.Cursor < input.Length)
            {
                var cursor = input.Cursor;

                if (!Integer(input, ref cursor, out var type) || !Integer(input, ref cursor, out var length)) break;

                if (length > 8192) throw new InvalidDataException("HTTP/3 frame budget exceeded.");

                if (length > (ulong)(input.Length - cursor)) break;

                var payload = input.Bytes
                    .AsSpan(cursor, (int)length)
                    .ToArray();

                if (input.Type == 0)
                {
                    if (!_settings && type != 4) throw new InvalidDataException("SETTINGS must be first.");

                    if (type == 4)
                    {
                        if (_settings) throw new InvalidDataException("Repeated SETTINGS.");

                        Http3PeerSettings.Parse(payload);
                        var peer = WebTransportSettings.Parse(payload);
                        _registry = new WebTransportSessionRegistry(
                            _local,
                            peer,
                            _parametersMet,
                            new WebTransportRegistryLimits { MaximumSessions = 2 },
                            Attach,
                            Datagram
                        );
                        _settings = true;
                    }

                    else if (type is 0 or 1) throw new InvalidDataException("Request frame on control stream.");
                }

                else if (!_settings) return;

                else if (type == 1 && !_sessions.ContainsKey((long)id)) Admit(id, input, payload);

                else if (type == 0 && _sessions.TryGetValue((long)id, out var session)) Capsules(session, payload);

                else throw new InvalidDataException("Unexpected request frame.");

                input.Cursor = cursor + (int)length;
            }

            if (input.Fin)
            {
                if (input.Cursor != input.Length || input.Type == 0)
                    throw new InvalidDataException("Incomplete or closed critical HTTP/3 input.");

                if (_capsules.TryGetValue((long)id, out var capsules) && capsules.Length != 0)
                    throw new WebTransportException(WebTransportProtocol.H3MessageError, "Truncated capsule.");

                if (_sessions.Remove((long)id, out var session))
                {
                    session.PeerFinished();
                    _registry?.SessionEnded(session);
                }

                input.Finished = true;
            }

            if (input.Type == 0 && _settings)
                foreach (var other in _inputs
                    .Where(pair => pair.Key != id)
                    .ToArray()) ProcessSafely(other.Key, other.Value);
        }

        private void Admit(ulong id, Input input, byte[] payload)
        {
            var decoded = _qpack
                .ReadSection(payload, 8192, 8192)
                .TryDecode()
                ?? throw new InvalidDataException("Blocked QPACK section without dynamic capacity.");
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            var regular = false;

            foreach (var field in decoded)
            {
                if (
                    field.Name.Length == 0
                    || field.Name.Any(c => c is >= 'A' and <= 'Z')
                    || field.Value.Any(c => c is '\r' or '\n' or '\0')
                    || (field.Name[0] == ':' && regular)
                    || !headers.TryAdd(field.Name, field.Value)
                )
                    throw new InvalidDataException("Invalid CONNECT headers.");

                regular |= field.Name[0] != ':';
            }

            var valid = headers.GetValueOrDefault(":method") == "CONNECT"
                && headers.GetValueOrDefault(":protocol") == WebTransportProtocol.ProtocolToken
                && headers.GetValueOrDefault(":scheme") == "https"
                && headers.GetValueOrDefault(":authority") == "localhost"
                && headers.GetValueOrDefault(":path") == "/echo"
                && headers.GetValueOrDefault("origin") == "https://localhost"
                && headers.Keys
                    .Where(k => k.StartsWith(':'))
                    .All(k => k is ":method" or ":protocol" or ":scheme" or ":authority" or ":path");
            var registry = _registry ?? throw new InvalidOperationException("Missing SETTINGS.");
            var authorized = valid && _authorize(headers);

            if (authorized && !registry.Negotiated)
            {
                RejectedSessions++;
                registry.NoteRequestStream((long)id);
                _abort(id, WebTransportProtocol.H3MessageError);
                input.Finished = true;
                return;
            }

            WebTransportSession? session = null;
            var admission = authorized
                ? registry.TryCreateSession((long)id, out session)
                : WebTransportAdmission.NotNegotiated;
            var accepted = admission == WebTransportAdmission.Accepted && session != null;
            var status = accepted ? "200" : !authorized ? "403" : "429";
            _write(id, Frame(1, QpackEncoder.Encode([new HpackField(":status", status)], 8192, 8192)), !accepted);

            if (session != null && accepted)
            {
                _sessions.Add((long)id, session);
                AcceptedSessions++;
                session.Establish();
            }

            else
            {
                RejectedSessions++;
                registry.NoteRequestStream((long)id);
                input.Finished = true;
            }
        }

        private void Attach(WebTransportSession session, WebTransportStreamHandle handle)
        {
            if (!_inputs.TryGetValue((ulong)handle.Id, out var input))
            {
                handle.Abort(WebTransportProtocol.SessionGone);
                session.StreamFinished(handle);
                return;
            }

            input.Session = session;
            input.Handle = handle;
            StreamsDelivered++;
            Deliver((ulong)handle.Id, input);
            Compact((ulong)handle.Id, input);
        }

        private void Deliver(ulong id, Input input)
        {
            var session = input.Session;

            if (session == null || session.State != WebTransportSessionState.Established || input.Finished) return;

            var count = input.Length - input.Cursor;

            if (count == 0 && !input.Fin) return;

            session.RecordIncomingData(input.Length - input.Accounted);
            input.Accounted = input.Length;

            if (input.OutputId == null)
            {
                if ((id & 2) == 0) input.OutputId = id;

                else
                {
                    if (!session.TryOpenOutgoingStream(false)) return;

                    input.OutputId = _nextUni;
                    _nextUni += 4;
                    var outputHandle = new WebTransportStreamHandle(
                        (long)input.OutputId.Value,
                        false,
                        error => _abort(input.OutputId.Value, error)
                    );
                    session.RegisterOutgoingStream(outputHandle);
                    _outputs.Add(input.OutputId.Value, (session, outputHandle));
                    var prefix = Wire.Frame(0x54, (ulong)session.Id);
                    _prefix(input.OutputId.Value, prefix);
                    _write(input.OutputId.Value, prefix, false);
                }
            }

            if (!session.TryReserveOutgoingData(count))
            {
                DrainCapsules(session);
                return;
            }

            _write(
                input.OutputId.Value,
                input.Bytes
                    .AsSpan(input.Cursor, count)
                    .ToArray(),
                input.Fin
            );
            input.Cursor = input.Length;
            session.ReleaseIncomingData(count);
            DrainCapsules(session);

            if (input.Fin)
            {
                input.Finished = true;

                if ((id & 2) != 0 && input.Handle != null)
                {
                    session.StreamFinished(input.Handle);
                    DrainCapsules(session);
                }
            }
        }

        internal override void ReceiveDatagram(byte[] payload)
        {
            var session = WebTransportProtocol.ReadDatagramSessionId(payload, 0, payload.Length, out var offset);
            _registry?.RouteDatagram(session, payload, offset, payload.Length - offset);
        }

        private void Datagram(WebTransportSession session, byte[] bytes, int offset, int count)
        {
            DatagramsDelivered++;
            using var packet = new MemoryStream();
            Wire.Integer(packet, (ulong)session.Id / 4);
            packet.Write(bytes, offset, count);
            _datagram(packet.ToArray());
        }

        private void Capsules(WebTransportSession session, byte[] payload)
        {
            if (!_capsules.TryGetValue(session.Id, out var input)) _capsules.Add(session.Id, input = new Input());

            if (payload.Length > input.Bytes.Length - input.Length)
                throw new InvalidDataException("Capsule buffer limit exceeded.");

            payload.CopyTo(input.Bytes, input.Length);
            input.Length += payload.Length;

            while (input.Cursor < input.Length)
            {
                var cursor = input.Cursor;

                if (!Integer(input, ref cursor, out var type) || !Integer(input, ref cursor, out var length)) break;

                if (length > 8192)
                    throw new WebTransportException(WebTransportProtocol.H3MessageError, "Capsule limit exceeded.");

                if (length > (ulong)(input.Length - cursor)) break;

                session.ProcessCapsule(
                    new WebTransportCapsule(
                        (long)type,
                        input.Bytes
                            .AsSpan(cursor, (int)length)
                            .ToArray()
                    )
                );
                input.Cursor = cursor + (int)length;
            }

            Array.Copy(input.Bytes, input.Cursor, input.Bytes, 0, input.Length - input.Cursor);
            input.Length -= input.Cursor;
            input.Cursor = 0;

            foreach (var item in _inputs
                .Where(pair => pair.Value.Session == session)
                .ToArray())
            {
                Deliver(item.Key, item.Value);
                Compact(item.Key, item.Value);
            }

            DrainCapsules(session);

            if (session.State == WebTransportSessionState.Closed)
            {
                _write((ulong)session.Id, [], true);
                _registry?.SessionEnded(session);
            }
        }

        private void DrainCapsules(WebTransportSession session)
        {
            while (session.TryDequeueOutgoingCapsule(out var capsule))
                _write((ulong)session.Id, Frame(0, capsule), false);
        }

        internal override void Stop(ulong id, ulong error)
        {
            if (id is 3 or 7 or 11) throw new InvalidDataException("Peer stopped a critical HTTP/3 stream.");
        }

        internal override void Reset(ulong id, ulong error)
        {
            if (_inputs.TryGetValue(id, out var critical) && critical.Type is 0 or 2 or 3)
                throw new InvalidDataException("Peer reset a critical HTTP/3 stream.");

            if (_sessions.Remove((long)id, out var session))
            {
                session.PeerAborted();
                _registry?.SessionEnded(session);
            }

            if (_inputs.TryGetValue(id, out var input))
            {
                if (input.Session != null) ResetsDelivered++;

                input.Finished = true;

                if ((id & 2) != 0 && input.Session != null && input.Handle != null)
                    input.Session.StreamFinished(input.Handle);

                if (input.OutputId.HasValue) _abort(input.OutputId.Value, WebTransportProtocol.SessionGone);

                Compact(id, input);
            }
        }

        private void AbortInput(ulong id, Input input, long error)
        {
            input.Finished = true;
            _abort(id, error);
            Compact(id, input);
        }

        internal override bool CanRetire(ulong id) =>
            !_inputs.TryGetValue(id, out var input)
                || input.Finished && (!input.OutputId.HasValue || !_outputs.ContainsKey(input.OutputId.Value));

        internal override void Retire(ulong id)
        {
            if (_outputs.Remove(id, out var output)) output.Session.StreamFinished(output.Handle);

            if (!_inputs.Remove(id, out var input)) return;

            if (input.Session != null && input.Handle != null)
            {
                input.Session.StreamFinished(input.Handle);
                DrainCapsules(input.Session);
            }

            _capsules.Remove((long)id);

            if ((id & 3) == 0) _registry?.NoteRequestStream((long)id);
        }

        private static bool Integer(Input input, ref int cursor, out ulong value)
        {
            value = 0;

            if (cursor == input.Length || (1 << (input.Bytes[cursor] >> 6)) > input.Length - cursor) return false;

            var reader = new WireReader(input.Bytes.AsSpan(cursor, input.Length - cursor));
            value = reader.Integer();
            cursor = input.Length - reader.Remaining;
            return true;
        }

        private static byte[] Frame(ulong type, byte[] payload)
        {
            using var stream = new MemoryStream();
            Wire.Integer(stream, type);
            Wire.Integer(stream, (ulong)payload.Length);
            stream.Write(payload);
            return stream.ToArray();
        }

        public override void Dispose()
        {
            try
            {
                _registry?.Shutdown();
            }

            finally
            {
                _registry = null;
                _inputs.Clear();
                _outputs.Clear();
                _capsules.Clear();
                _sessions.Clear();
            }
        }
    }
}
