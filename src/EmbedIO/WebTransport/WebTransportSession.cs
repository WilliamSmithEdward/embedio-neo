using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>One accepted WebTransport session: its streams, its datagrams and its closure.</summary>
    /// <remarks>
    /// <para>Streams and datagrams the peer sent before the session was accepted are delivered after
    /// acceptance, within fixed bounds; later arrivals beyond those bounds are refused.</para>
    /// <para>Disposing the session closes it with error code 0 if it is still open.</para>
    /// </remarks>
    public sealed class WebTransportSession :
        IDisposable
#if NET10_0_OR_GREATER
        ,
        IAsyncDisposable
#endif
    {
        private readonly IWebTransportSessionBackend _backend;

        internal WebTransportSession(IWebTransportSessionBackend backend) =>
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));

        /// <summary>Gets the HTTP datagrams of this session. Delivery is unreliable and unordered.</summary>
        public HttpDatagramChannel Datagrams => _backend.Datagrams;

        /// <summary>Gets a task that completes when the session has ended, however it ended.</summary>
        /// <remarks>The task does not fail; read <see cref="CloseCode"/> to learn how the session ended.</remarks>
        public Task Completion => _backend.Completion;

        /// <summary>Gets the application error code the session was closed with, or <see langword="null"/>
        /// while it is open or when it ended without a close, for example by a reset or a lost connection.</summary>
        /// <remarks>A peer that ends the session stream without a close message closes with code 0.</remarks>
        public uint? CloseCode => _backend.CloseCode;

        /// <summary>Gets the close message that accompanied <see cref="CloseCode"/>, or <see langword="null"/>.</summary>
        public string? CloseReason => _backend.CloseReason;

        /// <summary>Receives the next stream the peer opened in this session.</summary>
        /// <param name="cancellationToken">Cancels this wait without affecting streams not yet received.</param>
        /// <returns>The stream, or <see langword="null"/> once the session has ended. A unidirectional
        /// stream from the peer can only be read.</returns>
        public ValueTask<WebTransportStream?> AcceptStreamAsync(CancellationToken cancellationToken = default) =>
            _backend.AcceptStreamAsync(cancellationToken);

        /// <summary>Opens a stream to the peer.</summary>
        /// <param name="bidirectional"><see langword="true"/> for a stream both sides use;
        /// <see langword="false"/> for a stream this side only writes.</param>
        /// <param name="cancellationToken">Cancels the wait for stream credit from the peer.</param>
        /// <returns>The open stream.</returns>
        /// <exception cref="InvalidOperationException">The session has ended.</exception>
        public ValueTask<WebTransportStream> OpenStreamAsync(
            bool bidirectional,
            CancellationToken cancellationToken = default
        ) => _backend.OpenStreamAsync(bidirectional, cancellationToken);

        /// <summary>Closes the session: sends the close code and message, then ends the session stream.</summary>
        /// <param name="errorCode">The application error code the peer receives.</param>
        /// <param name="reason">A message for the peer, cut to 1024 UTF-8 bytes; <see langword="null"/> sends none.</param>
        /// <param name="cancellationToken">Stops waiting for the close to be sent; the close itself goes ahead.</param>
        /// <returns>A task that completes when the session has ended. Closing an ended session does nothing.</returns>
        /// <remarks>Streams of the session are reset as it closes.</remarks>
        public Task CloseAsync(
            uint errorCode = 0,
            string? reason = null,
            CancellationToken cancellationToken = default
        ) => _backend.CloseAsync(errorCode, reason ?? string.Empty, cancellationToken);

        /// <summary>Starts closing the session with error code 0 if it is still open, without waiting.</summary>
        public void Dispose()
        {
            var closing = _backend.DisposeAsync();

            if (!closing.IsCompleted)
                _ = closing.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                );

            else if (closing.IsFaulted) _ = closing.Exception;
        }
#if NET10_0_OR_GREATER
        /// <summary>Closes the session with error code 0 if it is still open and waits until it has ended.</summary>
        /// <returns>The closing operation.</returns>
        public ValueTask DisposeAsync() => new(_backend.DisposeAsync());
#endif
    }
}
