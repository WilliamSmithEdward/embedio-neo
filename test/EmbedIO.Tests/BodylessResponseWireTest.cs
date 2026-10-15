using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class BodylessResponseWireTest
    {
        [TestCase(204, false, false)]
        [TestCase(204, false, true)]
        [TestCase(204, true, false)]
        [TestCase(204, true, true)]
        [TestCase(304, false, false)]
        [TestCase(304, false, true)]
        [TestCase(304, true, false)]
        [TestCase(304, true, true)]
        public async Task BodylessStatusPreservesTheFollowingResponse(int status, bool asynchronous, bool explicitLength)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var bytes = Encoding.ASCII.GetBytes("leak");
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .OnGet("/bodyless", async context =>
                {
                    context.Response.StatusCode = status;
                    if (explicitLength) context.Response.ContentLength64 = bytes.Length;
                    if (asynchronous) await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, context.CancellationToken);
                    else context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                })
                .OnGet("/next", context => context.SendStringAsync("next", "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True, $"The fixture must own its endpoint: {url}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var socket = new TcpClient();
                var uri = new Uri(url);
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var stream = socket.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /bodyless HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n"), timeout.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await reader.ReadLineAsync(timeout.Token), Does.StartWith($"HTTP/1.1 {status}"));
                string? length = null, transfer = null;
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = line[15..].Trim();
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) transfer = line[18..].Trim();
                }
                if (status == 204)
                {
                    Assert.That(length, Is.Null, "204 must not advertise Content-Length.");
                    Assert.That(transfer, Is.Null, "204 must not advertise transfer framing.");
                }
                else if (explicitLength)
                    Assert.That(length, Is.EqualTo("4"), "304 may retain the selected representation length.");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /next HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                Assert.That(await reader.ReadLineAsync(timeout.Token), Does.StartWith("HTTP/1.1 200"),
                    "The bodyless response must not leak payload or a chunk terminator before the successor.");
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
