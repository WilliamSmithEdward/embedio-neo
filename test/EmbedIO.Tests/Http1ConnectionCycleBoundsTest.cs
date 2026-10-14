using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The HTTP/1 request cycle must bound every step it waits on, and must stop treating a
    // connection as a source of HTTP messages once it carries an upgraded protocol.
    public class Http1ConnectionCycleBoundsTest
    {
        private static string PlainUrl() => HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);

        private static async Task ReadHeadAsync(Stream stream, CancellationToken token)
        {
            using var head = new MemoryStream();
            var one = new byte[1];
            while (head.Length < 8192)
            {
                await stream.ReadExactlyAsync(one, token);
                head.WriteByte(one[0]);
                if (Encoding.ASCII.GetString(head.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) return;
            }

            throw new AssertionException("No response head arrived.");
        }

        // A handler that answers without reading the body leaves the cycle discarding it. That
        // read had no deadline: the head deadline stops once the head arrives and the keep-alive
        // deadline is armed only after the discard, so a client that stops sending held the
        // connection, and its admission slot, indefinitely.
        [Test]
        public async Task AnUnreadRequestBodyDiscardHonorsTheKeepAliveDeadline()
        {
            var url = PlainUrl();
            using var server = new WebServer(options => options
                .WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO)
                .WithConnectionLimits(null, keepAliveTimeout: TimeSpan.FromSeconds(1)));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, context =>
                context.SendStringAsync("answered without reading the body", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            try
            {
                var address = new Uri(url);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, address.Port, stop.Token);
                var stream = client.GetStream();
                // A declared body that never arrives: the response is sent, then the cycle discards.
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "POST / HTTP/1.1\r\nHost: " + address.Authority + "\r\nContent-Length: 1000000\r\n\r\n"), stop.Token);
                await ReadHeadAsync(stream, stop.Token);

                using var bound = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                bound.CancelAfter(TimeSpan.FromSeconds(15));
                var buffer = new byte[256];
                try
                {
                    // Reads the rest of the response, then the deadline retires the connection,
                    // so the stream ends instead of waiting for a body that never arrives.
                    while (await stream.ReadAsync(buffer, bound.Token) != 0)
                    {
                    }
                }
                catch (IOException)
                {
                    // A reset is an equally final end of the connection.
                }
                catch (OperationCanceledException) when (bound.IsCancellationRequested && !stop.IsCancellationRequested)
                {
                    Assert.Fail("The server never ended the stalled body discard.");
                }
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // After a 101 the connection carries the upgraded protocol, not further HTTP/1 messages
        // (RFC 9110, Section 7.8). A handler that returns while its WebSocket is open must not
        // leave the connection reusable, or the cycle reads frames as a request head.
        [Test]
        public async Task AnUpgradedConnectionIsNotReusedForAnotherRequest()
        {
            var url = PlainUrl();
            var accepted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url);
            server.WithModule(new ActionModule("/socket", HttpVerbs.Get, async context =>
            {
                // A plug-in that accepts the upgrade and returns without awaiting the socket.
                _ = await context.GetImplementation().AcceptWebSocketAsync(
                    Array.Empty<string>(), string.Empty, 2048, TimeSpan.FromSeconds(30), context.CancellationToken);
                accepted.TrySetResult(context.GetType().GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context)
                    ?? throw new AssertionException("Missing accepted connection."));
            }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            try
            {
                var address = new Uri(url);
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, address.Port, stop.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "GET /socket HTTP/1.1\r\nHost: " + address.Authority
                    + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13"
                    + "\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n"), stop.Token);
                await ReadHeadAsync(stream, stop.Token);
                var connection = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var reuses = connection.GetType().GetProperty("Reuses")
                    ?? throw new AssertionException("Missing connection reuse count.");

                // The cycle takes its next step once the handler returns. A reused connection
                // starts another request on the upgraded stream, which the reuse count records.
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    Assert.That(reuses.GetValue(connection), Is.EqualTo(0),
                        "The upgraded connection was recycled for another HTTP/1 request.");
                    await Task.Delay(20, stop.Token);
                }
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
