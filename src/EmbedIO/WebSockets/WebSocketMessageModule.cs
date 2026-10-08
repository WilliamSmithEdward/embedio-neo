using System;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;

namespace EmbedIO.WebSockets
{
    /// <summary>
    /// An opt-in WebSocket module with separate complete text and binary message callbacks.
    /// </summary>
    /// <remarks>
    /// Messages are dispatched sequentially per connection. Text is decoded as strict UTF-8.
    /// Unhandled message types close with 1003, invalid text delivered to the module with 1007, oversized messages
    /// with 1009, and unexpected message callback failures with 1011.
    /// The existing <see cref="WebSocketModule"/> contract is unchanged.
    /// </remarks>
    public abstract class WebSocketMessageModule : WebSocketModule
    {
        private static readonly UTF8Encoding TextEncoding = new UTF8Encoding(false, true);
        private readonly ConditionalWeakTable<IWebSocketContext, DispatchState> _dispatch = new ConditionalWeakTable<IWebSocketContext, DispatchState>();

        /// <summary>Initializes a message-oriented WebSocket endpoint.</summary>
        /// <param name="requestPath">The endpoint URL path.</param>
        /// <param name="enableConnectionWatchdog">Whether to purge disconnected contexts periodically.</param>
        protected WebSocketMessageModule(string requestPath, bool enableConnectionWatchdog = true)
            : base(requestPath, enableConnectionWatchdog)
        {
        }

        /// <summary>Handles a complete, valid UTF-8 text message.</summary>
        /// <param name="context">The connection context.</param>
        /// <param name="text">The decoded message, including an empty message.</param>
        /// <returns>The asynchronous callback operation.</returns>
        /// <remarks>The default implementation rejects text with close status 1003.</remarks>
        protected virtual Task OnTextMessageReceivedAsync(IWebSocketContext context, string text)
        {
            if (context is null) throw new System.NullReferenceException();
            return context.WebSocket.CloseAsync(CloseStatusCode.UnsupportedData, "Text messages are not supported.", context.CancellationToken);
        }

        /// <summary>Handles a complete binary message.</summary>
        /// <param name="context">The connection context.</param>
        /// <param name="data">The message bytes, including an empty message.</param>
        /// <returns>The asynchronous callback operation.</returns>
        /// <remarks>The default implementation rejects binary with close status 1003.</remarks>
        protected virtual Task OnBinaryMessageReceivedAsync(IWebSocketContext context, byte[] data)
        {
            if (context is null) throw new System.NullReferenceException();
            return context.WebSocket.CloseAsync(CloseStatusCode.UnsupportedData, "Binary messages are not supported.", context.CancellationToken);
        }

        /// <inheritdoc />
        protected sealed override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        {
            if (context is null) throw new System.NullReferenceException();
            if (buffer is null) throw new System.NullReferenceException();
            if (result is null) throw new System.NullReferenceException();
            // The managed backend's legacy event callback can overlap asynchronous handlers.
            // Chain completion tasks without retaining disconnected contexts or native wait handles.
            var state = _dispatch.GetValue(context, _ => new DispatchState());
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task previous;
            lock (state)
            {
                previous = state.Tail;
                state.Tail = completed.Task;
            }

            try
            {
                await previous.ConfigureAwait(false);
                if (context.CancellationToken.IsCancellationRequested || context.WebSocket.State != WebSocketState.Open)
                    return;

                if (MaxMessageSize > 0 && buffer.Length > MaxMessageSize)
                {
                    await context.WebSocket.CloseAsync(CloseStatusCode.TooBig, "Message exceeds the configured limit.", context.CancellationToken).ConfigureAwait(false);
                    return;
                }

                string? text = null;
                if (result.MessageType == (int)WebSocketMessageType.Text)
                {
                    try { text = TextEncoding.GetString(buffer); }
                    catch (DecoderFallbackException)
                    {
                        await context.WebSocket.CloseAsync(CloseStatusCode.InvalidData, "Text is not valid UTF-8.", context.CancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                try
                {
                    if (text != null)
                        await OnTextMessageReceivedAsync(context, text).ConfigureAwait(false);
                    else if (result.MessageType == (int)WebSocketMessageType.Binary)
                        await OnBinaryMessageReceivedAsync(context, buffer).ConfigureAwait(false);
                    else
                        await context.WebSocket.CloseAsync(CloseStatusCode.UnsupportedData, "Message type is not supported.", context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
                catch (Exception exception) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(exception))
                {
                    exception.Log(nameof(WebSocketMessageModule));
                    await context.WebSocket.CloseAsync(CloseStatusCode.ServerError, "Message callback failed.", context.CancellationToken).ConfigureAwait(false);
                }
            }
            finally { completed.TrySetResult(true); }
        }

        private sealed class DispatchState
        {
            public Task Tail { get; set; } = Task.CompletedTask;
        }
    }
}
