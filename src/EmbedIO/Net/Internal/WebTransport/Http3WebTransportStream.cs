#if NET10_0_OR_GREATER
using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Net.Internal.Http3;

namespace EmbedIO.Net.Internal.WebTransport
{
    // One WebTransport stream over its QUIC stream, after the session header. The
    // session counts the stream until both of its directions have ended.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class Http3WebTransportStream : IWebTransportStreamBackend
    {
        private readonly Http3WebTransportSession _session;
        private readonly Http3TransportStream _stream;
        private readonly WebTransportStreamHandle _handle;
        private int _writesCompleted;
        private int _finished;
        private int _disposed;

        internal Http3WebTransportStream(
            Http3WebTransportSession session,
            Http3TransportStream stream,
            WebTransportStreamHandle handle,
            bool canRead,
            bool canWrite
        )
        {
            _session = session;
            _stream = stream;
            _handle = handle;
            IsBidirectional = stream.Type == Http3StreamType.Bidirectional;
            CanRead = canRead;
            CanWrite = canWrite;
            _ = Task
                .WhenAll(
                    canRead ? stream.ReadsClosed : Task.CompletedTask,
                    canWrite ? stream.WritesClosed : Task.CompletedTask
                )
                .ContinueWith(
                    static (completed, state) =>
                    {
                        _ = completed.Exception;
                        (state as Http3WebTransportStream)?.Finish();
                    },
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );
        }

        public bool IsBidirectional { get; }
        public bool CanRead { get; }
        public bool CanWrite { get; }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (!CanRead) throw new NotSupportedException("This WebTransport stream cannot be read.");

            var count = await _stream
                .ReadAsync(buffer, token)
                .ConfigureAwait(false);

            if (count != 0) _session.Consumed(count);

            return count;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
        {
            if (!CanWrite) throw new NotSupportedException("This WebTransport stream cannot be written.");

            if (Volatile.Read(ref _writesCompleted) != 0)
                throw new InvalidOperationException("Writes to this WebTransport stream were completed.");

            // Session credit can be smaller than the write; send what it allows.
            while (!buffer.IsEmpty)
            {
                var granted = await _session
                    .ReserveOutgoingDataAsync(buffer.Length, token)
                    .ConfigureAwait(false);
                await _stream
                    .WriteAsync(buffer[..granted], false, token)
                    .ConfigureAwait(false);
                buffer = buffer[granted..];
            }
        }

        public async Task CompleteWritesAsync(CancellationToken token)
        {
            if (!CanWrite) throw new NotSupportedException("This WebTransport stream cannot be written.");

            if (Interlocked.Exchange(ref _writesCompleted, 1) != 0) return;

            await _stream
                .WriteAsync(ReadOnlyMemory<byte>.Empty, true, token)
                .ConfigureAwait(false);
        }

        public void Abort(WebTransportAbortDirection direction, uint errorCode)
        {
            var transport = (Http3AbortDirection)0;

            if ((direction & WebTransportAbortDirection.Read) != 0 && CanRead) transport |= Http3AbortDirection.Read;

            if ((direction & WebTransportAbortDirection.Write) != 0 && CanWrite)
            {
                transport |= Http3AbortDirection.Write;
                Volatile.Write(ref _writesCompleted, 1);
            }

            if (transport == 0) return;

            try
            {
                _stream.Abort(transport, WebTransportProtocol.ToHttp3ErrorCode(errorCode));
            }

            catch (ObjectDisposedException) { }
        }

        // Completes writes, stops reading what the peer has not sent, then releases the stream.
        // Once the session has ended the stream is reset with WT_SESSION_GONE instead (draft
        // section 6), even if the core has not reached it yet; otherwise disposal could finish
        // it or reset it with a generic code before the core's reset arrives.
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                if (_session.IsClosed) AbortFromSession(WebTransportProtocol.SessionGone);

                if (CanWrite && Interlocked.Exchange(ref _writesCompleted, 1) == 0)
                    try
                    {
                        _stream.CompleteWrites();
                    }

                    catch (Exception error) when (
                        Http3TransportFailure.IsTransport(error)
                        || error is ObjectDisposedException or InvalidOperationException
                    )
                    { }

                if (CanRead && !_stream.ReadsClosed.IsCompleted)
                    try
                    {
                        _stream.Abort(Http3AbortDirection.Read, WebTransportProtocol.ToHttp3ErrorCode(0));
                    }

                    catch (ObjectDisposedException) { }

                await _stream
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }

            finally
            {
                Finish();
            }
        }

        // The core aborts the stream when its session ends. The abort comes before the flag:
        // DisposeAsync takes the flag to mean that writes are over, and disposing a stream that
        // is not yet reset would reset it with the transport's generic code first.
        internal void AbortFromSession(long http3ErrorCode)
        {
            try
            {
                _stream.Abort(Http3AbortDirection.Both, http3ErrorCode);
            }

            catch (ObjectDisposedException) { }

            Volatile.Write(ref _writesCompleted, 1);
        }

        private void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) _session.StreamFinished(_handle);
        }
    }
}
#endif
