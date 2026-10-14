using System;
using System.IO;
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
        // A graceful drain must not end the connection with a reset. This client keeps
        // sending PING frames across the server's close and reads nothing until afterwards,
        // as a client still sending WINDOW_UPDATE for the final DATA does. A reset at close
        // discards the response before the client reads it, so the response must arrive
        // whole, followed by the server's FIN.
        [Test]
        public async Task GracefulDrainKeepsTheResponseWhenTheClientSendsAcrossTheClose()
        {
            var prefix = Resources.GetServerAddress();
            var port = new Uri(prefix).Port;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix)
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    await release.Task.ConfigureAwait(false);
                    await context.SendStringAsync("held", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var running = server.RunAsync(stop.Token);
            try
            {
                while (server.State != WebServerState.Listening)
                {
                    Assert.That(running.IsCompleted, Is.False, "The server stopped before it was listening.");
                    await Task.Delay(10, stop.Token);
                }
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                using var wire = client.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"), stop.Token);
                await SendWire(wire, 4, 0, 0, Array.Empty<byte>(), stop.Token);
                await SendWire(wire, 1, 5, 1, DrainRequestBlock(port), stop.Token);
                await entered.Task.WaitAsync(stop.Token);

                var drain = server.DrainAsync(TimeSpan.FromSeconds(20), stop.Token);
                for (var frame = await ReceiveWire(wire, stop.Token); frame.Type != 7; frame = await ReceiveWire(wire, stop.Token))
                {
                    if (frame.Type == 4 && (frame.Flags & 1) == 0) await SendWire(wire, 4, 1, 0, Array.Empty<byte>(), stop.Token);
                }

                var sending = Task.Run(async () =>
                {
                    var until = DateTime.UtcNow.AddMilliseconds(400);
                    while (DateTime.UtcNow < until)
                    {
                        await SendWire(wire, 6, 0, 0, new byte[8], stop.Token);
                        await Task.Delay(1, stop.Token);
                    }
                });
                release.TrySetResult();
                await sending;

                using var body = new MemoryStream();
                var ended = false;
                while (await ReceiveWireOrEndAsync(wire, stop.Token) is { } frame)
                {
                    if (frame.Type != 0 || frame.Id != 1) continue;
                    body.Write(frame.Payload);
                    ended |= (frame.Flags & 1) != 0;
                }
                Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("held"));
                Assert.That(ended, Is.True, "The response ends its stream before the connection closes.");
                client.Close();
                await drain.WaitAsync(stop.Token);
            }
            finally
            {
                release.TrySetResult();
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // GET / with the authority this server's prefix routes.
        private static byte[] DrainRequestBlock(int port)
        {
            var authority = Encoding.ASCII.GetBytes("localhost:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var block = new byte[5 + authority.Length];
            block[0] = 0x82; block[1] = 0x86; block[2] = 0x84; block[3] = 0x01; block[4] = (byte)authority.Length;
            authority.CopyTo(block, 5);
            return block;
        }

        // Null when the peer closed at a frame boundary.
        private static async Task<(byte Type, byte Flags, int Id, byte[] Payload)?> ReceiveWireOrEndAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[9];
            var read = await stream.ReadAsync(header, token);
            if (read == 0) return null;
            await stream.ReadExactlyAsync(header.AsMemory(read), token);
            var payload = new byte[(header[0] << 16) | (header[1] << 8) | header[2]];
            await stream.ReadExactlyAsync(payload, token);
            return (header[3], header[4], ((header[5] & 127) << 24) | (header[6] << 16) | (header[7] << 8) | header[8], payload);
        }
    }
}
