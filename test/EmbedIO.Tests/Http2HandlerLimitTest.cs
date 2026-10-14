using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        // Rapid reset: a client that resets its streams while their handlers keep running
        // must not free the streams' slots under the 128-stream limit, or it could pile up
        // handlers without bound. The slots free when the handlers return.
        [Test]
        public async Task ResetStreamsWhoseHandlersStillRunKeepOccupyingTheStreamLimit()
        {
            const int limit = 128;
            var prefix = Resources.GetServerAddress();
            var port = new Uri(prefix).Port;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    _ = Interlocked.Increment(ref running);
                    try
                    {
                        // Ignores the reset, as a slow or careless handler would.
                        await release.Task.ConfigureAwait(false);
                        await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
                    }
                    finally { _ = Interlocked.Decrement(ref running); }
                }));
            var serving = server.RunAsync(stop.Token);
            try
            {
                while (server.State != WebServerState.Listening)
                {
                    Assert.That(serving.IsCompleted, Is.False, "The server stopped before it was listening.");
                    await Task.Delay(10, stop.Token);
                }
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                using var wire = client.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), stop.Token);
                await SendWire(wire, 4, 0, 0, Array.Empty<byte>(), stop.Token);

                for (var index = 0; index < limit; index++)
                    await SendWire(wire, 1, 5, 1 + (2 * index), DrainRequestBlock(port), stop.Token);
                while (Volatile.Read(ref running) != limit) await Task.Delay(10, stop.Token);

                // Reset every stream (CANCEL); the handlers ignore it and keep running.
                var cancel = new byte[] { 0, 0, 0, 8 };
                for (var index = 0; index < limit; index++)
                    await SendWire(wire, 3, 0, 1 + (2 * index), cancel, stop.Token);
                // Let the server finish its side of every reset before the next stream.
                await SendWire(wire, 6, 0, 0, new byte[8], stop.Token);
                await NextPingAckAsync(wire, stop.Token);
                await Task.Delay(500, stop.Token);
                Assert.That(Volatile.Read(ref running), Is.EqualTo(limit), "The handlers outlive their reset streams.");

                var refusedId = 1 + (2 * limit);
                await SendWire(wire, 1, 5, refusedId, DrainRequestBlock(port), stop.Token);
                var reply = NextStreamFrameAsync(wire, refusedId, stop.Token);
                var admittedHandler = Task.Run(async () =>
                {
                    while (Volatile.Read(ref running) <= limit) await Task.Delay(10, stop.Token);
                }, stop.Token);
                if (await Task.WhenAny(reply, admittedHandler).WaitAsync(TimeSpan.FromSeconds(10), stop.Token) != reply)
                    Assert.Fail("The stream beyond the limit was admitted: " + Volatile.Read(ref running) + " handlers run.");
                var refusal = await reply;
                Assert.That(refusal.Type, Is.EqualTo(3), "The stream beyond the limit is reset.");
                Assert.That((refusal.Payload[0] << 24) | (refusal.Payload[1] << 16) | (refusal.Payload[2] << 8) | refusal.Payload[3], Is.EqualTo(7), "REFUSED_STREAM");

                // Once the handlers return, their slots free again.
                release.TrySetResult();
                while (Volatile.Read(ref running) != 0) await Task.Delay(10, stop.Token);
                await Task.Delay(100, stop.Token);
                var admittedId = refusedId + 2;
                await SendWire(wire, 1, 5, admittedId, DrainRequestBlock(port), stop.Token);
                var admitted = await NextStreamFrameAsync(wire, admittedId, stop.Token);
                Assert.That(admitted.Type, Is.EqualTo(1), "A stream after the handlers return is answered.");
            }
            finally
            {
                release.TrySetResult();
                await stop.CancelAsync();
                await serving.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // The next frame for the stream, answering SETTINGS and skipping others.
        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)> NextStreamFrameAsync(System.IO.Stream wire, int id, CancellationToken token)
        {
            while (true)
            {
                var frame = await ReceiveWire(wire, token);
                if (frame.Type == 4 && (frame.Flags & 1) == 0) await SendWire(wire, 4, 1, 0, Array.Empty<byte>(), token);
                else if (frame.Id == id) return frame;
            }
        }

        private static async Task NextPingAckAsync(System.IO.Stream wire, CancellationToken token)
        {
            while (true)
            {
                var frame = await ReceiveWire(wire, token);
                if (frame.Type == 4 && (frame.Flags & 1) == 0) await SendWire(wire, 4, 1, 0, Array.Empty<byte>(), token);
                else if (frame.Type == 6 && (frame.Flags & 1) != 0) return;
            }
        }
    }
}
