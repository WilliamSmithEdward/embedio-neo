#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class EmbedIOQuicTransportConnection : Http3TransportConnection
    {
        private readonly AsyncStreamApplication _application;
        private readonly QuicConnectionOwner _owner;
        private readonly QuicPeerFeatures _features;
        private readonly Http3DatagramTransport _datagrams;

        internal EmbedIOQuicTransportConnection(
            AsyncStreamApplication application,
            QuicConnectionOwner owner,
            QuicPeerFeatures features
        )
        {
            ArgumentNullException.ThrowIfNull(application);
            ArgumentNullException.ThrowIfNull(owner);
            _application = application;
            _owner = owner;
            _features = features;
            _datagrams = new EmbedIOQuicDatagramTransport(application);
        }

        internal override Http3DatagramTransport Datagrams => _datagrams;
        internal override ulong PeerMaximumDatagramFrameSize => _features.MaximumDatagramFrameSize;
        internal override bool ReliableReset => _features.ReliableReset;

        internal override async ValueTask<Http3TransportStream> OpenOutboundStreamAsync(
            Http3StreamType type,
            CancellationToken token
        )
        {
            if (type is not Http3StreamType.Bidirectional and not Http3StreamType.Unidirectional)
                throw new ArgumentOutOfRangeException(nameof(type));

            return new EmbedIOQuicTransportStream(
                await _application
                    .OpenOutboundStreamAsync(type == Http3StreamType.Unidirectional, token)
                    .ConfigureAwait(false)
            );
        }

        internal override async ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token) =>
            new EmbedIOQuicTransportStream(
                await _application
                    .AcceptInboundStreamAsync(token)
                    .ConfigureAwait(false)
            );

        internal override async ValueTask CloseAsync(long code, CancellationToken token)
        {
            if (code < 0 || (ulong)code > StreamReassembly.MaximumOffset)
                throw new ArgumentOutOfRangeException(nameof(code));

            await _application
                .CloseAsync((ulong)code, token)
                .ConfigureAwait(false);
            // Logical application closure precedes publishing the close packet.
            // Keep the native owner alive through transport closing/draining.
            await _owner.Completion
                .WaitAsync(token)
                .ConfigureAwait(false);
        }

        public override ValueTask DisposeAsync() => _owner.DisposeAsync();
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class EmbedIOQuicTransportStream : Http3TransportStream
    {
        private readonly AsyncQuicStream _stream;
        private bool _applicationBody;
        private CancellationToken _requestCancellation;

        internal EmbedIOQuicTransportStream(AsyncQuicStream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            _stream = stream;
        }

        internal override long Id => checked((long)_stream.Id);
        internal override Http3StreamType Type =>
            _stream.Unidirectional ? Http3StreamType.Unidirectional : Http3StreamType.Bidirectional;
        internal override Task ReadsClosed => _stream.ReadsClosed;
        internal override Task WritesClosed => _stream.WritesClosed;

        internal override void CompleteWrites() => _stream.CompleteWrites();

        internal override void SetReliablePrefix(ReadOnlySpan<byte> prefix) => _stream.SetReliablePrefix(prefix);

        internal override void EnableApplicationBodyCancellation(CancellationToken requestCancellation)
        {
            _requestCancellation = requestCancellation;
            _applicationBody = true;
        }

        internal override void Abort(Http3AbortDirection direction, long code)
        {
            if (code < 0 || (ulong)code > StreamReassembly.MaximumOffset)
                throw new ArgumentOutOfRangeException(nameof(code));

            if (
                direction is not Http3AbortDirection.Read
                    and not Http3AbortDirection.Write
                    and not Http3AbortDirection.Both
            )
                throw new ArgumentOutOfRangeException(nameof(direction));

            try
            {
                if (direction is Http3AbortDirection.Read or Http3AbortDirection.Both && _stream.CanRead)
                    _stream.AbortRead((ulong)code);

                if (direction is Http3AbortDirection.Write or Http3AbortDirection.Both && _stream.CanWrite)
                    _stream.AbortWrite((ulong)code);
            }

            // Connection closure already terminates both directions and retains
            // its failure on pending operations. Cleanup has no further work.
            catch (QuicConnectionFailure) { }
        }

        internal override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            bool completeWrites,
            CancellationToken token
        ) => _stream.WriteOwnedAsync(buffer, completeWrites, token);

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

        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_applicationBody || !cancellationToken.CanBeCanceled)
                return _stream.ReadAsync(buffer, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var pending = _stream.ReadAsync(buffer, CancellationToken.None);
            return pending.IsCompletedSuccessfully ? pending : ReadApplicationBodyAsync(pending, cancellationToken);
        }

        private async ValueTask<int> ReadApplicationBodyAsync(ValueTask<int> pending, CancellationToken token)
        {
            var registration = token.UnsafeRegister(
                static state => (state as EmbedIOQuicTransportStream)?.CancelBodyRead(),
                this
            );
            int count;

            try
            {
                count = await pending.ConfigureAwait(false);
            }

            catch (Exception error) when (
                token.IsCancellationRequested
                && (Http3TransportFailure.IsTransport(error) || error is OperationCanceledException)
            )
            {
                throw new OperationCanceledException("The HTTP/3 body read was canceled.", error, token);
            }

            finally
            {
                registration.Dispose();
            }

            token.ThrowIfCancellationRequested();
            return count;
        }

        private void CancelBodyRead()
        {
            try
            {
                _stream.AbortRead(_requestCancellation.IsCancellationRequested ? 0x10cUL : 0x100UL);
            }

            catch (ObjectDisposedException) { }
        }

        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _stream.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => _stream.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => _stream.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();

            base.Dispose(disposing);
        }
    }

    internal sealed class EmbedIOQuicDatagramTransport(AsyncStreamApplication application) : Http3DatagramTransport
    {
        internal override int MaximumSendLength => application.MaximumDatagramPayload ?? 0;

        internal override Http3DatagramSendStatus TrySend(ReadOnlyMemory<byte> payload)
        {
            if (application.IsClosed) return Http3DatagramSendStatus.Closed;

            if (MaximumSendLength == 0) return Http3DatagramSendStatus.Unavailable;

            if (payload.Length > MaximumSendLength) return Http3DatagramSendStatus.TooLarge;

            try
            {
                return application.TrySendDatagram(payload.Span)
                    ? Http3DatagramSendStatus.Queued
                    : Http3DatagramSendStatus.QueueFull;
            }

            catch (IOException) when (application.IsClosed)
            {
                return Http3DatagramSendStatus.Closed;
            }
        }

        internal override async ValueTask<Http3DatagramPacket?> ReceiveAsync(CancellationToken token)
        {
            try
            {
                return new Http3DatagramPacket(
                    await application
                        .ReceiveDatagramAsync(token)
                        .ConfigureAwait(false)
                );
            }

            catch (IOException) when (application.IsClosed)
            {
                return null;
            }
        }
    }
}
#endif
