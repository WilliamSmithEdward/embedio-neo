using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.WebSockets
{
    /// <inheritdoc />
    /// <summary>
    /// Interface to create a WebSocket implementation.
    /// </summary>
    /// <seealso cref="IDisposable" />
    public interface IWebSocket : IDisposable
    {
        /// <summary>
        /// Gets the state.
        /// </summary>
        /// <value>
        /// The state.
        /// </value>
        WebSocketState State { get; }

        /// <summary>
        /// Sends the buffer to the web socket asynchronously.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <param name="isText">if set to <c>true</c> [is text].</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// A task that represents the asynchronous of send data using websocket.
        /// </returns>
        /// <exception cref="WebSocketException">The WebSocket is not open, or it closed before the whole
        /// message was sent.</exception>
        Task SendAsync(byte[] buffer, bool isText, CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes the web socket asynchronously.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The task object representing the asynchronous operation.
        /// </returns>
        Task CloseAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes the web socket asynchronously.
        /// </summary>
        /// <param name="code">The code. <see cref="CloseStatusCode.Undefined"/> closes without sending a close frame;
        /// <see cref="CloseStatusCode.NoStatus"/> sends a close frame without a status.</param>
        /// <param name="comment">The comment.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>
        /// The task object representing the asynchronous operation.
        /// </returns>
        /// <exception cref="ArgumentException"><paramref name="code"/> is a status a server cannot send: below 1000,
        /// 1006, 1010, 1015 to 2999, or 5000 and above (RFC 6455, section 7.4). Or <paramref name="comment"/> is
        /// set with <see cref="CloseStatusCode.NoStatus"/>, or is longer than 123 UTF-8 bytes. Arguments are
        /// checked even when the WebSocket is no longer open.</exception>
        Task CloseAsync(CloseStatusCode code, string? comment = null, CancellationToken cancellationToken = default);
    }
}
