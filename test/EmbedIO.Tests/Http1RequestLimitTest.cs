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
    public class Http1RequestLimitTest
    {
        [TestCase("target", 8192, 414)]
        [TestCase("target", 17, 414)]
        [TestCase("headers", 8192, 431)]
        [TestCase("headers", 17, 431)]
        [TestCase("version", 8192, 400)]
        [TestCase("version", 17, 400)]
        [TestCase("syntax", 8192, 400)]
        [TestCase("syntax", 17, 400)]
        public async Task RequestLimitResponsesCloseWithoutDispatchingPipeline(string kind, int fragment, int status)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var calls = 0;
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any,
                context => { Interlocked.Increment(ref calls); return context.SendStringAsync("healthy", "text/plain", Encoding.UTF8); });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                var request = kind switch
                {
                    "target" => "GET /" + new string('x', 33000) + " HTTP/1.1\r\nHost: localhost\r\n\r\n",
                    "headers" => "GET / HTTP/1.1\r\nHost: localhost\r\nX-Large: " + new string('x', 33000) + "\r\n\r\n",
                    "version" => "GET / HTTP/" + new string('1', 33000) + "\r\nHost: localhost\r\n\r\n",
                    _ => "GET / HTTP/1.1\r\nHost: localhost\r\nX: bad\n" + new string('x', 33000) + "\r\n\r\n",
                };
                var bytes = Encoding.ASCII.GetBytes(request + "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                using var received = new MemoryStream();
                var wire = client.GetStream();
                async Task ReadReply()
                {
                    try { await wire.CopyToAsync(received, stop.Token); }
                    catch (IOException) when (received.Length > 0) { /* Reset after a received error response terminates the pipeline. */ }
                }
                var reading = ReadReply();
                try
                {
                    for (var offset = 0; offset < bytes.Length; offset += fragment)
                        await wire.WriteAsync(bytes.AsMemory(offset, Math.Min(fragment, bytes.Length - offset)), stop.Token);
                }
                catch (IOException) { /* The server can reject before the entire oversized request is sent. */ }
                try { await wire.CopyToAsync(received, stop.Token); }
                catch (IOException) when (received.Length > 0) { /* A reset still terminates the rejected pipeline. */ }
                var response = Encoding.ASCII.GetString(received.ToArray());
                Assert.That(response, Does.StartWith("HTTP/1.1 " + status + " "));
                Assert.That(response, Does.Contain("Content-Length: 0\r\nConnection: close\r\n\r\n"));
                Assert.That(response.Split("HTTP/1.1 ", StringSplitOptions.None).Length, Is.EqualTo(2));
                if (status != 400) Assert.That(response, Does.Contain("Cache-Control: no-store\r\n"));
                Assert.That(calls, Is.Zero);
                using var healthy = new System.Net.Http.HttpClient();
                Assert.That(await healthy.GetStringAsync(url, stop.Token), Is.EqualTo("healthy"));
                Assert.That(calls, Is.EqualTo(1));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
