using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedWebSocketCloseWireTest
    {
        [TestCase("03", 1002)]
        [TestCase("03ED", 1002)]
        [TestCase("03E8C080", 1007)]
        [TestCase("03F4", 1012)]
        [TestCase("0FA0646F6E65", 4000)]
        public async Task CloseValidationEmitsExpectedCodeAndListenerSurvives(string hex, int expected)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new TestWebSocket("/ws"))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context => context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port, stop.Token);
                var stream = tcp.GetStream();
                var handshake = $"GET /ws HTTP/1.1\r\nHost: {new Uri(url).Authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), stop.Token);
                var header = new StringBuilder(); var single = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(header.Length, Is.LessThan(8192));
                    await stream.ReadExactlyAsync(single, stop.Token); header.Append((char)single[0]);
                }
                Assert.That(header.ToString(), Does.StartWith("HTTP/1.1 101"));
                var payload = Convert.FromHexString(hex);
                var wire = new byte[6 + payload.Length]; wire[0] = 0x88; wire[1] = (byte)(0x80 | payload.Length); payload.CopyTo(wire, 6);
                // The one-byte length is invalid in the base header. Withhold the
                // rest so unread TCP bytes cannot turn shutdown into a reset.
                await stream.WriteAsync(wire.AsMemory(0, payload.Length == 1 ? 2 : wire.Length), stop.Token);
                var frameHeader = new byte[2]; await stream.ReadExactlyAsync(frameHeader, stop.Token);
                Assert.That(frameHeader[0], Is.EqualTo(0x88));
                Assert.That(frameHeader[1], Is.InRange(2, 125));
                var reply = new byte[frameHeader[1]]; await stream.ReadExactlyAsync(reply, stop.Token);
                Assert.That((reply[0] << 8) | reply[1], Is.EqualTo(expected));
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                Assert.That(await client.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
