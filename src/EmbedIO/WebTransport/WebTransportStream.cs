using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>One stream of a <see cref="WebTransportSession"/>.</summary>
    /// <remarks>
    /// <para>A bidirectional stream can be read and written. A unidirectional stream can only be read when
    /// the peer opened it, and only written when this side opened it.</para>
    /// <para>Disposing the stream completes its writes and stops reading what the peer has not yet sent.
    /// Use <see cref="Abort"/> to end it with an application error code instead.</para>
    /// </remarks>
    public sealed class WebTransportStream : Stream
    {
        private readonly IWebTransportStreamBackend _backend;
        private int _disposed;

        internal WebTransportStream(IWebTransportStreamBackend backend) =>
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));

        /// <summary>Gets whether both sides use the stream.</summary>
        public bool IsBidirectional => _backend.IsBidirectional;

        /// <inheritdoc />
        public override bool CanRead => _backend.CanRead;

        /// <inheritdoc />
        public override bool CanWrite => _backend.CanWrite;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>Ends sending: the peer reads the end of the stream after the data already written.</summary>
        /// <param name="cancellationToken">Cancels waiting for earlier writes to be accepted by the transport.</param>
        /// <returns>The completion of the send direction.</returns>
        /// <exception cref="NotSupportedException">The stream cannot be written.</exception>
        public Task CompleteWritesAsync(CancellationToken cancellationToken = default) =>
            _backend.CompleteWritesAsync(cancellationToken);

        /// <summary>Ends one or both directions at once with an application error code.</summary>
        /// <param name="direction">The directions to end.</param>
        /// <param name="errorCode">The application error code the peer receives.</param>
        /// <remarks>Data not yet delivered in an aborted direction is discarded. Aborting an ended
        /// direction does nothing.</remarks>
        public void Abort(WebTransportAbortDirection direction, uint errorCode) => _backend.Abort(direction, errorCode);

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        /// <inheritdoc />
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);
            return _backend
                .ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .AsTask();
        }
#if NET10_0_OR_GREATER
        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _backend.ReadAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        ) => _backend.WriteAsync(buffer, cancellationToken);

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            await DisposeBackendAsync().ConfigureAwait(false);
            Dispose();
        }
#endif
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

        /// <inheritdoc />
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);
            return _backend
                .WriteAsync(buffer.AsMemory(offset, count), cancellationToken)
                .AsTask();
        }

        /// <summary>Does nothing: written data is handed to the transport as it is written.</summary>
        public override void Flush() { }

        /// <summary>Does nothing: written data is handed to the transport as it is written.</summary>
        /// <param name="cancellationToken">Not used.</param>
        /// <returns>A completed task.</returns>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var disposal = DisposeBackendAsync();

                if (!disposal.IsCompleted)
                    _ = disposal
                        .AsTask()
                        .ContinueWith(
                            static completed => _ = completed.Exception,
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default
                        );

                else if (disposal.IsFaulted) _ = disposal.AsTask().Exception;
            }

            base.Dispose(disposing);
        }

        // Both disposal paths share one backend disposal.
        private ValueTask DisposeBackendAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? _backend.DisposeAsync() : default;

        private static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
        }
    }
}
