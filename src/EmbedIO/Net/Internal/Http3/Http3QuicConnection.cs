#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;
using EmbedIO.Net.Internal.Http2;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Net.Internal.Http3
{
    // Consumes one accepted connection. The listener selects TLS/ALPN and QUIC
    // receive-window/stream limits. This layer owns critical streams and workers.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3QuicConnection : IDisposable
    {
        private readonly BorrowedResource<Http3TransportConnection> _connection;
        private readonly Func<Http3QuicExchange, Task> _dispatch;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;

        // _sync guards stream workers, request admission and drain state.
        // QPACK decoding and response encoding each have their own gate, so
        // sibling streams do not serialize field coding behind admission.
        private readonly object _sync = new();
        private readonly object _decoderSync = new();
        private readonly object _encoderSync = new();
        private readonly Dictionary<long, Task> _workers = [];
        private readonly Dictionary<long, RequestScope> _requests = [];
        private readonly HashSet<long> _admitted = [];
        private readonly Http3PriorityState _priorities = new(256);
        private readonly Dictionary<long, TaskCompletionSource<HpackField[]>> _pending = [];
        private readonly CancellationTokenRegistration _stopRequests;
        private readonly bool _dispatchInline;
        private readonly QpackDecoder _decoder = new(4096, 16, 65536, 65536, 1048576, 65536);
        private readonly QpackEncoderFeedback _encoderFeedback = new(256, 4096);
        private readonly QpackResponseEncoder _responseEncoder;
        private readonly SemaphoreSlim _encoderReady = new(0, 1);
        private readonly SemaphoreSlim _feedbackReady = new(0, 1);
        private Http3PeerSettings _peer = Http3PeerSettings.Parse([]);
        private readonly Http3DatagramRouter? _datagrams;
        private readonly Http3WebTransport? _webTransport;
        private static readonly byte[] OrdinaryServerSettings = [0, 4, 12, 1, 0x50, 0, 6, 0x80, 1, 0, 0, 7, 16, 8, 1];
        private static readonly byte[] DatagramServerSettings = [
            0, 4, 14, 1, 0x50, 0, 6, 0x80, 1, 0, 0, 7, 16, 8, 1, 0x33, 1
        ];
        private Exception? _failure;
        private int _critical;

        // Matches the HTTP/2 concurrent-stream limit and QUIC's inbound
        // bidirectional stream limit (EmbedIOQuicListenerBinding), so one number
        // bounds concurrent application work per connection on both protocols.
        private const int MaximumApplications = 128;
        private int _applicationCount;
        private CancellationToken _drainToken;
        private static readonly TimeSpan ApplicationDrainTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _drainTimeout = ApplicationDrainTimeout;
        private readonly CancellationTokenSource _applicationDrain = new();
        private bool _draining;
        private long _highestRequestId = -4;

        private Http3QuicConnection(
            Http3TransportConnection connection,
            Func<Http3QuicExchange, Task> dispatch,
            CancellationToken token,
            bool dispatchInline,
            WebTransportOptions? webTransport = null
        )
        {
            _connection = new BorrowedResource<Http3TransportConnection>(connection);

            if (connection.Datagrams != null) _datagrams = new Http3DatagramRouter(connection.Datagrams);

            // Sessions need HTTP datagrams. The peer's SETTINGS and transport parameters
            // decide later whether this connection negotiated a WebTransport dialect.
            if (webTransport != null && _datagrams != null)
                _webTransport = new Http3WebTransport(connection, webTransport);

            _dispatch = dispatch;
            _dispatchInline = dispatchInline;
            _responseEncoder = new QpackResponseEncoder(_encoderFeedback, 4096, 65536);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _token = _stop.Token;
            // One registration fans connection cancellation out to every active
            // request. Per-request linked sources would all contend on this token.
            _stopRequests = _token.UnsafeRegister(
                static state => (state as Http3QuicConnection)?.CancelActiveRequests(),
                this
            );
        }

        // The listener's dispatch builds the context and either queues it or
        // starts the server's handler on the request stream's own task, which
        // already left the accept loop. It runs there instead of paying a second
        // thread-pool hop per request, so a handler that blocks before its first
        // suspension holds its own stream only, as it holds its own connection
        // over HTTP/1.1; other streams and the accept loop keep going.
        internal static async Task RunTransportForListenerAsync(
            Http3TransportConnection connection,
            Func<Http3QuicExchange, Task> dispatch,
            CancellationToken abortToken,
            CancellationToken drainToken,
            TimeSpan drainTimeout,
            WebTransportOptions? webTransport = null
        )
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(dispatch);

            if (drainTimeout <= TimeSpan.Zero || drainTimeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(drainTimeout));

            await using (connection.ConfigureAwait(false))
            using (var session = new Http3QuicConnection(connection, dispatch, abortToken, true, webTransport))
            {
                session._drainToken = drainToken;
                session._drainTimeout = drainTimeout;
                await session
                    .RunCoreAsync()
                    .ConfigureAwait(false);
            }
        }

        private async Task RunCoreAsync()
        {
            Http3TransportStream? control = null;
            Http3TransportStream? feedback = null;
            var background = Array.Empty<Task>();

            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(_token);
                startup.CancelAfter(TimeSpan.FromSeconds(10));
                control = await _connection.Value
                    .OpenOutboundStreamAsync(Http3StreamType.Unidirectional, startup.Token)
                    .ConfigureAwait(false);
                var settings = EncodeSettingsFrame(_datagrams != null, _webTransport?.EncodeSettings());
                await control
                    .WriteAsync(settings, startup.Token)
                    .ConfigureAwait(false);
                feedback = await _connection.Value
                    .OpenOutboundStreamAsync(Http3StreamType.Unidirectional, startup.Token)
                    .ConfigureAwait(false);
                await feedback
                    .WriteAsync((byte[])[3], startup.Token)
                    .ConfigureAwait(false);
                background =
                [
                    WriteEncoderAsync(),
                    WriteFeedbackAsync(feedback),
                    WatchCriticalOutputAsync(control),
                    WatchCriticalOutputAsync(feedback),
                    WatchDrainAsync(control)
                ];

                if (_datagrams != null)
                    background = [.. background.Append(ReadDatagramsAsync())];

                while (!_token.IsCancellationRequested)
                {
                    var stream = await _connection.Value
                        .AcceptInboundStreamAsync(_token)
                        .ConfigureAwait(false);
                    bool rejected;
                    bool draining;

                    lock (_sync)
                    {
                        draining = _draining && stream.Type == Http3StreamType.Bidirectional;
                        rejected = draining || _workers.Count >= 256;

                        // Register under the tracking gate before a worker can finish.
                        if (!rejected)
                        {
                            RequestScope? scope = null;

                            if ((stream.Id & 3) == 0)
                            {
                                _priorities.Open(stream.Id);
                                scope = new RequestScope(this, stream);
                                _requests.Add(stream.Id, scope);

                                // CancelActiveRequests may already have taken its snapshot.
                                if (_token.IsCancellationRequested) scope.Stop.Cancel();
                            }

                            // The worker yields to the thread pool before any stream work.
                            _workers.Add(stream.Id, ProcessStreamAsync(stream, scope));
                        }
                    }

                    if (rejected)
                    {
                        AbortStream(stream, draining ? 0x10b : 0x107);
                        await stream
                            .DisposeAsync()
                            .ConfigureAwait(false);

                        if (!draining)
                            throw new Http3ProtocolException(0x107, "Too many active HTTP/3 stream workers.");
                    }
                }
            }

            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }

            catch (OperationCanceledException)
            {
                Fail(
                    new Http3ProtocolException(
                        0x103,
                        "Peer did not provide critical-stream credit before startup deadline."
                    )
                );
            }

            catch (Exception error) when (Http3TransportFailure.IsTransport(error))
            {
                CancelRequests(_stop);
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                Fail(error);
            }

            finally
            {
                try
                {
                    _datagrams?.Dispose();
                    CancelRequests(_stop);
                    _webTransport?.Shutdown();

                    lock (_decoderSync)
                    {
                        foreach (var pending in _pending.Values) pending.TrySetCanceled(_token);

                        _pending.Clear();
                    }

                    using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var code = _failure is Http3ProtocolException protocol
                        ? protocol.ErrorCode
                        : _failure == null ? 0x100 : 0x102;

                    try
                    {
                        await _connection.Value
                            .CloseAsync(code, closeDeadline.Token)
                            .ConfigureAwait(false);
                    }

                    catch (Exception error) when (
                        Http3TransportFailure.IsTransport(error) || error is OperationCanceledException
                    )
                    { }

                    Task[] workers;

                    lock (_sync) workers = [.. _workers.Values];

                    await Task
                        .WhenAll(workers.Concat(background))
                        .ConfigureAwait(false);
                }

                finally
                {
                    // A failed worker or callback must not keep either critical
                    // stream open after connection shutdown.
                    try
                    {
                        if (feedback != null)
                            await feedback
                                .DisposeAsync()
                                .ConfigureAwait(false);
                    }

                    finally
                    {
                        if (control != null)
                            await control
                                .DisposeAsync()
                                .ConfigureAwait(false);
                    }
                }
            }

            if (_failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(_failure)
                    .Throw();
        }

        internal static byte[] EncodeServerSettings(bool datagrams, WebTransportSettings? webTransport)
        {
            if (webTransport != null && (!datagrams || !webTransport.Enabled))
                throw new ArgumentException(
                    "WebTransport SETTINGS require an enabled datagram profile.",
                    nameof(webTransport)
                );

            return EncodeSettingsFrame(datagrams, webTransport?.EncodeLocal());
        }

        // The server control stream's type and SETTINGS frame. WebTransport pairs
        // follow the datagram profile they depend on.
        private static byte[] EncodeSettingsFrame(bool datagrams, byte[]? extension)
        {
            if (extension == null) return datagrams ? DatagramServerSettings : OrdinaryServerSettings;

            if (!datagrams)
                throw new ArgumentException("WebTransport SETTINGS require the datagram profile.", nameof(extension));

            var basePairs = DatagramServerSettings.AsSpan(3);
            var payloadLength = basePairs.Length + extension.Length;
            var bytes = new byte[2 + QuicInteger.EncodedLength(payloadLength) + payloadLength];
            bytes[0] = 0; // Server control stream type.
            bytes[1] = 4; // SETTINGS frame type.
            var offset = 2 + QuicInteger.Write(bytes, 2, payloadLength);
            basePairs.CopyTo(bytes.AsSpan(offset));
            extension.CopyTo(bytes.AsSpan(offset + basePairs.Length));
            return bytes;
        }

        private async Task ReadDatagramsAsync()
        {
            try
            {
                if (_datagrams != null)
                    await _datagrams
                        .RunAsync(_token)
                        .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                if (!_token.IsCancellationRequested) Fail(error);
            }
        }

        private async Task WatchDrainAsync(Http3TransportStream control)
        {
            using var requested = CancellationTokenSource.CreateLinkedTokenSource(
                _token,
                _drainToken,
                _applicationDrain.Token
            );

            try
            {
                await Task
                    .Delay(Timeout.Infinite, requested.Token)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (requested.IsCancellationRequested) { }

            if (_token.IsCancellationRequested) return;

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
            // KeepAlive=false retains its own policy even when a listener supplies
            // an independent external drain deadline and abort token.
            deadline.CancelAfter(_applicationDrain.IsCancellationRequested ? ApplicationDrainTimeout : _drainTimeout);

            try
            {
                long cutoff;
                Task[] accepted;
                Http3TransportStream[] unprocessed;

                lock (_sync)
                {
                    _draining = true;
                    cutoff = _highestRequestId + 4;
                    unprocessed =
                    [
                        .. _requests
                            .Where(item => !_admitted.Contains(item.Key))
                            .Select(item => item.Value.Stream)
                    ];
                    accepted =
                    [
                        .. _workers
                            .Where(item => (item.Key & 3) == 0)
                            .Select(item => item.Value)
                    ];
                }

                foreach (var request in unprocessed) AbortStream(request, 0x10b);

                // Section 4.7: sessions are asked to finish while the drain lets them run.
                _webTransport?.Drain();

                // Exhausted request stream IDs need no GOAWAY (RFC 9114 section 5.2).
                if (cutoff <= QuicInteger.Maximum)
                {
                    var frame = new byte[10];
                    var length = QuicInteger.Write(frame, 2, cutoff);
                    frame[0] = 7;
                    frame[1] = (byte)length;
                    await control
                        .WriteAsync(frame.AsMemory(0, length + 2), deadline.Token)
                        .ConfigureAwait(false);
                }

                await Task
                    .WhenAll(accepted)
                    .WaitAsync(deadline.Token)
                    .ConfigureAwait(false);
                // WriteAsync releases the send buffer; it does not confirm receipt.
                // Keep critical streams alive for peer-driven closure, bounded by
                // the drain deadline, rather than immediately overtaking GOAWAY.
                await Task
                    .Delay(Timeout.Infinite, deadline.Token)
                    .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }

            catch (Exception error) when (
                Http3TransportFailure.IsTransport(error) && !Http3TransportFailure.IsStream(error)
            )
            {
                CancelRequests(_stop);
            } // Connection closure is not a critical-stream reset.

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                if (!_token.IsCancellationRequested) Fail(error);
            }

            finally
            {
                CancelRequests(_stop);
            }
        }

        private async Task ProcessStreamAsync(Http3TransportStream stream, RequestScope? scope)
        {
            var critical = false;
            var handedOff = false;
            var streamId = stream.Id;
            // Leave the accept loop at once. Yielding queues this state machine
            // itself, without the closure and wrapper tasks of Task.Run.
            await Task.Yield();

            try
            {
                if (stream.Type == Http3StreamType.Bidirectional)
                {
                    if (scope == null) throw new Http3ProtocolException(0x103, "Invalid request stream initiator.");

                    byte[]? prefix = null;

                    if (_webTransport != null)
                    {
                        // Section 4.3: a session's bidirectional stream begins with 0x41,
                        // which no request frame uses. Anything else is replayed as a request.
                        var (first, bytes) = await ReadIntegerPrefixAsync(stream, _token).ConfigureAwait(false);

                        if (first == WebTransportProtocol.BidirectionalStreamSignal)
                        {
                            lock (_sync) _requests.Remove(streamId);

                            handedOff = true;
                            await _webTransport
                                .AcceptBidirectionalAsync(stream, _token)
                                .ConfigureAwait(false);
                            return;
                        }

                        prefix = bytes;
                    }

                    await ProcessRequestAsync(stream, scope, prefix).ConfigureAwait(false);
                }

                else
                {
                    var type = await ReadStreamTypeAsync(stream).ConfigureAwait(false);

                    if (!type.HasValue)
                        return; // A partial/absent type may be abandoned.

                    if (type == WebTransportProtocol.UnidirectionalStreamType && _webTransport != null)
                    {
                        handedOff = true;
                        await _webTransport
                            .AcceptUnidirectionalAsync(stream, _token)
                            .ConfigureAwait(false);
                        return;
                    }

                    if (type == 1) throw new Http3ProtocolException(0x103, "Client initiated a push stream.");

                    var flag = type == 0 ? 1 : type == 2 ? 2 : type == 3 ? 4 : 0;

                    if (flag == 0)
                    {
                        stream.Abort(Http3AbortDirection.Read, 0x103);
                        return;
                    }

                    critical = true;

                    if ((Interlocked.Or(ref _critical, flag) & flag) != 0)
                        throw new Http3ProtocolException(0x103, "Duplicate critical stream.");

                    if (type == 0) await ReadControlAsync(stream).ConfigureAwait(false);

                    else if (type == 2) await ReadEncoderAsync(stream).ConfigureAwait(false);

                    else await ReadDecoderAsync(stream).ConfigureAwait(false);
                }
            }

            catch (Http3ProtocolException error)
            {
                Fail(error);
            }

            catch (Http3StreamException error)
            {
                AbortStream(stream, error.ErrorCode);
            }

            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }

            catch (Exception error) when (Http3TransportFailure.IsTransport(error))
            {
                if (!_token.IsCancellationRequested)
                {
                    if (Http3TransportFailure.IsStream(error))
                    {
                        if (critical) Fail(new Http3ProtocolException(0x104, "Peer aborted a critical stream."));
                    }

                    else CancelRequests(_stop);
                }
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                if (critical) Fail(new Http3ProtocolException(0x102, error.Message));

                else AbortStream(stream, 0x102);
            }

            finally
            {
                if ((streamId & 3) == 0)
                {
                    // RFC 9204 section 4.4.2: Stream Cancellation is for reset or
                    // abandoned input. A stream read to its FIN has no section left
                    // to decode, and every decoded section was already acknowledged.
                    // A WebTransport stream never carried a field section.
                    if (scope?.InputComplete != true && !handedOff) CancelDecode(streamId);

                    _priorities.Close(streamId);
                }

                try
                {
                    // A WebTransport stream now belongs to its session, or was released.
                    if (!handedOff)
                        await stream
                            .DisposeAsync()
                            .ConfigureAwait(false);
                }

                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    if (!_token.IsCancellationRequested) Fail(error);
                }

                finally
                {
                    lock (_sync)
                    {
                        _workers.Remove(streamId);
                        _requests.Remove(streamId);
                        _admitted.Remove(streamId);
                    }
                }
            }
        }

        private async Task ProcessRequestAsync(Http3TransportStream stream, RequestScope scope, byte[]? prefix)
        {
            var requestStop = scope.Stop;
            var requestToken = requestStop.Token;
            // FIN is a successful half-close. Only faulted direction completion
            // cancels the request; this observes resets even while QPACK is blocked.
            var reads = stream.ReadsClosed;
            var writes = stream.WritesClosed;
            WatchRequestReadDirection(reads, requestStop);
            WatchRequestDirection(writes, requestStop);
            Http3WebTransportSession? webTransport = null;

            try
            {
                var source = prefix is { Length: > 0 }
                    ? new Http3PrefixedStream(prefix, stream)
                    : (System.IO.Stream)stream;
                var reader = new Http3RequestStream(stream.Id, source, 65536, long.MaxValue);
                var first = await reader
                    .ReadEventAsync(requestToken)
                    .ConfigureAwait(false);
                var fields = await DecodeAsync(
                    stream.Id,
                    first.EncodedFields ?? throw new Http3ProtocolException(0x105, "Missing initial fields."),
                    requestToken
                ).ConfigureAwait(false);
                Http2RequestHeaders request;

                try
                {
                    request = Http2RequestHeaders.Parse(new Http2HeaderBlock(0, false, fields, 0), true);
                }

                catch (Http2ProtocolException error)
                {
                    throw new Http3StreamException(stream.Id, 0x10e, error.Message);
                }

                reader.ConfirmHeaders(request.ContentLength);
                using var exchange = new Http3QuicExchange(stream, reader, request, scope, requestToken)
                {
                    PriorityState = _priorities.Headers(stream.Id, request.Headers["priority"])
                };
                using var datagrams = request.Method == "CONNECT" && request.Protocol != null
                    ? _datagrams?.Open(
                        stream.Id,
                        () => !reads.IsCompleted,
                        () => exchange.CanSendDatagrams && !writes.IsCompleted
                    )
                    : null;
                exchange.Datagrams = datagrams;

                if (datagrams != null)
                    _ = reads.ContinueWith(
                        static (_, state) => (state as Http3DatagramChannel)?.CompleteReceive(),
                        datagrams,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    );

                lock (_sync)
                {
                    if (_draining)
                        throw new Http3StreamException(stream.Id, 0x10b, "Request arrived during connection drain.");

                    _highestRequestId = Math.Max(_highestRequestId, stream.Id);
                    _admitted.Add(stream.Id);
                }

                if (request.Method != "CONNECT") stream.EnableApplicationBodyCancellation(requestToken);

                var outcome = WebTransportRequestOutcome.NotWebTransport;

                if (_webTransport != null)
                {
                    (outcome, webTransport) = await _webTransport
                        .AdmitAsync(stream.Id, request.Method, request.Protocol, exchange, requestToken)
                        .ConfigureAwait(false);

                    if (outcome == WebTransportRequestOutcome.RequirementsNotMet)
                        throw new Http3StreamException(
                            stream.Id,
                            WebTransportProtocol.H3MessageError,
                            "This connection did not negotiate the WebTransport requirements of the request."
                        );

                    exchange.WebTransport = webTransport;
                }

                // Section 5.2: a session beyond the connection's limit is refused before dispatch.
                if (outcome == WebTransportRequestOutcome.TooManySessions)
                    await exchange
                        .SendHeadersAsync([new HpackField(":status", "429")], true, requestToken)
                        .ConfigureAwait(false);

                else
                {
                    // Isolate even callbacks that block before returning their Task.
                    if (Interlocked.Increment(ref _applicationCount) > MaximumApplications)
                    {
                        Interlocked.Decrement(ref _applicationCount);
                        throw new Http3StreamException(stream.Id, 0x107, "Too many outstanding application callbacks.");
                    }

                    var application = DispatchApplicationAsync(exchange, requestToken);

                    try
                    {
                        await application
                            .WaitAsync(requestToken)
                            .ConfigureAwait(false);
                    }

                    catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
                    {
                        // A detached callback may fail later. Observe that failure
                        // without retaining or accessing the disposed connection.
                        _ = application.ContinueWith(
                            static completed =>
                            {
                                _ = completed.Exception;
                            },
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );
                        throw;
                    }
                }

                if (!exchange.Ended)
                    await exchange
                        .CompleteAsync(requestToken)
                        .ConfigureAwait(false);

                // Most bodiless requests carry FIN with their HEADERS. Consuming an
                // already received end avoids STOP_SENDING and a QPACK Stream
                // Cancellation; unread or still-arriving input is abandoned as before.
                if (exchange.Body.TryEndWithoutWaiting(out var abandoned)) scope.InputComplete = true;

                else
                {
                    stream.Abort(Http3AbortDirection.Read, 0x100);
                    // The abort (or stream disposal) completes an abandoned read.
                    _ = abandoned?.ContinueWith(
                        static completed =>
                        {
                            _ = completed.Exception;
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    );
                }

                if (exchange.CloseConnectionAfterResponse) _applicationDrain.Cancel();
            }

            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                AbortStream(stream, 0x10c);
            }

            finally
            {
                // A session the handler never accepted ends with its request.
                webTransport?.EndPending();
                CancelRequests(requestStop);
                var unexpected = UnexpectedDirectionFault(reads, requestStop)
                    ?? UnexpectedDirectionFault(writes, requestStop);

                if (unexpected != null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo
                        .Capture(unexpected)
                        .Throw();
            }
        }

        private async Task DispatchApplicationAsync(Http3QuicExchange exchange, CancellationToken token)
        {
            try
            {
                // Isolate even callbacks that block before returning their Task,
                // unless the dispatcher is the listener's known non-blocking one.
                if (!_dispatchInline) await Task.Yield();

                token.ThrowIfCancellationRequested();
                await _dispatch(exchange).ConfigureAwait(false);
            }

            finally
            {
                // A reset or completed request whose handler still runs keeps its slot.
                var handler = exchange.Handler;

                if (handler == null || handler.IsCompleted) Interlocked.Decrement(ref _applicationCount);

                else
                    _ = handler.ContinueWith(
                        static (_, state) => Interlocked.Decrement(
                            ref ((Http3QuicConnection)(state
                                ?? throw new InvalidOperationException("Missing connection.")))._applicationCount
                        ),
                        this,
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default
                    );
            }
        }

        // Observes one transport direction for the request's lifetime with a single
        // continuation, instead of a watcher task per direction. FIN is a normal
        // half-close; a QUIC fault (peer reset, STOP_SENDING, connection loss)
        // cancels the request. The source is never disposed, so a continuation
        // that runs after the request ended only repeats an earlier cancellation.
        internal static void WatchRequestDirection(Task direction, CancellationTokenSource requestStop)
        {
            if (direction.IsCompleted)
            {
                ObserveDirection(direction, requestStop);
                return;
            }

            direction
                .ConfigureAwait(false)
                .GetAwaiter()
                .UnsafeOnCompleted(() => ObserveDirection(direction, requestStop));
        }

        // A failure the receive side reports for this side's own abort is a local
        // read abort, including application cancellation. Peer RESET_STREAM and
        // connection failures still cancel the request and its output.
        internal static void WatchRequestReadDirection(Task direction, CancellationTokenSource requestStop)
        {
            if (direction.IsCompleted)
            {
                ObserveDirection(direction, requestStop, true);
                return;
            }

            direction
                .ConfigureAwait(false)
                .GetAwaiter()
                .UnsafeOnCompleted(() => ObserveDirection(direction, requestStop, true));
        }

        private static void ObserveDirection(Task direction, CancellationTokenSource requestStop, bool reading = false)
        {
            if (
                direction.IsFaulted && direction.Exception?.InnerException is Exception error
                && Http3TransportFailure.IsTransport(error)
                && (!reading || !Http3TransportFailure.IsLocalReadAbort(error))
            ) CancelRequests(requestStop);
        }

        // A completed direction that failed in some other way is reported to the
        // stream owner when the request ends, as an awaited watcher would have.
        internal static Exception? UnexpectedDirectionFault(Task direction, CancellationTokenSource requestStop)
        {
            if (!direction.IsCompleted || direction.IsCompletedSuccessfully) return null;

            if (direction.IsCanceled)
                return requestStop.IsCancellationRequested ? null : new TaskCanceledException(direction);

            var error = direction.Exception?.InnerException;

            if (
                Http3TransportFailure.IsTransport(error)
                || (error is OperationCanceledException && requestStop.IsCancellationRequested)
            )
                return null;

            return error;
        }

        private async Task<HpackField[]> DecodeRequestAsync(
            long streamId,
            byte[] wire,
            CancellationToken caller,
            CancellationToken lifetime
        )
        {
            if (!caller.CanBeCanceled || caller == lifetime)
                return await DecodeAsync(streamId, wire, lifetime).ConfigureAwait(false);

            using var combined = CancellationTokenSource.CreateLinkedTokenSource(caller, lifetime);
            return await DecodeAsync(streamId, wire, combined.Token).ConfigureAwait(false);
        }

        private async Task<HpackField[]> DecodeAsync(long streamId, byte[] wire, CancellationToken token)
        {
            Task<HpackField[]> pending;

            lock (_decoderSync)
            {
                _token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                var decoded = _decoder.Submit(streamId, wire);

                // Static-only sections produce no acknowledgment to send.
                if (_decoder.HasFeedback) SignalFeedback();

                if (decoded != null) return decoded;

                var completion = new TaskCompletionSource<HpackField[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                _pending.Add(streamId, completion);
                pending = completion.Task;
            }

            return await pending
                .WaitAsync(token)
                .ConfigureAwait(false);
        }

        private void CancelDecode(long streamId)
        {
            Http3ProtocolException? failure = null;

            lock (_decoderSync)
            {
                if (_pending.Remove(streamId, out var pending)) pending.TrySetCanceled();

                if (_token.IsCancellationRequested) return;

                try
                {
                    _decoder.Cancel(streamId);
                    SignalFeedback();
                }

                catch (Http3ProtocolException error)
                {
                    failure = error;
                }
            }

            // Connection cancellation runs request callbacks; never under the gate.
            if (failure != null) Fail(failure);
        }

        private void SignalFeedback()
        {
            if (_feedbackReady.CurrentCount == 0) _feedbackReady.Release();
        }

        private async Task ReadEncoderAsync(Http3TransportStream stream)
        {
            var bytes = new byte[4096];

            while (true)
            {
                var count = await stream
                    .ReadAsync(bytes, _token)
                    .ConfigureAwait(false);

                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK encoder stream closed.");

                lock (_decoderSync)
                {
                    foreach (var ready in _decoder.FeedEncoder(bytes, 0, count))
                    {
                        if (!_pending.Remove(ready.StreamId, out var pending))
                            throw new Http3ProtocolException(0x102, "Missing blocked field-section owner.");

                        if (ready.Error != null) pending.TrySetException(ready.Error);

                        else pending.TrySetResult(ready.Fields);
                    }

                    SignalFeedback();
                }
            }
        }

        private async Task ReadDecoderAsync(Http3TransportStream stream)
        {
            var bytes = new byte[1024];

            while (true)
            {
                var count = await stream
                    .ReadAsync(bytes, _token)
                    .ConfigureAwait(false);

                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK decoder stream closed.");

                _encoderFeedback.Feed(bytes, 0, count);
            }
        }

        private async Task ReadControlAsync(Http3TransportStream stream)
        {
            var control = new Http3ControlStream(stream, false, _webTransport != null);

            while (true)
            {
                var received = await control
                    .ReadAsync(_token)
                    .ConfigureAwait(false);

                if (received.Type == 0xf0700)
                    _priorities.Update(
                        received.Identifier,
                        received.Priority ?? throw new Http3ProtocolException(0x102, "Missing parsed priority.")
                    );

                if (received.Type == 4)
                {
                    var settings = control.Settings
                        ?? throw new Http3ProtocolException(0x102, "Missing peer settings.");
                    Volatile.Write(ref _peer, settings);
                    _datagrams?.SetPeerEnabled(settings.Datagrams);
                    _webTransport?.PeerSettingsReceived(control.WebTransportSettings);
                }

                // No push IDs have been promised by this server yet.
                if (received.Type == 3 || received.Type == 0xf0701)
                    throw new Http3ProtocolException(0x108, "Control frame refers to an unpromised push.");
            }
        }

        private byte[] EncodeResponse(long streamId, HpackField[] fields)
        {
            lock (_encoderSync)
            {
                _token.ThrowIfCancellationRequested();
                var peer = Volatile.Read(ref _peer);
                var wire = _responseEncoder.Encode(
                    streamId,
                    fields,
                    peer,
                    65536,
                    (int)Math.Min(65536, peer.MaximumFieldSectionSize)
                );

                if (_responseEncoder.PendingBytes != 0 && _encoderReady.CurrentCount == 0) _encoderReady.Release();

                return wire;
            }
        }

        private async Task WriteEncoderAsync()
        {
            Http3TransportStream? stream = null;
            var watch = Task.CompletedTask;

            try
            {
                while (true)
                {
                    await _encoderReady
                        .WaitAsync(_token)
                        .ConfigureAwait(false);

                    if (stream == null)
                    {
                        stream = await _connection.Value
                            .OpenOutboundStreamAsync(Http3StreamType.Unidirectional, _token)
                            .ConfigureAwait(false);
                        await stream
                            .WriteAsync(new byte[] { 2 }, _token)
                            .ConfigureAwait(false);
                        watch = WatchCriticalOutputAsync(stream);
                    }

                    while (true)
                    {
                        byte[]? bytes;

                        lock (_encoderSync) bytes = _responseEncoder.DequeueInstructions();

                        if (bytes == null) break;

                        await stream
                            .WriteAsync(bytes, _token)
                            .ConfigureAwait(false);
                    }
                }
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                CriticalOutputFailed(error);
            }

            finally
            {
                try
                {
                    if (stream != null)
                        await stream
                            .DisposeAsync()
                            .ConfigureAwait(false);
                }

                finally
                {
                    await watch.ConfigureAwait(false);
                }
            }
        }

        private async Task WriteFeedbackAsync(Http3TransportStream stream)
        {
            try
            {
                while (true)
                {
                    await _feedbackReady
                        .WaitAsync(_token)
                        .ConfigureAwait(false);
                    byte[] bytes;

                    lock (_decoderSync) bytes = _decoder.DrainFeedback();

                    if (bytes.Length != 0)
                        await stream
                            .WriteAsync(bytes, _token)
                            .ConfigureAwait(false);
                }
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                CriticalOutputFailed(error);
            }
        }

        private async Task WatchCriticalOutputAsync(Http3TransportStream stream)
        {
            try
            {
                await stream.WritesClosed
                    .WaitAsync(_token)
                    .ConfigureAwait(false);

                if (!_token.IsCancellationRequested)
                    Fail(new Http3ProtocolException(0x104, "Peer closed critical output."));
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                CriticalOutputFailed(error);
            }
        }

        // The failure policy shared by the critical output streams: shutdown cancellation is quiet,
        // connection closure is not a critical-stream reset, and anything else closes the connection
        // with H3_CLOSED_CRITICAL_STREAM.
        private void CriticalOutputFailed(Exception error)
        {
            if (error is OperationCanceledException && _token.IsCancellationRequested) return;

            if (Http3TransportFailure.IsTransport(error) && !Http3TransportFailure.IsStream(error))
                CancelRequests(_stop);

            else if (!_token.IsCancellationRequested)
                Fail(new Http3ProtocolException(0x104, error.Message));
        }

        private Task<long?> ReadStreamTypeAsync(Http3TransportStream stream) => ReadIntegerAsync(stream, _token);

        // Reads one variable-length integer without reading past it. A stream that
        // ends inside the integer yields no value.
        internal static async Task<long?> ReadIntegerAsync(System.IO.Stream stream, CancellationToken token) =>
            (await ReadIntegerPrefixAsync(stream, token).ConfigureAwait(false)).Value;

        // As ReadIntegerAsync, and also returns the bytes it read, for replay.
        private static async Task<(long? Value, byte[] Bytes)> ReadIntegerPrefixAsync(
            System.IO.Stream stream,
            CancellationToken token
        )
        {
            var bytes = new byte[8];
            var count = await stream
                .ReadAsync(bytes.AsMemory(0, 1), token)
                .ConfigureAwait(false);

            if (count == 0) return (null, []);

            var length = 1 << (bytes[0] >> 6);

            while (count < length)
            {
                var read = await stream
                    .ReadAsync(bytes.AsMemory(count, length - count), token)
                    .ConfigureAwait(false);

                if (read == 0)
                    return (
                        null,
                        bytes
                            .AsSpan(0, count)
                            .ToArray()
                    );

                count += read;
            }

            var offset = 0;
            return (
                QuicInteger.Read(bytes, ref offset, length),
                bytes
                    .AsSpan(0, length)
                    .ToArray()
            );
        }

        private void RequestFailed(Http3TransportStream stream, Exception error)
        {
            if (_token.IsCancellationRequested) return;

            if (error is Http3ProtocolException protocol) Fail(protocol);

            else AbortStream(stream, error is Http3StreamException scoped ? scoped.ErrorCode : 0x10c);
        }

        private static void AbortStream(Http3TransportStream stream, long code)
        {
            try
            {
                stream.Abort(Http3AbortDirection.Both, code);
            }

            catch (ObjectDisposedException) { }
        }

        private static void CancelRequests(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }

            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                error.Log("HTTP/3 connection", "Exception thrown by a request cancellation callback.");
            }
        }

        private void CancelActiveRequests()
        {
            RequestScope[] active;

            lock (_sync) active = [.. _requests.Values];

            // Request callbacks run outside the tracking gate.
            foreach (var scope in active) CancelRequests(scope.Stop);
        }

        // Per-request state owned by the connection. It also serves as the
        // exchange's owner, so no per-request delegates capture the stream.
        internal sealed class RequestScope : IHttp3ExchangeOwner
        {
            private readonly Http3QuicConnection _connection;

            internal RequestScope(Http3QuicConnection connection, Http3TransportStream stream)
            {
                _connection = connection;
                Stream = stream;
            }

            internal Http3TransportStream Stream { get; }
            internal CancellationTokenSource Stop { get; } = new();

            // Set by the request worker when its input was read to FIN.
            internal bool InputComplete { get; set; }

            public Task<HpackField[]> DecodeAsync(byte[] wire, CancellationToken token) =>
                _connection.DecodeRequestAsync(Stream.Id, wire, token, Stop.Token);

            public byte[] Encode(HpackField[] fields) => _connection.EncodeResponse(Stream.Id, fields);

            public void Failed(Exception error) => _connection.RequestFailed(Stream, error);

            public bool IsLocalReadAbort(Exception error) => Http3TransportFailure.IsLocalReadAbort(error);
        }

        private void Fail(Exception error)
        {
            if (_token.IsCancellationRequested) return;

            Interlocked.CompareExchange(ref _failure, error, null);
            CancelRequests(_stop);
        }

        public void Dispose()
        {
            _stopRequests.Dispose();
            _datagrams?.Dispose();

            lock (_encoderSync)
            {
                _responseEncoder.Clear();
                _encoderFeedback.Abort();
            }

            _encoderReady.Dispose();
            _priorities.Clear();
            _decoder.Dispose();
            _feedbackReady.Dispose();
            _applicationDrain.Dispose();
            _stop.Dispose();
        }
    }
}
#endif
