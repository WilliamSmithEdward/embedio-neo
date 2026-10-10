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
        internal abstract ValueTask<Http3TransportStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken token);
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
        internal abstract ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, bool completeWrites, CancellationToken token);
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class SystemQuicTransportConnection : Http3TransportConnection
    {
        private readonly QuicConnection _connection;
        internal SystemQuicTransportConnection(QuicConnection connection)
        { _connection = connection ?? throw new ArgumentNullException(nameof(connection)); }
        internal override async ValueTask<Http3TransportStream> OpenOutboundStreamAsync(QuicStreamType type, CancellationToken token)
            => new SystemQuicTransportStream(await _connection.OpenOutboundStreamAsync(type, token).ConfigureAwait(false));
        internal override async ValueTask<Http3TransportStream> AcceptInboundStreamAsync(CancellationToken token)
            => new SystemQuicTransportStream(await _connection.AcceptInboundStreamAsync(token).ConfigureAwait(false));
        internal override ValueTask CloseAsync(long code, CancellationToken token) => _connection.CloseAsync(code, token);
        public override ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class SystemQuicTransportStream : Http3TransportStream
    {
        private readonly QuicStream _stream;
        internal SystemQuicTransportStream(QuicStream stream)
        { _stream = stream ?? throw new ArgumentNullException(nameof(stream)); }
        internal override long Id => _stream.Id;
        internal override QuicStreamType Type => _stream.Type;
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
        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken token) => _stream.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => _stream.ReadAsync(buffer, offset, count, token);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => _stream.ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => _stream.WriteAsync(buffer, offset, count, token);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => _stream.WriteAsync(buffer, token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync() => _stream.DisposeAsync();
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
}
#endif
