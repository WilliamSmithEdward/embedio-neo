using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2Exchange : IMultiplexedExchange, IMultiplexedHeaderCoalescing, IDisposable
    {
        private readonly Http2Connection _connection;
        private readonly SemaphoreSlim _response = new(1, 1);
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private bool _headersSent;
        private bool _ended;
        private bool _endedByLength;
        private bool _trailersExpected;
        private bool _tunnel;
        private bool _bodyAllowed = true;
        private long? _responseLength;
        private long _responseBytes;
        private int _disposed;
        internal Http2Exchange(Http2Connection connection, Http2StreamState state, Action<int> consumed, CancellationToken token)
        {
            _connection = connection; State = state;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            _token = _stop.Token;
            Body = new Http2RequestBody(state.Id, state.RequestHeaders.ContentLength, consumed);
            if (state.RemoteEnded) Body.Append(Array.Empty<byte>(), 0, 0, true);
        }
        internal Http2StreamState State { get; }
        public int Id => State.Id;
        public Http2RequestHeaders Request => State.RequestHeaders;
        public Stream InputStream => Body;
        public CancellationToken CancellationToken => _token;
        internal Http2RequestBody Body { get; }
        private static readonly Version Version = new(2, 0);
        public Version ProtocolVersion => Version;
        public bool InitialBodyComplete => State.InitialEndStream;
        public bool Ended => _ended;
        public bool FinalHeadersSent => _headersSent;
        public bool CloseConnectionAfterResponse { get; set; }

        internal async Task RespondAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            await SendHeadersAsync(new[] { new HpackField(":status", "200"), new HpackField("content-length", bytes.Length.ToString(CultureInfo.InvariantCulture)) }, bytes.Length == 0 || Request.Method == "HEAD", token).ConfigureAwait(false);
            if (bytes.Length != 0 && Request.Method != "HEAD") await WriteAsync(bytes, 0, bytes.Length, true, token).ConfigureAwait(false);
        }

        public async Task SendHeadersAsync(HpackField[] fields, bool endStream, CancellationToken token)
        {
            await EnterAsync(token).ConfigureAwait(false);
            try
            {
                if (_headersSent || _ended) throw new InvalidOperationException("Response headers already sent.");
                var response = Http2ResponseHeaders.Validate(fields, Request.Method, endStream);
                await _connection.SendDataAsync(Id, fields, null, 0, 0, endStream, token, _token).ConfigureAwait(false);
                Sent(response, endStream);
            }
            finally { _response.Release(); }
        }

        // Final headers and the first body bytes share one write, so a small
        // response does not cost a separate transport write for its headers.
        public async Task SendHeadersAndWriteAsync(HpackField[] fields, byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await EnterAsync(token).ConfigureAwait(false);
            try
            {
                if (_headersSent || _ended) throw new InvalidOperationException("Response headers already sent.");
                var response = Http2ResponseHeaders.Validate(fields, Request.Method, false);
                var reserved = 0;
                if (count != 0 && response.Status >= 200 && response.BodyAllowed
                    && (!response.ContentLength.HasValue || count <= response.ContentLength.Value))
                    reserved = _connection.SendFlow.TryReserve(Id, Math.Min(16384, count));
                if (reserved == 0)
                {
                    // Headers alone first: an informational or bodiless response, an
                    // invalid body, or a stream that must wait for credit.
                    await _connection.SendDataAsync(Id, fields, null, 0, 0, false, token, _token).ConfigureAwait(false);
                    Sent(response, false);
                    await WriteCoreAsync(bytes, offset, count, false, token).ConfigureAwait(false);
                    return;
                }
                var end = !_trailersExpected && reserved == count && response.ContentLength == count;
                var dataCommitted = await _connection.SendDataAsync(Id, fields, bytes, offset, reserved, end, token, _token).ConfigureAwait(false);
                Sent(response, false);
                if (dataCommitted)
                {
                    offset += reserved; count -= reserved; _responseBytes += reserved;
                    if (end) { EndByLength(); return; }
                }
                await WriteCoreAsync(bytes, offset, count, false, token).ConfigureAwait(false);
            }
            finally { _response.Release(); }
        }

        public async Task WriteAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await EnterAsync(token).ConfigureAwait(false);
            try { await WriteCoreAsync(bytes, offset, count, endStream, token).ConfigureAwait(false); }
            finally { _response.Release(); }
        }

        // Reserve before final headers so Content-Length completion leaves room
        // for the ending HEADERS section. Ordinary responses retain coalesced FIN.
        internal void ExpectTrailers()
        {
            _token.ThrowIfCancellationRequested();
            if (!_response.Wait(0)) throw new InvalidOperationException("Cannot reserve trailers during an output operation.");
            try
            {
                if (_headersSent || _ended) throw new InvalidOperationException("Reserve trailers before final response headers.");
                _trailersExpected = true;
            }
            finally { _response.Release(); }
        }

        internal async Task SendTrailersAsync(HpackField[] fields, CancellationToken token)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            await EnterAsync(token).ConfigureAwait(false);
            try
            {
                if (!_headersSent || _ended || !_bodyAllowed || _tunnel)
                    throw new InvalidOperationException("Response cannot contain trailers.");
                if (_responseLength.HasValue && _responseBytes != _responseLength.Value)
                    throw new InvalidDataException("Response does not match Content-Length.");
                try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(Id, true, fields, 0)); }
                catch (Http2ProtocolException error) { throw new InvalidDataException(error.Message, error); }
                if (Array.Exists(fields, field => field.Name == "te"))
                    throw new InvalidDataException("TE is only valid in requests.");
                await _connection.SendDataAsync(Id, fields, null, 0, 0, true, token, _token).ConfigureAwait(false);
                EndLocal();
            }
            finally { _response.Release(); }
        }

        public Task CompleteAsync(CancellationToken token)
            => _headersSent ? WriteAsync(Array.Empty<byte>(), 0, 0, true, token) : RespondAsync(Array.Empty<byte>(), token);

        // Holds the response gate.
        private async Task WriteCoreAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token)
        {
            if (_endedByLength && count != 0) throw new InvalidDataException("Response exceeds Content-Length.");
            if (!_headersSent || _ended) throw new InvalidOperationException("Response is not writable.");
            if (!_bodyAllowed && count != 0) throw new InvalidOperationException("This response cannot contain DATA.");
            if (count > long.MaxValue - _responseBytes || (_bodyAllowed && _responseLength.HasValue && count > _responseLength.Value - _responseBytes))
                throw new InvalidDataException("Response exceeds Content-Length.");
            if (_bodyAllowed && endStream && _responseLength.HasValue && _responseBytes + count != _responseLength.Value)
                throw new InvalidDataException("Response does not match Content-Length.");
            // A body that reaches its declared length is complete: its last DATA
            // frame ends the stream instead of a separate empty frame at close.
            var byLength = !_trailersExpected && !endStream && count != 0 && _bodyAllowed && _responseLength == _responseBytes + count;
            endStream |= byLength;
            if (count == 0 && endStream)
                await _connection.SendDataAsync(Id, null, bytes, offset, 0, true, token, _token).ConfigureAwait(false);
            while (count > 0)
            {
                var reserved = _connection.SendFlow.TryReserve(Id, Math.Min(16384, count));
                if (reserved == 0) reserved = await ReserveAsync(Math.Min(16384, count), token).ConfigureAwait(false);
                if (!await _connection.SendDataAsync(Id, null, bytes, offset, reserved, endStream && count == reserved, token, _token).ConfigureAwait(false))
                    continue;
                count -= reserved; offset += reserved;
                _responseBytes += reserved;
            }
            if (byLength) EndByLength();
            else if (endStream) EndLocal();
        }

        // Waits only when the response gate is held, so the common case links no tokens.
        private Task EnterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _token.ThrowIfCancellationRequested();
            return _response.Wait(0) ? Task.CompletedTask : EnterSlowAsync(token);
        }

        private async Task EnterSlowAsync(CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            await _response.WaitAsync(linked.Token).ConfigureAwait(false);
        }

        private async Task<int> ReserveAsync(int maximum, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _token);
            return await _connection.SendFlow.ReserveAsync(Id, maximum, linked.Token).ConfigureAwait(false);
        }

        private void Sent(Http2ResponseHeaders response, bool endStream)
        {
            if (response.Status >= 200)
            {
                _headersSent = true;
                _bodyAllowed = response.BodyAllowed;
                _tunnel = Request.Method == "CONNECT" && response.Status < 300;
                _responseLength = response.ContentLength;
            }
            if (endStream) EndLocal();
        }

        private void EndByLength() { _endedByLength = true; EndLocal(); }
        private void EndLocal() { _ended = true; _connection.Streams.EndLocal(Id); _connection.SendFlow.Close(Id); }
        private void CancelApplication()
        {
            try { _stop.Cancel(); }
            catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
            {
                error.Log("HTTP/2 stream", "Exception thrown by an application cancellation callback.");
            }
        }
        internal void Cancel(Exception error) { CancelApplication(); Body.Fail(error); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { CancelApplication(); }
            finally
            {
                try { Body.Dispose(); }
                finally { _stop.Dispose(); _response.Dispose(); }
            }
        }
    }
}
