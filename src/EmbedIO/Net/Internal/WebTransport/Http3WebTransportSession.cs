#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    // One WebTransport session of an HTTP/3 connection. It is admitted with its
    // CONNECT request, waits there for the handler to accept it, and then runs a
    // capsule reader and a capsule writer on the CONNECT stream until it ends.
    // The writer finishes the stream after a close, or resets it after a fault.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebTransportSession : IWebTransportSessionBackend, IWebTransportPendingSession
    {
        private const int Pending = 0;
        private const int Accepted = 1;
        private const int Ended = 2;
        private readonly object _gate = new();
        private readonly Http3WebTransport _connection;
        private readonly WebTransportSessionCore _core;
        private readonly IMultiplexedTunnelControl _control;
        private readonly long _connectStreamId;
        private readonly CancellationToken _requestToken;
        private readonly Channel<WebTransportStream> _incoming = Channel.CreateUnbounded<WebTransportStream>();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _credit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _output = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HttpTunnel? _tunnel;
        private HttpDatagramChannel? _datagrams;
        private int _state;
        private bool _abrupt;
        private uint? _closeCode;
        private string? _closeReason;

        internal Http3WebTransportSession(
            Http3WebTransport connection,
            WebTransportSessionCore core,
            IMultiplexedTunnelControl control,
            long connectStreamId,
            bool draft02,
            CancellationToken requestToken
        )
        {
            _connection = connection;
            _core = core;
            _control = control;
            _connectStreamId = connectStreamId;
            Draft02 = draft02;
            _requestToken = requestToken;
            core.Owner = this;
        }

        public bool Draft02 { get; }
        public HttpDatagramChannel Datagrams =>
            Volatile.Read(ref _datagrams) ?? throw new InvalidOperationException("The session was not accepted.");
        public Task Completion => _completion.Task;
        public uint? CloseCode => Volatile.Read(ref _state) == Ended ? _closeCode : null;
        public string? CloseReason => Volatile.Read(ref _state) == Ended ? _closeReason : null;

        public WebTransportSession Accept(HttpTunnel tunnel, HttpDatagramChannel datagrams)
        {
            lock (_gate)
            {
                if (_state != Pending)
                    throw new InvalidOperationException("The WebTransport session was already accepted or has ended.");

                _tunnel = tunnel;
                Volatile.Write(ref _datagrams, datagrams);
                _state = Accepted;
            }

            try
            {
                // Streams that arrived before acceptance are delivered now, in order.
                _core.Establish();
            }

            catch (WebTransportException error)
            {
                _core.Terminate(error.ErrorCode);
            }

            // The connection ended the session first; the writer finishes it at once.
            catch (InvalidOperationException) when (_core.State == WebTransportSessionState.Closed)
            {
                _abrupt = true;
            }

            Observe(new ValueTask(RunAsync(tunnel)));
            return new WebTransportSession(this);
        }

        // The request ended before the handler accepted the session: forget it, and
        // reset the streams that were waiting for it.
        internal void EndPending()
        {
            lock (_gate)
            {
                if (_state != Pending) return;

                _state = Ended;
            }

            _abrupt = true;
            _core.Terminate(null);
            Finish();
        }

        public async ValueTask<WebTransportStream?> AcceptStreamAsync(CancellationToken token)
        {
            while (
                await _incoming.Reader
                    .WaitToReadAsync(token)
                    .ConfigureAwait(false)
            )
                if (_incoming.Reader.TryRead(out var stream)) return stream;

            return null;
        }

        public async ValueTask<WebTransportStream> OpenStreamAsync(bool bidirectional, CancellationToken token)
        {
            while (true)
            {
                var credit = WaitForCredit();

                if (_core.TryOpenOutgoingStream(bidirectional)) break;

                if (_core.State == WebTransportSessionState.Closed)
                    throw new InvalidOperationException("The WebTransport session has ended.");

                // A stream limit queued WT_STREAMS_BLOCKED; wait for WT_MAX_STREAMS.
                Signal();
                await credit
                    .WaitAsync(token)
                    .ConfigureAwait(false);
            }

            Http3TransportStream stream;

            try
            {
                stream = await _connection
                    .OpenOutboundStreamAsync(bidirectional, token)
                    .ConfigureAwait(false);
            }

            catch
            {
                _core.ReleaseOutgoingStream(bidirectional);
                throw;
            }

            Http3WebTransportStream? backend = null;
            var handle = new WebTransportStreamHandle(
                stream.Id,
                bidirectional,
                code => backend?.AbortFromSession(code)
            );
            backend = new Http3WebTransportStream(this, stream, handle, bidirectional, true);

            try
            {
                var header = new byte[16];
                var length = bidirectional
                    ? WebTransportProtocol.WriteBidirectionalHeader(header, 0, _core.Id)
                    : WebTransportProtocol.WriteUnidirectionalHeader(header, 0, _core.Id);

                // Draft-16 section 4.4: a reset must still deliver the header. The prefix
                // marks the header reliable; the header is then written like any data.
                if (!Draft02 && _connection.ReliableReset) stream.SetReliablePrefix(header.AsSpan(0, length));

                await stream
                    .WriteAsync(header.AsMemory(0, length), false, token)
                    .ConfigureAwait(false);
            }

            catch
            {
                backend.AbortFromSession(WebTransportProtocol.SessionGone);
                await backend
                    .DisposeAsync()
                    .ConfigureAwait(false);
                throw;
            }

            _core.RegisterOutgoingStream(handle);
            return new WebTransportStream(backend);
        }

        public async Task CloseAsync(uint errorCode, string reason, CancellationToken token)
        {
            if (_core.Close(errorCode, reason)) Signal();

            await _completion.Task
                .WaitAsync(token)
                .ConfigureAwait(false);
        }

        public async Task CloseForContextAsync(CancellationToken token)
        {
            if (Volatile.Read(ref _state) == Pending) return;

            if (_core.Close(0, string.Empty)) Signal();

            try
            {
                await _completion.Task
                    .WaitAsync(token)
                    .ConfigureAwait(false);
            }

            // The context still ends the stream; a canceled close only stops waiting.
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        public Task DisposeAsync() => CloseAsync(0, string.Empty, CancellationToken.None);

        // A peer stream attached to this established session.
        internal void Deliver(WebTransportStreamHandle handle)
        {
            if (handle.Transport is not Http3WebTransport.IncomingStream incoming) return;

            var backend = new Http3WebTransportStream(this, incoming.Stream, handle, true, handle.Bidirectional);

            if (!incoming.Attach(backend)) return;

            if (!_incoming.Writer.TryWrite(new WebTransportStream(backend)))
            {
                backend.AbortFromSession(WebTransportProtocol.SessionGone);
                Observe(backend.DisposeAsync());
            }
        }

        // The application consumed received stream bytes.
        internal void Consumed(int count)
        {
            try
            {
                _core.RecordIncomingData(count);
                _core.ReleaseIncomingData(count);
            }

            catch (WebTransportException error)
            {
                Signal();
                throw new IOException(error.Message, error);
            }

            Signal();
        }

        // Returns how many bytes of requested the session's credit allows now.
        internal async ValueTask<int> ReserveOutgoingDataAsync(int requested, CancellationToken token)
        {
            while (true)
            {
                var credit = WaitForCredit();
                var granted = _core.ReserveOutgoingData(requested);

                if (granted > 0) return (int)granted;

                if (granted < 0) throw new IOException("The WebTransport session has ended.");

                Signal();
                await credit
                    .WaitAsync(token)
                    .ConfigureAwait(false);
            }
        }

        internal void StreamFinished(WebTransportStreamHandle handle)
        {
            _core.StreamFinished(handle);
            Signal();
        }

        // The server is draining: ask the peer to finish (section 4.7).
        internal void Drain()
        {
            if (_core.Drain()) Signal();
        }

        // The connection is ending, or a stream broke the session's limits.
        internal void Terminate(long? errorCode)
        {
            if (errorCode == null) _abrupt = true;

            _core.Terminate(errorCode);
            ReleaseCredit();
            Signal();

            if (Volatile.Read(ref _state) == Pending) EndPending();
        }

        // Wakes the capsule writer, which writes whatever the core has queued.
        internal void Signal() =>
            Volatile
                .Read(ref _output)
                .TrySetResult();

        // Waits for a signal, then arms the next one. The writer drains the queue after
        // this returns, so a capsule queued before the new signal is armed is still written.
        private async Task WaitForOutputAsync(CancellationToken token)
        {
            var current = Volatile.Read(ref _output);
            await current.Task
                .WaitAsync(token)
                .ConfigureAwait(false);
            Interlocked.CompareExchange(ref _output, new(TaskCreationOptions.RunContinuationsAsynchronously), current);
        }

        private Task WaitForCredit()
        {
            lock (_gate) return _credit.Task;
        }

        // Wakes every credit waiter; each checks its own limit again.
        private void ReleaseCredit()
        {
            TaskCompletionSource released;

            lock (_gate)
            {
                released = _credit;
                _credit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            released.TrySetResult();
        }

        private async Task RunAsync(HttpTunnel tunnel)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_requestToken);
            var reading = ReadCapsulesAsync(tunnel.Stream, stop.Token);

            try
            {
                await WriteCapsulesAsync(tunnel, stop.Token).ConfigureAwait(false);
            }

            finally
            {
                lock (_gate) _state = Ended;

                Finish();
            }

            // The writer ended the session's output; the reader goes on until the peer
            // finishes, resets or the request ends, so late capsules are still checked.
            await reading.ConfigureAwait(false);
        }

        private async Task ReadCapsulesAsync(Stream stream, CancellationToken token)
        {
            await Task.Yield();
            var reader = new WebTransportCapsuleReader(stream);

            try
            {
                while (true)
                {
                    var capsule = await reader
                        .ReadAsync(token)
                        .ConfigureAwait(false);

                    if (!capsule.HasValue)
                    {
                        // Section 6: a clean end without a close is a close with code 0.
                        _core.PeerFinished();
                        break;
                    }

                    if (_core.ProcessCapsule(capsule.Value) == WebTransportCapsuleEffect.CreditIncreased)
                        ReleaseCredit();

                    Signal();
                }
            }

            catch (WebTransportException error)
            {
                _core.Terminate(error.ErrorCode);
            }

            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _abrupt = _core.State != WebTransportSessionState.Closed || _abrupt;
                _core.Terminate(null);
            }

            catch (Exception error) when (
                Http3TransportFailure.IsTransport(error)
                || error is IOException or ObjectDisposedException or InvalidOperationException
            )
            {
                if (_core.State != WebTransportSessionState.Closed) _abrupt = true;

                _core.PeerAborted();
            }

            finally
            {
                // Waiters for credit see the closed session and stop waiting.
                ReleaseCredit();
                Signal();
            }
        }

        private async Task WriteCapsulesAsync(HttpTunnel tunnel, CancellationToken token)
        {
            var stream = tunnel.Stream;

            try
            {
                while (true)
                {
                    // Read the state first: everything queued before the session closed is
                    // then written below, including the close capsule itself.
                    var closed = _core.State == WebTransportSessionState.Closed;
                    var wrote = false;

                    while (_core.TryDequeueOutgoingCapsule(out var capsule))
                    {
                        await stream
                            .WriteAsync(capsule, token)
                            .ConfigureAwait(false);
                        wrote = true;
                    }

                    if (wrote)
                        await stream
                            .FlushAsync(token)
                            .ConfigureAwait(false);

                    if (closed) break;

                    await WaitForOutputAsync(token).ConfigureAwait(false);
                }

                if (_core.FaultErrorCode is { } code)
                {
                    _abrupt = true;
                    await _control
                        .AbortTunnelAsync(
                            new Http3StreamException(_connectStreamId, code, "WebTransport session error."),
                            false
                        )
                        .ConfigureAwait(false);
                }

                else
                    await tunnel
                        .CompleteOutputAsync(token)
                        .ConfigureAwait(false);
            }

            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _abrupt = true;
                _core.Terminate(null);
            }

            catch (Exception error) when (
                Http3TransportFailure.IsTransport(error)
                || error is IOException or ObjectDisposedException or InvalidOperationException
            )
            {
                _abrupt = true;
                _core.Terminate(null);
            }
        }

        // Records how the session ended, releases its identifier and its undelivered streams.
        private void Finish()
        {
            lock (_gate)
            {
                if (_completion.Task.IsCompleted) return;

                if (!_abrupt && _core.FaultErrorCode == null)
                {
                    _closeCode = _core.CloseErrorCode;
                    _closeReason = _core.CloseMessage;
                }
            }

            _connection.SessionEnded(_core);
            _incoming.Writer.TryComplete();

            while (_incoming.Reader.TryRead(out var stream)) Observe(stream.DisposeAsync());

            ReleaseCredit();
            _completion.TrySetResult();
        }

        private static void Observe(ValueTask disposal)
        {
            if (disposal.IsCompletedSuccessfully) return;

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
#endif
