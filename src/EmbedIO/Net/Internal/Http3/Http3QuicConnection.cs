#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;
using EmbedIO.Net.Internal.Http2;

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
        private readonly Dictionary<long, Task> _workers = new();
        private readonly Dictionary<long, RequestScope> _requests = new();
        private readonly HashSet<long> _admitted = new();
        private readonly Http3PriorityState _priorities = new(256);
        private readonly Dictionary<long, TaskCompletionSource<HpackField[]>> _pending = new();
        private readonly CancellationTokenRegistration _stopRequests;
        private readonly bool _dispatchInline;
        private readonly QpackDecoder _decoder = new(4096, 16, 65536, 65536, 1048576, 65536);
        private readonly QpackEncoderFeedback _encoderFeedback = new(256, 4096);
        private readonly QpackResponseEncoder _responseEncoder;
        private readonly SemaphoreSlim _encoderReady = new(0, 1);
        private readonly SemaphoreSlim _feedbackReady = new(0, 1);
        private Http3PeerSettings _peer = Http3PeerSettings.Parse(Array.Empty<byte>());
        private Exception? _failure;
        private int _critical;
        private int _applicationCount;
        private CancellationToken _drainToken;
        private static readonly TimeSpan ApplicationDrainTimeout = TimeSpan.FromSeconds(30);
        private TimeSpan _drainTimeout = ApplicationDrainTimeout;
        private readonly CancellationTokenSource _applicationDrain = new();
        private bool _draining;
        private long _highestRequestId = -4;

        private Http3QuicConnection(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token)
            : this(connection, dispatch, token, false) { }
        private Http3QuicConnection(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token, bool dispatchInline)
            : this(new SystemQuicTransportConnection(connection), dispatch, token, dispatchInline) { }
        private Http3QuicConnection(Http3TransportConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token, bool dispatchInline)
        {
            _connection = new BorrowedResource<Http3TransportConnection>(connection);
            _dispatch = dispatch;
            _dispatchInline = dispatchInline;
            _responseEncoder = new QpackResponseEncoder(_encoderFeedback, 4096, 65536);
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _token = _stop.Token;
            // One registration fans connection cancellation out to every active
            // request. Per-request linked sources would all contend on this token.
            _stopRequests = _token.UnsafeRegister(static state => (state as Http3QuicConnection)?.CancelActiveRequests(), this);
        }
        internal static async Task RunNativeAsync(MsQuicNativeConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            // Acceptance and credentials must be configured before the native
            // handshake. The adapter owns this already-connected connection.
            await using var transport = new MsQuicTransportConnection(connection);
            using var session = new Http3QuicConnection(transport, dispatch, token, false);
            await session.RunCoreAsync().ConfigureAwait(false);
        }
        internal static async Task RunNativeForListenerAsync(MsQuicNativeConnection connection, Func<Http3QuicExchange, Task> dispatch,
            CancellationToken abortToken, CancellationToken drainToken, TimeSpan drainTimeout)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            if (drainTimeout <= TimeSpan.Zero || drainTimeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(drainTimeout));
            await using var transport = new MsQuicTransportConnection(connection);
            using var session = new Http3QuicConnection(transport, dispatch, abortToken, true);
            session._drainToken = drainToken;
            session._drainTimeout = drainTimeout;
            await session.RunCoreAsync().ConfigureAwait(false);
        }
        internal static async Task RunAsync(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            await using (connection.ConfigureAwait(false))
            using (var session = new Http3QuicConnection(connection, dispatch, token))
                await session.RunCoreAsync().ConfigureAwait(false);
        }
        internal static Task RunWithDrainAsync(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch,
            CancellationToken abortToken, CancellationToken drainToken, TimeSpan drainTimeout)
            => RunWithDrainCoreAsync(connection, dispatch, abortToken, drainToken, drainTimeout, false);
        // The listener's dispatch only builds a context and queues it; it never
        // blocks before returning its task, so it may run on the stream worker
        // instead of paying a second thread-pool hop per request.
        internal static Task RunForListenerAsync(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch,
            CancellationToken abortToken, CancellationToken drainToken, TimeSpan drainTimeout)
            => RunWithDrainCoreAsync(connection, dispatch, abortToken, drainToken, drainTimeout, true);
        private static async Task RunWithDrainCoreAsync(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch,
            CancellationToken abortToken, CancellationToken drainToken, TimeSpan drainTimeout, bool dispatchInline)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            if (drainTimeout <= TimeSpan.Zero || drainTimeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(drainTimeout));
            await using (connection.ConfigureAwait(false))
            using (var session = new Http3QuicConnection(connection, dispatch, abortToken, dispatchInline))
            {
                session._drainToken = drainToken;
                session._drainTimeout = drainTimeout;
                await session.RunCoreAsync().ConfigureAwait(false);
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
                control = await _connection.Value.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, startup.Token).ConfigureAwait(false);
                // Control type, SETTINGS: capacity 4096, field limit 65536, blocked streams 16, extended CONNECT enabled.
                await control.WriteAsync(new byte[] { 0, 4, 12, 1, 0x50, 0, 6, 0x80, 1, 0, 0, 7, 16, 8, 1 }, startup.Token).ConfigureAwait(false);
                feedback = await _connection.Value.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, startup.Token).ConfigureAwait(false);
                await feedback.WriteAsync(new byte[] { 3 }, startup.Token).ConfigureAwait(false);
                background = new[] { WriteEncoderAsync(), WriteFeedbackAsync(feedback), WatchCriticalOutputAsync(control), WatchCriticalOutputAsync(feedback), WatchDrainAsync(control) };
                while (!_token.IsCancellationRequested)
                {
                    var stream = await _connection.Value.AcceptInboundStreamAsync(_token).ConfigureAwait(false);
                    bool rejected;
                    bool draining;
                    lock (_sync)
                    {
                        draining = _draining && stream.Type == QuicStreamType.Bidirectional;
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
                        await stream.DisposeAsync().ConfigureAwait(false);
                        if (!draining) throw new Http3ProtocolException(0x107, "Too many active HTTP/3 stream workers.");
                    }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (OperationCanceledException) { Fail(new Http3ProtocolException(0x103, "Peer did not provide critical-stream credit before startup deadline.")); }
            catch (QuicException) { CancelRequests(_stop); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { Fail(error); }
            finally
            {
                try
                {
                    CancelRequests(_stop);
                    lock (_decoderSync)
                    {
                        foreach (var pending in _pending.Values) pending.TrySetCanceled(_token);
                        _pending.Clear();
                    }
                    using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    var code = _failure is Http3ProtocolException protocol ? protocol.ErrorCode : _failure == null ? 0x100 : 0x102;
                    try { await _connection.Value.CloseAsync(code, closeDeadline.Token).ConfigureAwait(false); }
                    catch (Exception error) when (error is QuicException or OperationCanceledException) { }
                    Task[] workers;
                    lock (_sync) workers = _workers.Values.ToArray();
                    await Task.WhenAll(workers.Concat(background)).ConfigureAwait(false);
                }
                finally
                {
                    // A failed worker or callback must not retain either critical
                    // stream's native reference after connection shutdown.
                    try { if (feedback != null) await feedback.DisposeAsync().ConfigureAwait(false); }
                    finally { if (control != null) await control.DisposeAsync().ConfigureAwait(false); }
                }
            }
            if (_failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }
        private async Task WatchDrainAsync(Http3TransportStream control)
        {
            using var requested = CancellationTokenSource.CreateLinkedTokenSource(_token, _drainToken, _applicationDrain.Token);
            try { await Task.Delay(Timeout.Infinite, requested.Token).ConfigureAwait(false); }
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
                    unprocessed = _requests.Where(item => !_admitted.Contains(item.Key)).Select(item => item.Value.Stream).ToArray();
                    accepted = _workers.Where(item => (item.Key & 3) == 0).Select(item => item.Value).ToArray();
                }
                foreach (var request in unprocessed) AbortStream(request, 0x10b);
                // Exhausted request stream IDs need no GOAWAY (RFC 9114 section 5.2).
                if (cutoff <= QuicInteger.Maximum)
                {
                    var frame = new byte[10];
                    var length = QuicInteger.Write(frame, 2, cutoff);
                    frame[0] = 7; frame[1] = (byte)length;
                    await control.WriteAsync(frame.AsMemory(0, length + 2), deadline.Token).ConfigureAwait(false);
                }
                await Task.WhenAll(accepted).WaitAsync(deadline.Token).ConfigureAwait(false);
                // WriteAsync releases the send buffer; it does not confirm receipt.
                // Keep critical streams alive for peer-driven closure, bounded by
                // the drain deadline, rather than immediately overtaking GOAWAY.
                await Task.Delay(Timeout.Infinite, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (QuicException error) when (error.QuicError is not (QuicError.StreamAborted or QuicError.OperationAborted))
            { CancelRequests(_stop); } // Connection closure is not a critical-stream reset.
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(error); }
            finally { CancelRequests(_stop); }
        }
        private async Task ProcessStreamAsync(Http3TransportStream stream, RequestScope? scope)
        {
            var critical = false;
            var streamId = stream.Id;
            // Leave the accept loop at once. Yielding queues this state machine
            // itself, without the closure and wrapper tasks of Task.Run.
            await Task.Yield();
            try
            {
                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    if (scope == null) throw new Http3ProtocolException(0x103, "Invalid request stream initiator.");
                    await ProcessRequestAsync(stream, scope).ConfigureAwait(false);
                }
                else
                {
                    var type = await ReadStreamTypeAsync(stream).ConfigureAwait(false);
                    if (!type.HasValue) return; // A partial/absent type may be abandoned.
                    if (type == 1) throw new Http3ProtocolException(0x103, "Client initiated a push stream.");
                    var flag = type == 0 ? 1 : type == 2 ? 2 : type == 3 ? 4 : 0;
                    if (flag == 0) { stream.Abort(QuicAbortDirection.Read, 0x103); return; }
                    critical = true;
                    if ((Interlocked.Or(ref _critical, flag) & flag) != 0) throw new Http3ProtocolException(0x103, "Duplicate critical stream.");
                    if (type == 0) await ReadControlAsync(stream).ConfigureAwait(false);
                    else if (type == 2) await ReadEncoderAsync(stream).ConfigureAwait(false);
                    else await ReadDecoderAsync(stream).ConfigureAwait(false);
                }
            }
            catch (Http3ProtocolException error) { Fail(error); }
            catch (Http3StreamException error) { AbortStream(stream, error.ErrorCode); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (QuicException error)
            {
                if (!_token.IsCancellationRequested)
                {
                    if (error.QuicError is QuicError.StreamAborted or QuicError.OperationAborted)
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
                    if (scope?.InputComplete != true) CancelDecode(streamId);
                    _priorities.Close(streamId);
                }
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                { if (!_token.IsCancellationRequested) Fail(error); }
                finally
                {
                    lock (_sync) { _workers.Remove(streamId); _requests.Remove(streamId); _admitted.Remove(streamId); }
                }
            }
        }
        private async Task ProcessRequestAsync(Http3TransportStream stream, RequestScope scope)
        {
            var requestStop = scope.Stop;
            var requestToken = requestStop.Token;
            // FIN is a successful half-close. Only faulted direction completion
            // cancels the request; this observes resets even while QPACK is blocked.
            var reads = stream.ReadsClosed;
            var writes = stream.WritesClosed;
            WatchRequestDirection(reads, requestStop);
            WatchRequestDirection(writes, requestStop);
            try
            {
                var reader = new Http3RequestStream(stream.Id, stream, 65536, long.MaxValue);
                var first = await reader.ReadEventAsync(requestToken).ConfigureAwait(false);
                var fields = await DecodeAsync(stream.Id, first.EncodedFields ?? throw new Http3ProtocolException(0x105, "Missing initial fields."), requestToken).ConfigureAwait(false);
                Http2RequestHeaders request;
                try { request = Http2RequestHeaders.Parse(new Http2HeaderBlock(0, false, fields, 0), true); }
                catch (Http2ProtocolException error) { throw new Http3StreamException(stream.Id, 0x10e, error.Message); }
                reader.ConfirmHeaders(request.ContentLength);
                using var exchange = new Http3QuicExchange(stream, reader, request, scope, requestToken)
                { PriorityState = _priorities.Headers(stream.Id, request.Headers["priority"]) };
                lock (_sync)
                {
                    if (_draining) throw new Http3StreamException(stream.Id, 0x10b, "Request arrived during connection drain.");
                    _highestRequestId = Math.Max(_highestRequestId, stream.Id);
                    _admitted.Add(stream.Id);
                }
                // Isolate even callbacks that block before returning their Task.
                if (Interlocked.Increment(ref _applicationCount) > 256)
                {
                    Interlocked.Decrement(ref _applicationCount);
                    throw new Http3StreamException(stream.Id, 0x107, "Too many outstanding application callbacks.");
                }
                var application = DispatchApplicationAsync(exchange, requestToken);
                try { await application.WaitAsync(requestToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
                {
                    // A detached callback may fail later. Observe that failure
                    // without retaining or accessing the disposed connection.
                    _ = application.ContinueWith(static completed => { _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    throw;
                }
                if (!exchange.Ended) await exchange.CompleteAsync(requestToken).ConfigureAwait(false);
                // Most bodiless requests carry FIN with their HEADERS. Consuming an
                // already received end avoids STOP_SENDING and a QPACK Stream
                // Cancellation; unread or still-arriving input is abandoned as before.
                if (exchange.Body.TryEndWithoutWaiting(out var abandoned)) scope.InputComplete = true;
                else
                {
                    stream.Abort(QuicAbortDirection.Read, 0x100);
                    // The abort (or stream disposal) completes an abandoned read.
                    _ = abandoned?.ContinueWith(static completed => { _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                if (exchange.CloseConnectionAfterResponse) _applicationDrain.Cancel();
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            { AbortStream(stream, 0x10c); }
            finally
            {
                CancelRequests(requestStop);
                var unexpected = UnexpectedDirectionFault(reads, requestStop) ?? UnexpectedDirectionFault(writes, requestStop);
                if (unexpected != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected).Throw();
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
            finally { Interlocked.Decrement(ref _applicationCount); }
        }
        // Observes one transport direction for the request's lifetime with a single
        // continuation, instead of a watcher task per direction. FIN is a normal
        // half-close; a QUIC fault (peer reset, STOP_SENDING, connection loss)
        // cancels the request. The source is never disposed, so a continuation
        // that runs after the request ended only repeats an earlier cancellation.
        internal static void WatchRequestDirection(Task direction, CancellationTokenSource requestStop)
        {
            if (direction.IsCompleted) { ObserveDirection(direction, requestStop); return; }
            direction.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => ObserveDirection(direction, requestStop));
        }
        private static void ObserveDirection(Task direction, CancellationTokenSource requestStop)
        {
            if (direction.IsFaulted && direction.Exception?.InnerException is QuicException) CancelRequests(requestStop);
        }
        // A completed direction that failed in some other way is reported to the
        // stream owner when the request ends, as an awaited watcher would have.
        internal static Exception? UnexpectedDirectionFault(Task direction, CancellationTokenSource requestStop)
        {
            if (!direction.IsCompleted || direction.IsCompletedSuccessfully) return null;
            if (direction.IsCanceled) return requestStop.IsCancellationRequested ? null : new TaskCanceledException(direction);
            var error = direction.Exception?.InnerException;
            if (error is QuicException || (error is OperationCanceledException && requestStop.IsCancellationRequested)) return null;
            return error;
        }
        private async Task<HpackField[]> DecodeRequestAsync(long streamId, byte[] wire, CancellationToken caller, CancellationToken lifetime)
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
                var completion = new TaskCompletionSource<HpackField[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(streamId, completion);
                pending = completion.Task;
            }
            return await pending.WaitAsync(token).ConfigureAwait(false);
        }
        private void CancelDecode(long streamId)
        {
            Http3ProtocolException? failure = null;
            lock (_decoderSync)
            {
                if (_pending.Remove(streamId, out var pending)) pending.TrySetCanceled();
                if (_token.IsCancellationRequested) return;
                try { _decoder.Cancel(streamId); SignalFeedback(); }
                catch (Http3ProtocolException error) { failure = error; }
            }
            // Connection cancellation runs request callbacks; never under the gate.
            if (failure != null) Fail(failure);
        }
        private void SignalFeedback()
        { if (_feedbackReady.CurrentCount == 0) _feedbackReady.Release(); }
        private async Task ReadEncoderAsync(Http3TransportStream stream)
        {
            var bytes = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, _token).ConfigureAwait(false);
                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK encoder stream closed.");
                lock (_decoderSync)
                {
                    foreach (var ready in _decoder.FeedEncoder(bytes, 0, count))
                    {
                        if (!_pending.Remove(ready.StreamId, out var pending)) throw new Http3ProtocolException(0x102, "Missing blocked field-section owner.");
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
                var count = await stream.ReadAsync(bytes, _token).ConfigureAwait(false);
                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK decoder stream closed.");
                _encoderFeedback.Feed(bytes, 0, count);
            }
        }
        private async Task ReadControlAsync(Http3TransportStream stream)
        {
            var control = new Http3ControlStream(stream, false);
            while (true)
            {
                var received = await control.ReadAsync(_token).ConfigureAwait(false);
                if (received.Type == 0xf0700)
                    _priorities.Update(received.Identifier, received.Priority ?? throw new Http3ProtocolException(0x102, "Missing parsed priority."));
                if (received.Type == 4) Volatile.Write(ref _peer, control.Settings ?? throw new Http3ProtocolException(0x102, "Missing peer settings."));
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
                var wire = _responseEncoder.Encode(streamId, fields, peer, 65536, (int)Math.Min(65536, peer.MaximumFieldSectionSize));
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
                    await _encoderReady.WaitAsync(_token).ConfigureAwait(false);
                    if (stream == null)
                    {
                        stream = await _connection.Value.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, _token).ConfigureAwait(false);
                        await stream.WriteAsync(new byte[] { 2 }, _token).ConfigureAwait(false);
                        watch = WatchCriticalOutputAsync(stream);
                    }
                    while (true)
                    {
                        byte[]? bytes;
                        lock (_encoderSync) bytes = _responseEncoder.DequeueInstructions();
                        if (bytes == null) break;
                        await stream.WriteAsync(bytes, _token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (QuicException error) when (error.QuicError is not (QuicError.StreamAborted or QuicError.OperationAborted))
            { CancelRequests(_stop); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, error.Message)); }
            finally
            {
                try { if (stream != null) await stream.DisposeAsync().ConfigureAwait(false); }
                finally { await watch.ConfigureAwait(false); }
            }
        }
        private async Task WriteFeedbackAsync(Http3TransportStream stream)
        {
            try
            {
                while (true)
                {
                    await _feedbackReady.WaitAsync(_token).ConfigureAwait(false);
                    byte[] bytes;
                    lock (_decoderSync) bytes = _decoder.DrainFeedback();
                    if (bytes.Length != 0) await stream.WriteAsync(bytes, _token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (QuicException error) when (error.QuicError is not (QuicError.StreamAborted or QuicError.OperationAborted))
            { CancelRequests(_stop); } // Connection closure is not a critical-stream reset.
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, error.Message)); }
        }
        private async Task WatchCriticalOutputAsync(Http3TransportStream stream)
        {
            try
            {
                await stream.WritesClosed.WaitAsync(_token).ConfigureAwait(false);
                if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, "Peer closed critical output."));
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (QuicException error) when (error.QuicError is not (QuicError.StreamAborted or QuicError.OperationAborted))
            { CancelRequests(_stop); } // Connection closure is not a critical-stream reset.
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, error.Message)); }
        }
        private async Task<long?> ReadStreamTypeAsync(Http3TransportStream stream)
        {
            var bytes = new byte[8];
            var count = await stream.ReadAsync(bytes.AsMemory(0, 1), _token).ConfigureAwait(false);
            if (count == 0) return null;
            var length = 1 << (bytes[0] >> 6);
            while (count < length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count, length - count), _token).ConfigureAwait(false);
                if (read == 0) return null;
                count += read;
            }
            var offset = 0;
            return QuicInteger.Read(bytes, ref offset, length);
        }
        private void RequestFailed(Http3TransportStream stream, Exception error)
        {
            if (_token.IsCancellationRequested) return;
            if (error is Http3ProtocolException protocol) Fail(protocol);
            else AbortStream(stream, error is Http3StreamException scoped ? scoped.ErrorCode : 0x10c);
        }
        private static void AbortStream(Http3TransportStream stream, long code)
        {
            try { stream.Abort(QuicAbortDirection.Both, code); }
            catch (ObjectDisposedException) { }
        }
        private static void CancelRequests(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                error.Log("HTTP/3 connection", "Exception thrown by a request cancellation callback.");
            }
        }
        private void CancelActiveRequests()
        {
            RequestScope[] active;
            lock (_sync) active = _requests.Values.ToArray();
            // Request callbacks run outside the tracking gate.
            foreach (var scope in active) CancelRequests(scope.Stop);
        }
        // Per-request state owned by the connection. It also serves as the
        // exchange's owner, so no per-request delegates capture the stream.
        internal sealed class RequestScope : IHttp3ExchangeOwner
        {
            private readonly Http3QuicConnection _connection;
            internal RequestScope(Http3QuicConnection connection, Http3TransportStream stream) { _connection = connection; Stream = stream; }
            internal Http3TransportStream Stream { get; }
            internal CancellationTokenSource Stop { get; } = new();
            // Set by the request worker when its input was read to FIN.
            internal bool InputComplete { get; set; }
            public Task<HpackField[]> DecodeAsync(byte[] wire, CancellationToken token)
                => _connection.DecodeRequestAsync(Stream.Id, wire, token, Stop.Token);
            public byte[] Encode(HpackField[] fields) => _connection.EncodeResponse(Stream.Id, fields);
            public void Failed(Exception error) => _connection.RequestFailed(Stream, error);
        }
        private void Fail(Exception error)
        {
            if (_token.IsCancellationRequested) return;
            Interlocked.CompareExchange(ref _failure, error, null);
            CancelRequests(_stop);
        }
        public void Dispose() { _stopRequests.Dispose(); lock (_encoderSync) { _responseEncoder.Clear(); _encoderFeedback.Abort(); } _encoderReady.Dispose(); _priorities.Clear(); _decoder.Dispose(); _feedbackReady.Dispose(); _applicationDrain.Dispose(); _stop.Dispose(); }
    }
}
#endif
