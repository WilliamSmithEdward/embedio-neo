using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    // Server stops send each open WebSocket a 1001 close and wait up to a second for the client's
    // answer. A test client that is not reading never answers; this one answers, as clients do.
    internal static class ClientWebSocketClose
    {
        // Receives the server's close and answers it.
        internal static async Task AnswerAsync(ClientWebSocket client, CancellationToken cancellationToken)
        {
            var close = await client.ReceiveAsync(new ArraySegment<byte>(new byte[64]), cancellationToken).ConfigureAwait(false);
            Assert.That(close.MessageType, Is.EqualTo(WebSocketMessageType.Close));
            await AnswerReceivedAsync(client, cancellationToken).ConfigureAwait(false);
        }

        // Answers a close the client has already received. The server may have ended the
        // connection already, for example once its wait for the answer ran out, so a failed
        // answer is expected.
        internal static async Task AnswerReceivedAsync(ClientWebSocket client, CancellationToken cancellationToken)
        {
            try
            {
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is WebSocketException or IOException)
            {
                // The connection ended before the answer.
            }
        }
    }
}
