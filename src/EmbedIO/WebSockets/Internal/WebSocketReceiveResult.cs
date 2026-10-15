using System;

namespace EmbedIO.WebSockets.Internal
{
    /// <summary>
    /// Represents a WS Receive result.
    /// </summary>
    internal sealed class WebSocketReceiveResult : IWebSocketReceiveResult
    {
        internal WebSocketReceiveResult(int count, Opcode code)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            Count = count;
            // Only complete, reassembled messages are delivered through this result.
            EndOfMessage = true;
            MessageType = code == Opcode.Text ? 0 : 1;
        }

        /// <inheritdoc />
        public int Count { get; }

        /// <inheritdoc />
        public bool EndOfMessage { get; }

        /// <inheritdoc />
        public int MessageType { get; }
    }
}
