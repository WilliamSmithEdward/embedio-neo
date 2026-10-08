using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http2
{
    internal sealed class Http2Exchange : IDisposable
    {
        private readonly Http2Connection _connection;
        private readonly SemaphoreSlim _response = new(1, 1);
        private readonly CancellationTokenSource _stop;
        private bool _headersSent;
        private bool _ended;
        private int _disposed;
        internal Http2Exchange(Http2Connection connection, Http2StreamState state, Action<int> consumed, CancellationToken token)
        {
            _connection = connection; State = state;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Body = new Http2RequestBody(state.Id, state.RequestHeaders.ContentLength, consumed);
            if (state.RemoteEnded) Body.Append(Array.Empty<byte>(), 0, 0, true);
        }
        internal Http2StreamState State { get; }
        public int Id => State.Id;
        public Http2RequestHeaders Request => State.RequestHeaders;
        public Stream InputStream => Body;
        public CancellationToken CancellationToken => _stop.Token;
        internal Http2RequestBody Body { get; }
        internal bool Ended => _ended;

        internal async Task RespondAsync(byte[] bytes, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            await SendHeadersAsync(new[] { new HpackField(":status", "200"), new HpackField("content-length", bytes.Length.ToString(CultureInfo.InvariantCulture)) }, bytes.Length == 0, token).ConfigureAwait(false);
            if (bytes.Length != 0) await WriteAsync(bytes, 0, bytes.Length, true, token).ConfigureAwait(false);
        }

        internal async Task SendHeadersAsync(HpackField[] fields, bool endStream, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            await _response.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (_headersSent || _ended) throw new InvalidOperationException("Response headers already sent.");
                await _connection.SendHeadersAsync(Id, fields, endStream, linked.Token).ConfigureAwait(false);
                _headersSent = true;
                if (endStream) EndLocal();
            }
            finally { _response.Release(); }
        }

        internal async Task WriteAsync(byte[] bytes, int offset, int count, bool endStream, CancellationToken token)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
            await _response.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!_headersSent || _ended) throw new InvalidOperationException("Response is not writable.");
                if (count == 0 && endStream)
                    await _connection.SendAsync(new[] { new Http2Frame(0, 1, Id, Array.Empty<byte>()) }, linked.Token).ConfigureAwait(false);
                while (count > 0)
                {
                    var reserved = await _connection.SendFlow.ReserveAsync(Id, Math.Min(16384, count), linked.Token).ConfigureAwait(false);
                    var payload = new byte[reserved];
                    Buffer.BlockCopy(bytes, offset, payload, 0, reserved);
                    count -= reserved; offset += reserved;
                    await _connection.SendAsync(new[] { new Http2Frame(0, endStream && count == 0 ? (byte)1 : (byte)0, Id, payload) }, linked.Token).ConfigureAwait(false);
                }
                if (endStream) EndLocal();
            }
            finally { _response.Release(); }
        }

        private void EndLocal() { _ended = true; _connection.Streams.EndLocal(Id); _connection.SendFlow.Close(Id); }
        internal void Cancel(Exception error) { _stop.Cancel(); Body.Fail(error); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel(); Body.Dispose(); _stop.Dispose(); _response.Dispose();
        }
    }
}
