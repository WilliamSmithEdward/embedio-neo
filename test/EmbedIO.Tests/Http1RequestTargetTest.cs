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
    public class Http1RequestTargetTest
    {
        [TestCase("OPTIONS", "*", true)]
        [TestCase("GET", "*", false)]
        [TestCase("OPTIONS", "*?x=1", false)]
        [TestCase("GET", "/path#fragment", false)]
        [TestCase("GET", "/path?x=#fragment", false)]
        [TestCase("GET", "/path\\segment", false)]
        [TestCase("GET", "/bad%", false)]
        [TestCase("GET", "/bad%2", false)]
        [TestCase("GET", "/bad%GG", false)]
        [TestCase("GET", "/good%25", true)]
        [TestCase("GET", "/path%23fragment", true)]
        [TestCase("GET", "/path?x=%23fragment", true)]
        public async Task TargetSyntaxIsValidatedBeforeDispatch(string method, string target, bool valid)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var calls = 0;
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any, context =>
            {
                calls++;
                Assert.That(context.Request.RawTarget, Is.EqualTo(target));
                if (target == "*") Assert.That(context.Request.Url.AbsolutePath, Is.EqualTo("/"));
                return context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(method + " " + target
                    + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"), stop.Token);
                using var received = new MemoryStream();
                await client.GetStream().CopyToAsync(received, stop.Token);
                var response = Encoding.ASCII.GetString(received.ToArray());
                Assert.That(response, Does.StartWith(valid ? "HTTP/1.1 200 " : "HTTP/1.1 400 "));
                Assert.That(calls, Is.EqualTo(valid ? 1 : 0));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
