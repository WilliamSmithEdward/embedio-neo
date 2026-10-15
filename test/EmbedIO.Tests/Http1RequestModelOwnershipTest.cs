using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1RequestModelOwnershipTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task MutableHeaderViewCannotReframeTheBodyOrConsumeTheNextRequest(bool chunked)
        {
            var prefix = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var address = new Uri(prefix);
            var requests = new List<(string Target, long Length, string Body)>();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(HttpListenerMode.EmbedIO, prefix).WithAction("/", HttpVerbs.Any, async context =>
            {
                var request = context.Request;
                var length = request.ContentLength64;
                request.Headers["Content-Length"] = "1000";
                request.Headers["Transfer-Encoding"] = "unsupported";
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8, false, 1024, true);
                var body = await reader.ReadToEndAsync(stop.Token);
                requests.Add((request.RawTarget, request.ContentLength64, body));
                Assert.That(request.ContentLength64, Is.EqualTo(length));
                await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port, stop.Token);
                var framing = chunked ? "Transfer-Encoding: chunked" : "Content-Length: 3";
                var body = chunked ? "3\r\nabc\r\n0\r\n\r\n" : "abc";
                var wire = "POST /first HTTP/1.1\r\nHost: " + address.Authority + "\r\n" + framing + "\r\n\r\n" + body
                    + "GET /second HTTP/1.1\r\nHost: " + address.Authority + "\r\nConnection: close\r\n\r\n";
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(wire), stop.Token);
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
                var responses = await reader.ReadToEndAsync(stop.Token);
                Assert.That(responses.Split("HTTP/1.1 200", StringSplitOptions.None), Has.Length.EqualTo(3));
                Assert.That(requests, Is.EqualTo(new[] { ("/first", chunked ? -1L : 3L, "abc"), ("/second", 0L, "") }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
