using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2Dispatcher : IDisposable
    {
        private readonly Http2Connection _connection;
        private readonly object _sync = new();
        private readonly Dictionary<int, Http2Exchange> _exchanges = new();
        private readonly WaitCallback _startApplication;
        private readonly Func<int, Exception, bool, Task> _abortTunnel;
        private Func<Http2Exchange, Task>? _application;
        private int _runningApplications;
        private TaskCompletionSource<bool>? _applicationsDone;
        private readonly object _creditSync = new();
        private readonly Dictionary<int, int> _credits = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _creditPump = Task.CompletedTask;
        private bool _pumping;
        private int _running;
        private Exception? _failure;
        private bool _draining;
        private bool _drainSent;
        private Task? _drainTask;
        private int _drainLastStream;

        internal Http2Dispatcher(Http2Connection connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _startApplication = state => _ = RunApplicationAsync((Http2Exchange)(state ?? throw new InvalidOperationException("Missing exchange.")));
            _abortTunnel = AbortTunnelAsync;
            _connection.OutputFailed = Abort;
            _connection.UseTransportCancellation(_stop.Token);
        }

        internal async Task RunAsync(Func<Http2Exchange, Task> application, CancellationToken token)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            if (Interlocked.Exchange(ref _running, 1) != 0) throw new InvalidOperationException("Dispatcher already started.");
            _application = application;
            using var registration = token.Register(CancelConnection);
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    Http2Frame? frame = null;
                    var dataAccounted = false;
                    try
                    {
                        frame = await _connection.ReadFrameAsync(_stop.Token).ConfigureAwait(false);
                        if (frame == null) break;
                        if (await _connection.ProcessControlAsync(frame, _stop.Token).ConfigureAwait(false)) continue;
                        if (frame.Type == 8 && frame.StreamId == 0)
                        {
                            _connection.SendFlow.Update(0, (int)(Http2PeerSettings.ReadUInt32(frame.Payload, 0) & 0x7fffffff));
                            continue;
                        }
                        Http2Exchange? started = null;
                        lock (_sync)
                        {
                            if (_draining && (frame.StreamId & 1) != 0 && frame.StreamId > _drainLastStream)
                            {
                                // ReadFrameAsync already validates frame shape and processes
                                // compression state. Refused streams must not enter the registry:
                                // in-flight DATA still belongs to connection flow control.
                                if (frame.HeaderBlock != null)
                                    throw new Http2ProtocolException(7, "Connection is draining.", frame.StreamId);
                                if (frame.Type == 0)
                                {
                                    dataAccounted = true;
                                    QueueCredit(0, _connection.ReceiveFlow.Discard(frame.PayloadLength));
                                }
                                continue;
                            }
                            var state = _connection.Streams.Receive(frame);
                            _exchanges.TryGetValue(frame.StreamId, out var exchange);
                            if (state == null)
                            {
                                if (frame.Type == 0) QueueCredit(0, _connection.ReceiveFlow.Discard(frame.PayloadLength));
                                continue;
                            }
                            if (frame.Type == 16) _connection.SendFlow.SetPriority(state.Id, state.Priority);
                            if (frame.HeaderBlock != null)
                            {
                                if (exchange == null)
                                {
                                    _connection.SendFlow.Open(state.Id);
                                    _connection.SendFlow.SetPriority(state.Id, state.Priority);
                                    _connection.ReceiveFlow.Open(state.Id);
                                    started = new Http2Exchange(_connection, state, count => Consumed(state.Id, count), _stop.Token, _abortTunnel);
                                    _exchanges.Add(state.Id, started);
                                }
                                else exchange.Body.Append(Array.Empty<byte>(), 0, 0, true);
                            }
                            else if (frame.Type == 0 && exchange != null)
                            {
                                dataAccounted = true;
                                _connection.ReceiveFlow.Receive(state.Id, frame.PayloadLength);
                                var offset = (frame.Flags & 8) != 0 ? 1 : 0;
                                var padding = offset == 0 ? 0 : frame.Payload[frame.PayloadOffset] + 1;
                                if (padding != 0) Consumed(state.Id, padding);
                                exchange.Body.Append(frame.Payload, frame.PayloadOffset + offset, frame.PayloadLength - padding, (frame.Flags & 1) != 0);
                            }
                            else if (frame.Type == 3 && exchange != null)
                            {
                                Release(exchange, new IOException("Peer reset the HTTP/2 stream."));
                            }
                            else if (frame.Type == 8 && !state.LocalEnded)
                                _connection.SendFlow.Update(state.Id, (int)(Http2PeerSettings.ReadUInt32(frame.Payload, 0) & 0x7fffffff));
                        }
                        if (started != null) StartApplication(started);
                    }
                    catch (Http2ProtocolException error) when (error.StreamId != 0)
                    {
                        if (frame?.Type == 0 && !dataAccounted) QueueCredit(0, _connection.ReceiveFlow.Discard(frame.PayloadLength));
                        await ResetAsync(error.StreamId, error.ErrorCode, error).ConfigureAwait(false);
                    }
                    finally { frame?.Dispose(); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Http2ProtocolException error)
            {
                _failure = error;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var payload = new byte[8];
                WriteUInt32(payload, 0, (uint)_connection.Streams.LastStreamId);
                WriteUInt32(payload, 4, error.ErrorCode);
                try { await _connection.SendAsync(new[] { new Http2Frame(7, 0, 0, payload) }, deadline.Token).ConfigureAwait(false); }
                catch (Exception failure) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(failure)) { }
            }
            finally
            {
                CancelConnection();
                Task applications;
                lock (_sync)
                {
                    foreach (var exchange in _exchanges.Values) exchange.Cancel(new IOException("HTTP/2 connection ended."));
                    // The read loop has ended, so no further application can start.
                    applications = Volatile.Read(ref _runningApplications) == 0 ? Task.CompletedTask
                        : (_applicationsDone ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
                }
                await applications.ConfigureAwait(false);
                await _creditPump.ConfigureAwait(false);
                Task? drain;
                lock (_sync) drain = _drainTask;
                if (drain != null)
                    try { await drain.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            if (_failure != null) throw new IOException("HTTP/2 connection failed.", _failure);
        }

        // Called only by the read loop. The application runs on the thread pool so
        // reading continues. RunAsync waits for the count after the read loop ends.
        private void StartApplication(Http2Exchange exchange)
        {
            _ = Interlocked.Increment(ref _runningApplications);
            ThreadPool.QueueUserWorkItem(_startApplication, exchange);
        }

        private async Task RunApplicationAsync(Http2Exchange exchange)
        {
            try
            {
                var application = _application ?? throw new InvalidOperationException("Dispatcher has not started.");
                await application(exchange).ConfigureAwait(false);
                if (!exchange.Ended) await exchange.CompleteAsync(exchange.CancellationToken).ConfigureAwait(false);
                if (exchange.CloseConnectionAfterResponse) await DrainAsync().ConfigureAwait(false);
                if (!exchange.State.RemoteEnded) await ResetAsync(exchange.Id, 0, new IOException("Response completed before request body.")).ConfigureAwait(false);
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                if (!_stop.IsCancellationRequested && !exchange.State.Reset)
                    try { await ResetAsync(exchange.Id, 2, error).ConfigureAwait(false); } catch (Exception failure) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(failure)) { Abort(failure); }
            }
            finally
            {
                try
                {
                    try
                    {
                        lock (_sync)
                        {
                            Release(exchange, null);
                            if (_drainSent && _exchanges.Count == 0) CancelConnection();
                        }
                    }
                    finally { exchange.Dispose(); }
                }
                finally
                {
                    // Cleanup failures must not strand connection shutdown.
                    // RunAsync reads the count and creates the signal under _sync.
                    if (Interlocked.Decrement(ref _runningApplications) == 0)
                        lock (_sync) _applicationsDone?.TrySetResult(true);
                }
            }
        }

        internal Task DrainAsync()
        {
            lock (_sync)
            {
                if (_drainTask != null) return _drainTask;
                _draining = true;
                _drainLastStream = _connection.Streams.LastStreamId;
                return _drainTask = SendDrainAsync();
            }
        }

        private async Task SendDrainAsync()
        {
            var payload = new byte[8];
            WriteUInt32(payload, 0, (uint)_drainLastStream);
            await _connection.SendAsync(new[] { new Http2Frame(7, 0, 0, payload) }, _stop.Token).ConfigureAwait(false);
            lock (_sync)
            {
                _drainSent = true;
                // An external drain can start with no applications, or the last
                // stream can reset while GOAWAY waits for the output gate.
                if (_exchanges.Count == 0) CancelConnection();
            }
        }

        private Task AbortTunnelAsync(int id, Exception cause, bool malformed) => ResetAsync(id, malformed ? 1u : 2u, cause);

        private async Task ResetAsync(int id, uint code, Exception error)
        {
            lock (_sync)
            {
                if (_exchanges.TryGetValue(id, out var exchange)) Release(exchange, error);
                else { _connection.Streams.Reset(id); _connection.SendFlow.Close(id); QueueCredit(0, _connection.ReceiveFlow.Close(id)); }
            }
            var payload = new byte[4]; WriteUInt32(payload, 0, code);
            await _connection.SendAsync(new[] { new Http2Frame(3, 0, id, payload) }, _stop.Token).ConfigureAwait(false);
        }

        private void Release(Http2Exchange exchange, Exception? error)
        {
            if (!_exchanges.Remove(exchange.Id)) return;
            _connection.Streams.Reset(exchange.Id);
            if (error != null) exchange.Cancel(error);
            _connection.SendFlow.Close(exchange.Id);
            QueueCredit(0, _connection.ReceiveFlow.Close(exchange.Id));
        }

        private void Consumed(int id, int count)
        {
            if (_stop.IsCancellationRequested) return;
            var updates = _connection.ReceiveFlow.Consume(id, count);
            QueueCredit(0, updates.Connection);
            QueueCredit(id, updates.Stream);
        }

        private void QueueCredit(int id, int increment)
        {
            if (increment == 0 || _stop.IsCancellationRequested) return;
            lock (_creditSync)
            {
                _credits.TryGetValue(id, out var previous);
                _credits[id] = checked(previous + increment);
                if (_pumping) return;
                _pumping = true;
                _creditPump = Task.Run(PumpCreditAsync);
            }
        }

        private async Task PumpCreditAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    Http2Frame[] frames;
                    lock (_creditSync)
                    {
                        if (_credits.Count == 0) { _pumping = false; return; }
                        frames = new Http2Frame[_credits.Count];
                        var index = 0;
                        foreach (var pair in _credits)
                        {
                            var payload = new byte[4]; WriteUInt32(payload, 0, (uint)pair.Value);
                            frames[index++] = new Http2Frame(8, 0, pair.Key, payload);
                        }
                        _credits.Clear();
                    }
                    await _connection.SendAsync(frames, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { if (!_stop.IsCancellationRequested) Abort(error); }
            lock (_creditSync) _pumping = false;
        }

        private void CancelConnection()
        {
            try { _stop.Cancel(); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                error.Log("HTTP/2 connection", "Exception thrown by an application cancellation callback.");
            }
        }
        private void Abort(Exception error) { if (_stop.IsCancellationRequested) return; _failure = error; CancelConnection(); }
        internal static void WriteUInt32(byte[] bytes, int offset, uint value)
        { bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value; }
        public void Dispose() { _connection.OutputFailed = null; _stop.Dispose(); }
    }
}
