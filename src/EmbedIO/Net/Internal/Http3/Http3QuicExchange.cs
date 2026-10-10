#if NET10_0_OR_GREATER
using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal.Http2;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3QuicExchange : IMultiplexedExchange, IMultiplexedHeaderCoalescing, IMultiplexedResponseTrailers, IMultiplexedTunnelControl, IDisposable
    {
        private const int MaximumDataPayload = 256 * 1024;
        // Frames up to this payload size are submitted with their header in one
        // transport write; larger payloads are written in place without a copy.
        private const int CoalescedPayload = 16 * 1024;
        private readonly BorrowedResource<QuicStream> _stream;
        private readonly Http3RequestStream _reader;
        private readonly IHttp3ExchangeOwner _owner;
        private readonly SemaphoreSlim _output = new(1, 1);
        private readonly object _outputLifetime = new();
        private int _outputUsers;
        private readonly byte[] _frameHeader = new byte[16];
        private bool _headers;
        private bool _trailersExpected;
        private bool _tunnel;
        private bool _ended;
        private bool _bodyAllowed = true;
        private bool _outputFailed;
        private long? _length;
        private long _sent;
        private int _disposed;
        internal Http3QuicExchange(QuicStream stream, Http3RequestStream reader, Http2RequestHeaders request,
            IHttp3ExchangeOwner owner, CancellationToken token)
        {
            _stream = new BorrowedResource<QuicStream>(stream); _reader = reader; Request = request;
            _owner = owner; CancellationToken = token;
            Body = new Http3RequestBody(stream.Id, reader, owner);
        }
        internal Http3PriorityState.Entry? PriorityState { get; set; }
        internal HttpPriority Priority => PriorityState?.Value ?? new HttpPriority(3, false);
        public long Id => _stream.Value.Id;
        public Http2RequestHeaders Request { get; }
        public Stream InputStream => Body;
        public CancellationToken CancellationToken { get; }
        internal Http3RequestBody Body { get; }
        private static readonly Version Version = new(3, 0);
        public Version ProtocolVersion => Version;
        // QUIC FIN can follow HEADERS later; no HTTP/2 END_STREAM bit exists.
        // A declared zero-length body is known empty without reading ahead.
        public bool InitialBodyComplete => Request.ContentLength == 0;
        public bool Ended => _ended;
        public bool FinalHeadersSent => _headers;
        public bool CloseConnectionAfterResponse { get; set; }

        internal async Task RespondAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            var end = bytes.Length == 0 || Request.Method == "HEAD";
            await SendHeadersAsync(new[] { new HpackField(":status", "200"), new HpackField("content-length", bytes.Length.ToString(CultureInfo.InvariantCulture)) }, end, token).ConfigureAwait(false);
            if (!end) await WriteAsync(bytes, 0, bytes.Length, true, token).ConfigureAwait(false);
        }
        public async Task SendHeadersAsync(HpackField[] fields, bool endStream, CancellationToken token)
        {
            await AcquireOutputAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (_headers) throw new InvalidOperationException("Final response headers already sent.");
                var response = Http2ResponseHeaders.Validate(fields, Request.Method, endStream);
                var encoded = _owner.Encode(fields);
                await FrameAsync(1, encoded, endStream, token).ConfigureAwait(false);
                if (response.Status >= 200)
                {
                    _headers = true; _bodyAllowed = response.BodyAllowed; _length = response.ContentLength;
                    if (Request.Method == "CONNECT" && response.Status < 300) { _tunnel = true; _reader.EnterTunnel(); }
                }
                if (endStream) _ended = true;
            }
            finally { ReleaseOutput(); }
        }
        public async Task WriteAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            await AcquireOutputAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (!_headers || (!_bodyAllowed && count != 0)) throw new InvalidOperationException("Response cannot contain DATA.");
                if (count > long.MaxValue - _sent || (_bodyAllowed && _length.HasValue && count > _length.Value - _sent))
                    throw new InvalidDataException("Response exceeds Content-Length.");
                if (endStream && _bodyAllowed && _length.HasValue && _sent + count != _length.Value)
                    throw new InvalidDataException("Response does not match Content-Length.");
                // Bound each transport submission: QUIC may buffer an entire write
                // before completing it, regardless of the peer's stream credit.
                // Keep ownership of the caller's buffer and the output gate until
                // every DATA fragment has completed or the transport has failed.
                var remaining = count;
                while (remaining != 0)
                {
                    var size = Math.Min(MaximumDataPayload, remaining);
                    await FrameAsync(0, bytes.AsMemory(offset, size), endStream && size == remaining, token).ConfigureAwait(false);
                    offset += size;
                    remaining -= size;
                }
                if (count == 0 && endStream) _stream.Value.CompleteWrites();
                _sent += count;
                if (endStream) _ended = true;
            }
            catch (ObjectDisposedException error) when (CancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("The HTTP/3 request was canceled during transport disposal.", error, CancellationToken); }
            catch (Exception error) when (error is QuicException or OperationCanceledException) { _outputFailed = true; _owner.Failed(error); throw; }
            finally { ReleaseOutput(); }
        }
        public void ExpectTrailers()
        {
            lock (_outputLifetime)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (_disposed != 0) throw new ObjectDisposedException(nameof(Http3QuicExchange));
                if (!_output.Wait(0)) throw new InvalidOperationException("Cannot reserve trailers during an output operation.");
                ++_outputUsers;
            }
            try
            {
                CheckWritable();
                if (_headers) throw new InvalidOperationException("Reserve trailers before final response headers.");
                _trailersExpected = true;
            }
            finally { ReleaseOutput(); }
        }
        public async Task SendTrailersAsync(HpackField[] fields, CancellationToken token)
        {
            if (fields == null) throw new ArgumentNullException(nameof(fields));
            await AcquireOutputAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (!_headers || !_bodyAllowed || _tunnel)
                    throw new InvalidOperationException("Response cannot contain trailers.");
                if (_length.HasValue && _sent != _length.Value) throw new InvalidDataException("Response does not match Content-Length.");
                try { Http2RequestHeaders.ValidateTrailers(new Http2HeaderBlock(0, true, fields, 0)); }
                catch (Http2ProtocolException error) { throw new InvalidDataException(error.Message, error); }
                if (Array.Exists(fields, field => field.Name == "te"))
                    throw new InvalidDataException("TE is only valid in requests.");
                var encoded = _owner.Encode(fields);
                await FrameAsync(1, encoded, true, token).ConfigureAwait(false);
                _ended = true;
            }
            finally { ReleaseOutput(); }
        }
        public Task CompleteAsync(CancellationToken token) => _headers
            ? WriteAsync(Array.Empty<byte>(), 0, 0, true, token) : RespondAsync(Array.Empty<byte>(), token);
        // Final headers and a small first DATA frame share one transport write.
        // A write that completes a declared Content-Length also carries FIN.
        // Anything else (informational or bodiless status, CONNECT tunnel, a
        // body larger than the declared length or the coalescing bound) uses
        // the separate header and DATA paths with their usual validation.
        public async Task SendHeadersAndWriteAsync(HpackField[] fields, byte[] bytes, int offset, int count, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            var response = Http2ResponseHeaders.Validate(fields, Request.Method, false);
            if (count == 0 || count > CoalescedPayload || response.Status < 200 || !response.BodyAllowed
                || (Request.Method == "CONNECT" && response.Status < 300)
                || (response.ContentLength.HasValue && count > response.ContentLength.Value))
            {
                await SendHeadersAsync(fields, false, token).ConfigureAwait(false);
                await WriteAsync(bytes, offset, count, false, token).ConfigureAwait(false);
                return;
            }
            await AcquireOutputAsync(token).ConfigureAwait(false);
            try
            {
                CheckWritable();
                if (_headers) throw new InvalidOperationException("Final response headers already sent.");
                var encoded = _owner.Encode(fields);
                var end = !_trailersExpected && response.ContentLength == count;
                // Committed once submitted, even if the transport write then fails.
                _headers = true; _bodyAllowed = true; _length = response.ContentLength;
                await HeadersAndDataAsync(encoded, bytes.AsMemory(offset, count), end, token).ConfigureAwait(false);
                _sent = count;
                if (end) _ended = true;
            }
            finally { ReleaseOutput(); }
        }
        private async Task HeadersAndDataAsync(byte[] fields, ReadOnlyMemory<byte> data, bool endStream, CancellationToken token)
        {
            // Two frame headers need at most 18 bytes.
            var total = 18 + fields.Length + data.Length;
            var buffer = ArrayPool<byte>.Shared.Rent(total);
            var size = 0;
            try
            {
                size += QuicInteger.Write(buffer, size, 1);
                size += QuicInteger.Write(buffer, size, fields.Length);
                fields.AsSpan().CopyTo(buffer.AsSpan(size));
                size += fields.Length;
                size += QuicInteger.Write(buffer, size, 0);
                size += QuicInteger.Write(buffer, size, data.Length);
                data.Span.CopyTo(buffer.AsSpan(size));
                size += data.Length;
                await _stream.Value.WriteAsync(buffer.AsMemory(0, size), endStream, token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException error) when (CancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("The HTTP/3 request was canceled during transport disposal.", error, CancellationToken); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _outputFailed = true; _owner.Failed(error); throw; }
            finally
            {
                // Response bytes must not linger in the shared pool.
                buffer.AsSpan(0, total).Clear();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        private void CheckWritable()
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _disposed) != 0 || _outputFailed || _ended) throw new InvalidOperationException("HTTP/3 response is no longer writable.");
        }
        private async Task FrameAsync(long type, ReadOnlyMemory<byte> payload, bool endStream, CancellationToken token)
        {
            var size = QuicInteger.Write(_frameHeader, 0, type);
            size += QuicInteger.Write(_frameHeader, size, payload.Length);
            byte[]? coalesced = null;
            try
            {
                if (payload.Length <= CoalescedPayload)
                {
                    // One transport submission (and completion) per small frame.
                    // QuicStream copies the bytes into native memory before it submits
                    // them (runtime 10.0.12 MsQuicBuffers.SetBuffer), so the rented
                    // array is free once the write has completed or failed.
                    coalesced = ArrayPool<byte>.Shared.Rent(size + payload.Length);
                    _frameHeader.AsSpan(0, size).CopyTo(coalesced);
                    payload.Span.CopyTo(coalesced.AsSpan(size));
                    await _stream.Value.WriteAsync(coalesced.AsMemory(0, size + payload.Length), endStream, token).ConfigureAwait(false);
                }
                else
                {
                    await _stream.Value.WriteAsync(_frameHeader.AsMemory(0, size), false, token).ConfigureAwait(false);
                    await _stream.Value.WriteAsync(payload, endStream, token).ConfigureAwait(false);
                }
            }
            catch (ObjectDisposedException error) when (CancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("The HTTP/3 request was canceled during transport disposal.", error, CancellationToken); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { _outputFailed = true; _owner.Failed(error); throw; }
            finally
            {
                // Response bytes must not linger in the shared pool.
                if (coalesced != null) { coalesced.AsSpan(0, size + payload.Length).Clear(); ArrayPool<byte>.Shared.Return(coalesced); }
            }
        }
        private async Task AcquireOutputAsync(CancellationToken token)
        {
            lock (_outputLifetime)
            {
                // A reset cancels the request before its owner disposes the exchange.
                // Detached callbacks must retain that cancellation when attempting another write.
                token.ThrowIfCancellationRequested();
                CancellationToken.ThrowIfCancellationRequested();
                if (_disposed != 0) throw new ObjectDisposedException(nameof(Http3QuicExchange));
                ++_outputUsers;
            }
            try { await _output.WaitAsync(token).ConfigureAwait(false); }
            catch { FinishOutputUser(); throw; }
        }
        private void ReleaseOutput()
        {
            _output.Release();
            FinishOutputUser();
        }
        private void FinishOutputUser()
        {
            lock (_outputLifetime)
            {
                if (--_outputUsers == 0 && _disposed != 0) _output.Dispose();
            }
        }
        public Task AbortTunnelAsync(Exception cause, bool malformed)
        {
            if (_outputFailed) return Task.CompletedTask;
            _outputFailed = true;
            _owner.Failed(malformed ? new Http3StreamException(Id, 0x10e, cause.Message) : cause);
            return Task.CompletedTask;
        }
        public void Dispose()
        {
            lock (_outputLifetime)
            {
                if (_disposed != 0) return;
                Volatile.Write(ref _disposed, 1);
                // Already-running writers retain the semaphore until their finally
                // blocks release it. New calls cannot enter after invalidation.
                if (_outputUsers == 0) _output.Dispose();
            }
            Body.Dispose();
        }
    }
}
#endif
