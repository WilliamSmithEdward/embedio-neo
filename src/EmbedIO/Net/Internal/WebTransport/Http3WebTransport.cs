#if NET10_0_OR_GREATER
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    internal enum WebTransportRequestOutcome
    {
        // An ordinary request; the connection serves it as before.
        NotWebTransport,
        // A WebTransport request this connection did not negotiate: H3_MESSAGE_ERROR.
        RequirementsNotMet,
        // Answered with 429 before dispatch (section 5.2).
        TooManySessions,
        Admitted
    }

    // WebTransport for one HTTP/3 connection: the SETTINGS it advertises, the
    // dialect the peer's SETTINGS select, the requests and peer streams it
    // classifies and the sessions it admits. The connection owns each QUIC stream
    // until this class hands it to a session.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebTransport
    {
        // Session data a peer may send before more credit. Credit is renewed as the
        // application reads, in steps of half this window.
        private const long InitialMaxData = 262144;

        // Section 3.1: a server waits for the client's SETTINGS before processing a session.
        private static readonly TimeSpan SettingsWait = TimeSpan.FromSeconds(10);
        private const int NoDialect = 0;
        private const int Draft16 = 1;
        private const int Draft02 = 2;
        private readonly Http3TransportConnection _connection;
        private readonly WebTransportOptions _options;
        private readonly WebTransportSettings _local;
        private readonly WebTransportSessionRegistry _registry;
        private readonly TaskCompletionSource _settings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dialect;

        internal Http3WebTransport(Http3TransportConnection connection, WebTransportOptions options)
        {
            _connection = connection;
            _options = options;
            _local = WebTransportSettings.Local(
                true,
                InitialMaxData,
                options.MaximumIncomingStreamsPerSession,
                options.MaximumIncomingStreamsPerSession
            );
            // Every request stream is settled when its worker ends, so the gaps in the
            // record are requests still in progress: at most one per worker, and the
            // record holds one more range than it has gaps.
            var limits = new WebTransportRegistryLimits
            {
                MaximumSessions = options.MaximumSessionsPerConnection,
                MaximumTrackedRequestRanges = Http3QuicConnection.MaximumStreamWorkers + 1
            };
            limits.Session.MaximumActiveIncomingStreams = options.MaximumIncomingStreamsPerSession;
            _registry = new WebTransportSessionRegistry(
                _local,
                limits,
                static (session, stream) => (session.Owner as Http3WebTransportSession)?.Deliver(stream),
                static (_, _, _, _) => { }
            );
        }

        internal bool ReliableReset => _connection.ReliableReset;

        // The extension's pairs for the server's SETTINGS frame.
        internal byte[] EncodeSettings() => _local.EncodeLocal(_options.AcceptBrowserDraft02);

        // The peer's SETTINGS fix the dialect of every session on this connection.
        // Draft-16 needs negotiated datagrams and reliable reset; draft-02 only datagrams.
        internal void PeerSettingsReceived(WebTransportSettings? peer)
        {
            var settings = peer ?? WebTransportSettings.Parse([]);
            var datagrams = settings.Datagrams && _connection.PeerMaximumDatagramFrameSize != 0;

            if (settings.Enabled)
            {
                _registry.Negotiate(settings, datagrams && _connection.ReliableReset, 1);
                Volatile.Write(ref _dialect, _registry.Negotiated ? Draft16 : NoDialect);
            }

            else if (_options.AcceptBrowserDraft02 && settings.Draft02Enabled && datagrams)
            {
                // Draft-02 declares no session limits, so no flow control applies.
                _registry.Negotiate(
                    WebTransportSettings.Local(true, 0, 0, 0),
                    true,
                    _options.MaximumSessionsPerConnection
                );
                Volatile.Write(ref _dialect, Draft02);
            }

            else _registry.Negotiate(settings, false, 1);

            _settings.TrySetResult();
        }

        // Classifies a request and admits a WebTransport session for it.
        internal async Task<(WebTransportRequestOutcome Outcome, Http3WebTransportSession? Session)> AdmitAsync(
            long streamId,
            string method,
            string? protocol,
            IMultiplexedTunnelControl control,
            CancellationToken token
        )
        {
            var dialect = method == "CONNECT" && protocol != null
                ? HttpUpgradeProtocols.Matches(protocol, WebTransportProtocol.ProtocolToken)
                    ? Draft16
                    : _options.AcceptBrowserDraft02
                        && HttpUpgradeProtocols.Matches(protocol, WebTransportProtocol.Draft02ProtocolToken)
                        ? Draft02
                        : NoDialect
                : NoDialect;

            if (dialect == NoDialect)
            {
                _registry.NoteRequestStream(streamId);
                return (WebTransportRequestOutcome.NotWebTransport, null);
            }

            try
            {
                await _settings.Task
                    .WaitAsync(SettingsWait, token)
                    .ConfigureAwait(false);
            }

            catch (TimeoutException)
            {
                _registry.NoteRequestStream(streamId);
                return (WebTransportRequestOutcome.RequirementsNotMet, null);
            }

            if (Volatile.Read(ref _dialect) != dialect)
            {
                _registry.NoteRequestStream(streamId);
                return (WebTransportRequestOutcome.RequirementsNotMet, null);
            }

            switch (_registry.TryCreateSession(streamId, out var core))
            {
                case WebTransportAdmission.Accepted when core != null:
                    return (
                        WebTransportRequestOutcome.Admitted,
                        new Http3WebTransportSession(this, core, control, streamId, dialect == Draft02, token)
                    );

                case WebTransportAdmission.TooManySessions:
                    _registry.NoteRequestStream(streamId);
                    return (WebTransportRequestOutcome.TooManySessions, null);

                default:
                    _registry.NoteRequestStream(streamId);
                    return (WebTransportRequestOutcome.RequirementsNotMet, null);
            }
        }

        // A client bidirectional stream that began with the 0x41 signal. Its identifier
        // is not a request, so it can never name a session (section 4.3).
        internal async Task AcceptBidirectionalAsync(Http3TransportStream stream, CancellationToken token)
        {
            _registry.NoteRequestStream(stream.Id);
            await RouteAsync(stream, true, token).ConfigureAwait(false);
        }

        // A unidirectional stream of type 0x54 (section 4.2).
        internal Task AcceptUnidirectionalAsync(Http3TransportStream stream, CancellationToken token) =>
            RouteAsync(stream, false, token);

        internal ValueTask<Http3TransportStream> OpenOutboundStreamAsync(bool bidirectional, CancellationToken token) =>
            _connection.OpenOutboundStreamAsync(
                bidirectional ? Http3StreamType.Bidirectional : Http3StreamType.Unidirectional,
                token
            );

        internal void SessionEnded(WebTransportSessionCore session) => _registry.SessionEnded(session);

        // A request stream's worker ended, whether or not the request was classified.
        internal void RequestEnded(long streamId) => _registry.RequestEnded(streamId);

        // The server is draining: each session is asked to finish.
        internal void Drain()
        {
            foreach (var session in _registry.Sessions) (session.Owner as Http3WebTransportSession)?.Drain();
        }

        // The connection is ending: sessions end and waiting streams are reset.
        internal void Shutdown()
        {
            foreach (var session in _registry.Sessions) (session.Owner as Http3WebTransportSession)?.Terminate(null);

            _registry.Shutdown();
            _settings.TrySetResult();
        }

        // Owns the stream from here: it is released unless a session or the registry takes it.
        private async Task RouteAsync(Http3TransportStream stream, bool bidirectional, CancellationToken token)
        {
            long? sessionId;

            try
            {
                sessionId = await Http3QuicConnection
                    .ReadIntegerAsync(stream, token)
                    .ConfigureAwait(false);

                if (sessionId.HasValue && !WebTransportProtocol.IsSessionId(sessionId.Value))
                    throw new Http3ProtocolException(
                        WebTransportProtocol.H3IdError,
                        "WebTransport session identifier is not a client-initiated bidirectional stream."
                    );
            }

            catch
            {
                new IncomingStream(stream).Abort(WebTransportProtocol.SessionGone);
                throw;
            }

            if (!sessionId.HasValue)
            {
                // The peer ended the stream inside its header.
                await stream
                    .DisposeAsync()
                    .ConfigureAwait(false);
                return;
            }

            var incoming = new IncomingStream(stream);
            var handle = new WebTransportStreamHandle(stream.Id, bidirectional, incoming.Abort)
            {
                Transport = incoming
            };

            try
            {
                _registry.RouteIncomingStream(sessionId.Value, handle);
            }

            // The stream broke an established session's limit; the core ended the
            // session and reset its streams. Let its writer reset the session stream.
            catch (WebTransportException)
            {
                FindOwner(sessionId.Value)?.Signal();
            }
        }

        private Http3WebTransportSession? FindOwner(long sessionId)
        {
            foreach (var session in _registry.Sessions)
                if (session.Id == sessionId) return session.Owner as Http3WebTransportSession;

            return null;
        }

        // A peer stream between its header and its session. Until a session takes
        // it, an abort from the core also releases the QUIC stream.
        internal sealed class IncomingStream
        {
            private readonly object _gate = new();
            private Http3WebTransportStream? _backend;
            private bool _aborted;

            internal IncomingStream(Http3TransportStream stream) => Stream = stream;

            internal Http3TransportStream Stream { get; }

            internal bool Attach(Http3WebTransportStream backend)
            {
                lock (_gate)
                {
                    if (_aborted) return false;

                    _backend = backend;
                    return true;
                }
            }

            internal void Abort(long code)
            {
                Http3WebTransportStream? backend;

                lock (_gate)
                {
                    _aborted = true;
                    backend = _backend;
                }

                if (backend != null)
                {
                    backend.AbortFromSession(code);
                    return;
                }

                try
                {
                    Stream.Abort(Http3AbortDirection.Both, code);
                }

                catch (ObjectDisposedException) { }

                var disposal = Stream.DisposeAsync();

                if (!disposal.IsCompletedSuccessfully)
                    _ = disposal
                        .AsTask()
                        .ContinueWith(
                            static completed => _ = completed.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );
            }
        }
    }
}
#endif
