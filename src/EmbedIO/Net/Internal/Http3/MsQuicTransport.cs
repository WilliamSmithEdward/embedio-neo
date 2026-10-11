#if NET10_0_OR_GREATER
using System;
using System.IO;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class MsQuicTransportConnection : Http3TransportConnection
    {
        private readonly MsQuicNativeConnection _connection;
        internal MsQuicTransportConnection(MsQuicNativeConnection connection)
        { _connection = connection ?? throw new ArgumentNullException(nameof(connection)); }
        internal override async ValueTask<Http3TransportStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken token)
        {
            if (type != QuicStreamType.Bidirectional && type != QuicStreamType.Unidirectional) throw new ArgumentOutOfRangeException(nameof(type));
            return new MsQuicTransportStream(await _connection.OpenStreamAsync(type == QuicStreamType.Unidirectional, token).ConfigureAwait(false));
        }
        internal override async ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token)
        {
            try { return new MsQuicTransportStream(await _connection.AcceptStreamAsync(token).ConfigureAwait(false)); }
            catch (ChannelClosedException)
            { throw new QuicException(QuicError.OperationAborted, null, "Native QUIC connection stopped accepting streams."); }
        }
        internal override async ValueTask CloseAsync(long code, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await _connection.ShutdownAsync(code).WaitAsync(token).ConfigureAwait(false);
        }
        public override async ValueTask DisposeAsync()
        {
            if (_connection.IsClosed) return;
            try { await _connection.ShutdownAsync(0x100).ConfigureAwait(false); }
            finally { _connection.Dispose(); }
        }
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class MsQuicTransportStream : Http3TransportStream
    {
        private readonly MsQuicNativeStream _stream;
        internal MsQuicTransportStream(MsQuicNativeStream stream)
        { _stream = stream ?? throw new ArgumentNullException(nameof(stream)); }
        internal override long Id => _stream.Id;
        internal override QuicStreamType Type => _stream.Unidirectional ? QuicStreamType.Unidirectional : QuicStreamType.Bidirectional;
        internal override Task ReadsClosed => _stream.ReadsClosed;
        internal override Task WritesClosed => _stream.WritesClosed;
        internal override void Abort(QuicAbortDirection direction, long code) => _stream.Abort(direction, code);
        internal override void CompleteWrites() => _stream.CompleteWrites();
        internal override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken token)
            => _stream.WriteAsync(buffer, completeWrites, token);
        public override bool CanRead => _stream.CanRead;
        public override bool CanWrite => _stream.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
            if (_stream.IsClosed) throw new ObjectDisposedException(nameof(MsQuicTransportStream));
        }
        public override Task FlushAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Flush(); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count)
            => _stream.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => _stream.ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => _stream.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count)
            => _stream.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), false, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => _stream.WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), false, token).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => _stream.WriteAsync(buffer, false, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => _stream.DisposeAsync();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
#endif
