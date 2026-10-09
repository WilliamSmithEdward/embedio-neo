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
    public class Http10ResponseFramingTest
    {
        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        public async Task PersistenceRequiresASelfDelimitedResponse(bool fixedLength, bool bodyless, bool asynchronous)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var payload = Encoding.ASCII.GetBytes("body");
            var calls = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any, async context =>
            {
                Interlocked.Increment(ref calls);
                if (bodyless) context.Response.StatusCode = 204;
                if (fixedLength) context.Response.ContentLength64 = payload.Length;
                if (asynchronous) await context.Response.OutputStream.WriteAsync(payload, stop.Token);
                else context.Response.OutputStream.Write(payload, 0, payload.Length);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True);
                using var client = new TcpClient();
                var uri = new Uri(url);
                await client.ConnectAsync(uri.Host, uri.Port, stop.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {uri.Authority}\r\nConnection: keep-alive\r\n\r\n"), stop.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await reader.ReadLineAsync(stop.Token), Does.StartWith(bodyless ? "HTTP/1.0 204 " : "HTTP/1.0 200 "));
                string? connection = null, length = null, transfer = null;
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                {
                    if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) connection = line[11..].Trim();
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = line[15..].Trim();
                    if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) transfer = line[18..].Trim();
                }
                Assert.That(transfer, Is.Null);
                var persistent = fixedLength || bodyless;
                Assert.That(connection, Is.EqualTo(persistent ? "keep-alive" : "close"));
                if (!persistent)
                {
                    Assert.That(length, Is.Null);
                    Assert.That(await reader.ReadToEndAsync(stop.Token), Is.EqualTo("body"));
                    Assert.That(calls, Is.EqualTo(1));
                    return;
                }
                if (fixedLength)
                {
                    Assert.That(length, Is.EqualTo("4"));
                    var body = new char[4];
                    Assert.That(await reader.ReadBlockAsync(body.AsMemory(), stop.Token), Is.EqualTo(4));
                    Assert.That(new string(body), Is.EqualTo("body"));
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), stop.Token);
                Assert.That(await reader.ReadLineAsync(stop.Token), Does.StartWith(bodyless ? "HTTP/1.0 204 " : "HTTP/1.0 200 "));
                Assert.That(calls, Is.EqualTo(2));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
