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
        private readonly BorrowedResource<QuicConnection> _connection;
        private readonly Func<Http3QuicExchange, Task> _dispatch;
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private readonly object _sync = new();
        private readonly Dictionary<long, Task> _workers = new();
        private readonly Dictionary<long, TaskCompletionSource<HpackField[]>> _pending = new();
        private readonly QpackDecoder _decoder = new(4096, 16, 65536, 65536, 1048576, 65536);
        private readonly SemaphoreSlim _feedbackReady = new(0, 1);
        private Http3PeerSettings _peer = Http3PeerSettings.Parse(Array.Empty<byte>());
        private Exception? _failure;
        private int _critical;

        private Http3QuicConnection(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token)
        {
            _connection = new BorrowedResource<QuicConnection>(connection);
            _dispatch = dispatch;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _token = _stop.Token;
        }
        internal static async Task RunAsync(QuicConnection connection, Func<Http3QuicExchange, Task> dispatch, CancellationToken token)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
            await using (connection.ConfigureAwait(false))
            using (var session = new Http3QuicConnection(connection, dispatch, token))
                await session.RunCoreAsync().ConfigureAwait(false);
        }
        private async Task RunCoreAsync()
        {
            QuicStream? control = null;
            QuicStream? feedback = null;
            var background = Array.Empty<Task>();
            try
            {
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(_token);
                startup.CancelAfter(TimeSpan.FromSeconds(10));
                control = await _connection.Value.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, startup.Token).ConfigureAwait(false);
                // Control type, SETTINGS: capacity 4096, field limit 65536, blocked streams 16.
                await control.WriteAsync(new byte[] { 0, 4, 10, 1, 0x50, 0, 6, 0x80, 1, 0, 0, 7, 16 }, startup.Token).ConfigureAwait(false);
                feedback = await _connection.Value.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, startup.Token).ConfigureAwait(false);
                await feedback.WriteAsync(new byte[] { 3 }, startup.Token).ConfigureAwait(false);
                background = new[] { WriteFeedbackAsync(feedback), WatchCriticalOutputAsync(control), WatchCriticalOutputAsync(feedback) };
                while (!_token.IsCancellationRequested)
                {
                    var stream = await _connection.Value.AcceptInboundStreamAsync(_token).ConfigureAwait(false);
                    bool rejected;
                    lock (_sync)
                    {
                        rejected = _workers.Count >= 256;
                        // Register under the tracking gate before a worker can finish.
                        if (!rejected) _workers.Add(stream.Id, Task.Run(() => ProcessStreamAsync(stream)));
                    }
                    if (rejected)
                    {
                        AbortStream(stream, 0x107);
                        await stream.DisposeAsync().ConfigureAwait(false);
                        throw new Http3ProtocolException(0x107, "Too many active HTTP/3 stream workers.");
                    }
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (OperationCanceledException) { Fail(new Http3ProtocolException(0x103, "Peer did not provide critical-stream credit before startup deadline.")); }
            catch (QuicException) { _stop.Cancel(); }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error)) { Fail(error); }
            finally
            {
                _stop.Cancel();
                lock (_sync)
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
                if (feedback != null) await feedback.DisposeAsync().ConfigureAwait(false);
                if (control != null) await control.DisposeAsync().ConfigureAwait(false);
            }
            if (_failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
        }
        private async Task ProcessStreamAsync(QuicStream stream)
        {
            var critical = false;
            var streamId = stream.Id;
            try
            {
                if (stream.Type == QuicStreamType.Bidirectional)
                {
                    if ((stream.Id & 3) != 0) throw new Http3ProtocolException(0x103, "Invalid request stream initiator.");
                    await ProcessRequestAsync(stream).ConfigureAwait(false);
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
                    else _stop.Cancel();
                }
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                if (critical) Fail(new Http3ProtocolException(0x102, error.Message));
                else AbortStream(stream, 0x102);
            }
            finally
            {
                if ((streamId & 3) == 0) CancelDecode(streamId);
                try { await stream.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                { if (!_token.IsCancellationRequested) Fail(error); }
                finally { lock (_sync) _workers.Remove(streamId); }
            }
        }
        private async Task ProcessRequestAsync(QuicStream stream)
        {
            var reader = new Http3RequestStream(stream.Id, stream, 65536, long.MaxValue);
            var first = await reader.ReadEventAsync(_token).ConfigureAwait(false);
            var fields = await DecodeAsync(stream.Id, first.EncodedFields ?? throw new Http3ProtocolException(0x105, "Missing initial fields."), _token).ConfigureAwait(false);
            Http2RequestHeaders request;
            try { request = Http2RequestHeaders.Parse(new Http2HeaderBlock(0, false, fields, 0)); }
            catch (Http2ProtocolException error) { throw new Http3StreamException(stream.Id, 0x10e, error.Message); }
            reader.ConfirmHeaders(request.ContentLength);
            using var exchange = new Http3QuicExchange(stream, reader, request,
                (wire, token) => DecodeAsync(stream.Id, wire, token),
                () => (int)Math.Min(65536, Volatile.Read(ref _peer).MaximumFieldSectionSize),
                error => RequestFailed(stream, error), _token);
            await _dispatch(exchange).ConfigureAwait(false);
            if (!exchange.Ended) await exchange.CompleteAsync(_token).ConfigureAwait(false);
            if (!exchange.Body.Ended) stream.Abort(QuicAbortDirection.Read, 0x100);
        }
        private async Task<HpackField[]> DecodeAsync(long streamId, byte[] wire, CancellationToken token)
        {
            Task<HpackField[]> pending;
            lock (_sync)
            {
                _token.ThrowIfCancellationRequested();
                var decoded = _decoder.Submit(streamId, wire);
                SignalFeedback();
                if (decoded != null) return decoded;
                var completion = new TaskCompletionSource<HpackField[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(streamId, completion);
                pending = completion.Task;
            }
            try { return await pending.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { CancelDecode(streamId); throw; }
        }
        private void CancelDecode(long streamId)
        {
            lock (_sync)
            {
                if (_pending.Remove(streamId, out var pending)) pending.TrySetCanceled();
                if (_token.IsCancellationRequested) return;
                try { _decoder.Cancel(streamId); SignalFeedback(); }
                catch (Http3ProtocolException error) { Fail(error); }
            }
        }
        private void SignalFeedback()
        { if (_feedbackReady.CurrentCount == 0) _feedbackReady.Release(); }
        private async Task ReadEncoderAsync(QuicStream stream)
        {
            var bytes = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, _token).ConfigureAwait(false);
                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK encoder stream closed.");
                lock (_sync)
                {
                    foreach (var ready in _decoder.FeedEncoder(bytes, 0, count))
                    {
                        if (!_pending.Remove(ready.StreamId, out var pending)) throw new Http3ProtocolException(0x102, "Missing blocked field-section owner.");
                        pending.TrySetResult(ready.Fields);
                    }
                    SignalFeedback();
                }
            }
        }
        private async Task ReadDecoderAsync(QuicStream stream)
        {
            // This direction currently emits stateless field sections: cancellation
            // is valid, but no dynamic insert or section can be acknowledged.
            var bytes = new byte[10];
            while (true)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(0, 1), _token).ConfigureAwait(false);
                if (count == 0) throw new Http3ProtocolException(0x104, "QPACK decoder stream closed.");
                if ((bytes[0] & 192) != 64) throw new Http3ProtocolException(0x202, "Unexpected acknowledgment of stateless QPACK output.");
                while (true)
                {
                    var offset = 0;
                    try { QpackInteger.Read(bytes, ref offset, count, 6); break; }
                    catch (EndOfStreamException)
                    {
                        if (count == bytes.Length) throw new Http3ProtocolException(0x202, "Overlong QPACK cancellation.");
                        var read = await stream.ReadAsync(bytes.AsMemory(count, 1), _token).ConfigureAwait(false);
                        if (read == 0) throw new Http3ProtocolException(0x104, "QPACK decoder stream closed.");
                        count += read;
                    }
                    catch (InvalidDataException) { throw new Http3ProtocolException(0x202, "Invalid QPACK cancellation integer."); }
                }
            }
        }
        private async Task ReadControlAsync(QuicStream stream)
        {
            var control = new Http3ControlStream(stream, false);
            while (true)
            {
                var received = await control.ReadAsync(_token).ConfigureAwait(false);
                if (received.Type == 4) Volatile.Write(ref _peer, control.Settings ?? throw new Http3ProtocolException(0x102, "Missing peer settings."));
                // No push IDs have been promised by this server yet.
                if (received.Type == 3) throw new Http3ProtocolException(0x108, "CANCEL_PUSH refers to an unpromised push.");
            }
        }
        private async Task WriteFeedbackAsync(QuicStream stream)
        {
            try
            {
                while (true)
                {
                    await _feedbackReady.WaitAsync(_token).ConfigureAwait(false);
                    byte[] bytes;
                    lock (_sync) bytes = _decoder.DrainFeedback();
                    if (bytes.Length != 0) await stream.WriteAsync(bytes, _token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, error.Message)); }
        }
        private async Task WatchCriticalOutputAsync(QuicStream stream)
        {
            try
            {
                await stream.WritesClosed.WaitAsync(_token).ConfigureAwait(false);
                if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, "Peer closed critical output."));
            }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            { if (!_token.IsCancellationRequested) Fail(new Http3ProtocolException(0x104, error.Message)); }
        }
        private async Task<long?> ReadStreamTypeAsync(QuicStream stream)
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
        private void RequestFailed(QuicStream stream, Exception error)
        {
            if (error is Http3ProtocolException protocol) Fail(protocol);
            else AbortStream(stream, error is Http3StreamException scoped ? scoped.ErrorCode : 0x10c);
        }
        private static void AbortStream(QuicStream stream, long code)
        {
            try { stream.Abort(QuicAbortDirection.Both, code); }
            catch (ObjectDisposedException) { }
        }
        private void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref _failure, error, null);
            _stop.Cancel();
        }
        public void Dispose() { _decoder.Dispose(); _feedbackReady.Dispose(); _stop.Dispose(); }
    }
}
#endif
