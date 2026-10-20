using System;

namespace EmbedIO.WebSockets.Internal
{
    /// <summary>
    /// Represents the event data for the <see cref="Internal.WebSocket.OnMessage"/> event.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   That event occurs when the <see cref="Internal.WebSocket"/> receives
    ///   a message or a ping if the <see cref="Internal.WebSocket.EmitOnPing"/>
    ///   property is set to <c>true</c>.
    ///   </para>
    ///   <para>
    ///   If you would like to get the message data, you should access
    ///   the <see cref="RawData"/> property.
    ///   </para>
    /// </remarks>
    internal sealed class MessageEventArgs : EventArgs, IDisposable
    {
        private readonly byte[] _rawData;

        internal WebSocketDeliveryBudget.Lease? DeliveryLease { get; set; }

        public void Dispose() => DeliveryLease?.Dispose();

        internal MessageEventArgs(WebSocketFrame frame)
        {
            Opcode = frame.Opcode;
            _rawData = (byte[])frame.PayloadData
                .ToArray()
                .Clone();
        }

        internal MessageEventArgs(Opcode opcode, byte[] rawData)
        {
            if ((ulong)rawData.Length > PayloadData.MaxLength)
                throw new WebSocketException(CloseStatusCode.TooBig);

            Opcode = opcode;
            _rawData = rawData;
        }

        /// <summary>
        /// Gets the message data as an array of <see cref="byte"/>.
        /// </summary>
        /// <value>
        /// An array of <see cref="byte"/> that represents the message data.
        /// </value>
        public byte[] RawData => _rawData;
        internal Opcode Opcode { get; }
    }
}
