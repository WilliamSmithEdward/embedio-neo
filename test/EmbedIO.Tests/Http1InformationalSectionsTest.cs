using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class Http1InformationalSectionsTest
    {
        private static Task Send(IHttpResponse response, int status, WebHeaderCollection headers, CancellationToken token)
            => (response as IHttpResponseSections ?? throw new AssertionException("Missing response capability."))
                .SendInformationalAsync(status, headers, token);

        [TestCase("1.0", 103, false)]
        [TestCase("1.1", 101, false)]
        [TestCase("1.1", 200, false)]
        [TestCase("1.1", 103, true)]
        public async Task InvalidInterimSectionsWriteNoBytesAndLeaveTheFinalResponseUsable(string version, int status, bool framing)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                var fields = new WebHeaderCollection();
                if (framing) fields["Content-Length"] = "0";
                var rejected = Assert.Catch(() => Send(context.Response, status, fields, stop.Token));
                var expectedError = version == "1.0" ? typeof(InvalidOperationException)
                    : framing ? typeof(InvalidDataException) : typeof(ArgumentOutOfRangeException);
                Assert.That(rejected, Is.TypeOf(expectedError));
                Assert.That(context.Response.StatusCode, Is.EqualTo(200));
                context.Response.ContentLength64 = 7;
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("healthy"), stop.Token);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                var uri = new Uri(url);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(uri.Host, uri.Port, stop.Token);
                using var wire = tcp.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/{version}\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), stop.Token);
                using var bytes = new MemoryStream();
                await wire.CopyToAsync(bytes, stop.Token);
                var text = Encoding.ASCII.GetString(bytes.ToArray());
                Assert.That(text, Does.StartWith("HTTP/" + version + " 200 "));
                Assert.That(text, Does.Not.Contain(" 103 "));
                Assert.That(text, Does.EndWith("healthy"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Test]
        public async Task TwoEarlyHintsPrecedeAnUnchangedFinalResponse()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Get, async context =>
            {
                context.Response.StatusCode = 201;
                context.Response.ContentLength64 = 3;
                context.Response.Headers["X-Final"] = "retained";
                await Send(context.Response, 103, new WebHeaderCollection { ["Link"] = "</one>; rel=preload" }, stop.Token);
                await Send(context.Response, 103, new WebHeaderCollection { ["Link"] = "</two>; rel=preload" }, stop.Token);
                Assert.That(context.Response.StatusCode, Is.EqualTo(201));
                Assert.That(context.Response.ContentLength64, Is.EqualTo(3));
                Assert.That(context.Response.Headers["Link"], Is.Null);
                await context.Response.OutputStream.WriteAsync(new byte[] { 65, 66, 67 }, stop.Token);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                var uri = new Uri(url);
                await tcp.ConnectAsync(uri.Host, uri.Port, stop.Token);
                using var wire = tcp.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), stop.Token);
                using var bytes = new MemoryStream();
                await wire.CopyToAsync(bytes, stop.Token);
                var text = Encoding.ASCII.GetString(bytes.ToArray());
                var first = text.IndexOf("HTTP/1.1 103 ", StringComparison.Ordinal);
                var second = text.IndexOf("HTTP/1.1 103 ", first + 1, StringComparison.Ordinal);
                var final = text.IndexOf("HTTP/1.1 201 ", StringComparison.Ordinal);
                Assert.That(first, Is.Zero);
                Assert.That(second, Is.GreaterThan(first));
                Assert.That(final, Is.GreaterThan(second));
                Assert.That(text.Substring(first, second - first), Does.Contain("link: </one>; rel=preload\r\n"));
                Assert.That(text.Substring(second, final - second), Does.Contain("link: </two>; rel=preload\r\n"));
                Assert.That(text.Substring(final), Does.Contain("X-Final: retained\r\n"));
                Assert.That(text, Does.EndWith("\r\n\r\nABC"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
