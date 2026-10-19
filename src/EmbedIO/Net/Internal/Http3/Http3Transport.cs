#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    // Provider-neutral ownership boundary. Streams returned by a connection are
    // owned by the protocol worker. Writes borrow memory only until completion.
    internal abstract class Http3TransportConnection : IAsyncDisposable
    {
        internal virtual Http3DatagramTransport? Datagrams => null;

        internal abstract ValueTask<Http3TransportStream> OpenOutboundStreamAsync(
            QuicStreamType type,
            CancellationToken token
        );

        internal abstract ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token);

        internal abstract ValueTask CloseAsync(long code, CancellationToken token);

        public abstract ValueTask DisposeAsync();
    }

    internal abstract class Http3TransportStream : Stream
    {
        internal abstract long Id { get; }
        internal abstract QuicStreamType Type { get; }
        internal abstract Task ReadsClosed { get; }
        internal abstract Task WritesClosed { get; }

        internal abstract void Abort(QuicAbortDirection direction, long code);

        internal abstract void CompleteWrites();

        internal abstract ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            bool completeWrites,
            CancellationToken token
        );
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class SystemQuicTransportConnection : Http3TransportConnection
    {
        private readonly QuicConnection _connection;

        internal SystemQuicTransportConnection(QuicConnection connection) =>
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));

        internal override async ValueTask<Http3TransportStream> OpenOutboundStreamAsync(
            QuicStreamType type,
            CancellationToken token
        ) =>
            new SystemQuicTransportStream(
                await _connection
                    .OpenOutboundStreamAsync(type, token)
                    .ConfigureAwait(false)
            );

        internal override async ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token) =>
            new SystemQuicTransportStream(
                await _connection
                    .AcceptInboundStreamAsync(token)
                    .ConfigureAwait(false)
            );

        internal override ValueTask CloseAsync(long code, CancellationToken token) =>
            _connection.CloseAsync(code, token);

        public override ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class SystemQuicTransportStream : Http3TransportStream
    {
        private readonly QuicStream _stream;
        private bool _applicationBody;
        private CancellationToken _requestCancellation;

        internal SystemQuicTransportStream(QuicStream stream) =>
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));

        // Enabled only after an ordinary request has been admitted. Initial fields,
        // control streams and tunnels retain the provider's normal cancellation behavior.
        internal void EnableApplicationBodyCancellation(CancellationToken requestCancellation)
        {
            _requestCancellation = requestCancellation;
            _applicationBody = true;
        }

        internal override long Id => _stream.Id;
        internal override QuicStreamType Type => _stream.Type;
        internal override Task ReadsClosed => _stream.ReadsClosed;
        internal override Task WritesClosed => _stream.WritesClosed;

        internal override void Abort(QuicAbortDirection direction, long code) => _stream.Abort(direction, code);

        internal override void CompleteWrites() => _stream.CompleteWrites();

        internal override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            bool completeWrites,
            CancellationToken token
        ) => _stream.WriteAsync(buffer, completeWrites, token);

        public override bool CanRead => _stream.CanRead;
        public override bool CanWrite => _stream.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _stream.Flush();

        public override Task FlushAsync(CancellationToken token) => _stream.FlushAsync(token);

        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            return _applicationBody
                ? ReadAsync(buffer.AsMemory(offset, count), token).AsTask()
                : _stream.ReadAsync(buffer, offset, count, token);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!_applicationBody || !token.CanBeCanceled) return _stream.ReadAsync(buffer, token);

            token.ThrowIfCancellationRequested();
            // Passing the caller token to QuicStream would send its default 0x10c
            // before the application can send an early response. Own that abort here.
            var pending = _stream.ReadAsync(buffer, CancellationToken.None);
            return pending.IsCompletedSuccessfully ? pending : ReadApplicationBodyAsync(pending, token);
        }

        private async ValueTask<int> ReadApplicationBodyAsync(ValueTask<int> pending, CancellationToken token)
        {
            var registration = token.UnsafeRegister(
                static state => (state as SystemQuicTransportStream)?.CancelApplicationBodyRead(),
                this
            );
            int count;

            try
            {
                count = await pending.ConfigureAwait(false);
            }

            catch (Exception error) when (
                token.IsCancellationRequested && error is QuicException or OperationCanceledException
            )
            {
                throw new OperationCanceledException("The HTTP/3 body read was canceled.", error, token);
            }

            finally
            {
                registration.Dispose();
            }

            // The borrowed buffer is no longer in use, even when cancellation won
            // just as the provider supplied data. Never resume a partly read frame.
            token.ThrowIfCancellationRequested();
            return count;
        }

        private void CancelApplicationBodyRead()
        {
            try
            {
                _stream.Abort(QuicAbortDirection.Read, _requestCancellation.IsCancellationRequested ? 0x10c : 0x100);
            }

            catch (ObjectDisposedException) { }
        }

        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            _stream.WriteAsync(buffer, offset, count, token);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            _stream.WriteAsync(buffer, token);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => _stream.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();

            base.Dispose(disposing);
        }
    }
}
#endif
