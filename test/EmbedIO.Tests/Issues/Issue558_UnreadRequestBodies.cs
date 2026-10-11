using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue558_UnreadRequestBodies
    {
        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task OriginalFourRequestsSucceedWithoutMandatoryBodyReading(HttpListenerMode mode, bool expectContinue)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithLocalSessionManager()
                .WithWebApi("/api", m => m.WithController<OriginalController>());
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                for (var i = 0; i < 4; i++)
                {
                    var reading = i == 1 || i == 2;
                    using var request = new HttpRequestMessage(i < 2 ? HttpMethod.Get : HttpMethod.Post, url + "api/" + (reading ? "reading" : "ignorant"));
                    request.Headers.ExpectContinue = expectContinue;
                    if (i >= 2) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    Assert.That(await response.Content.ReadAsStringAsync(), Does.Contain(reading ? "response1" : "response2"));
                }
                using var following = await client.GetAsync(url + "api/ignorant");
                following.EnsureSuccessStatusCode();
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        public sealed class OriginalController : WebApiController
        {
            [Route(HttpVerbs.Any, "/reading")]
            public async Task<string> Reading()
            {
                await HttpContext.GetRequestBodyAsStringAsync();
                return "response1";
            }
            [Route(HttpVerbs.Any, "/ignorant")]
            public Task<string> Ignorant() => Task.FromResult("response2");
        }

        [TestCase(HttpListenerMode.EmbedIO, 0, 200)]
        [TestCase(HttpListenerMode.EmbedIO, 7, 200)]
        [TestCase(HttpListenerMode.EmbedIO, -1, 200)]
        [TestCase(HttpListenerMode.EmbedIO, 0, 403)]
        [TestCase(HttpListenerMode.EmbedIO, 7, 403)]
        [TestCase(HttpListenerMode.EmbedIO, -1, 403)]
        public async Task FixedLengthBodiesDoNotPreventFollowingRequests(HttpListenerMode mode, int read, int status)
        {
            var url = Resources.GetServerAddress();
            var body = new string('x', 16384);
            var handled = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/post", HttpVerbs.Post, async c =>
                {
                    if (read == -1) Assert.That(await c.GetRequestBodyAsStringAsync(), Is.EqualTo(body));
                    else if (read > 0)
                    {
                        var prefix = new byte[read];
                        await c.Request.InputStream.ReadExactlyAsync(prefix);
                        Assert.That(Encoding.ASCII.GetString(prefix), Is.EqualTo(body.Substring(0, read)));
                    }
                    Interlocked.Increment(ref handled);
                    c.Response.StatusCode = status;
                    await c.SendStringAsync("ACCEPTED", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithModule(new ActionModule("/next", HttpVerbs.Get, c => { c.Response.ContentLength64 = 4; return c.SendStringAsync("NEXT", "text/plain", WebServer.Utf8NoBomEncoding); }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                for (var i = 0; i < 3; i++)
                {
                    using var content = new StringContent(body, Encoding.ASCII, "text/plain");
                    using var response = await client.PostAsync(url + "post", content);
                    Assert.That((int)response.StatusCode, Is.EqualTo(status));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ACCEPTED"));
                    Assert.That(await client.GetStringAsync(url + "next"), Is.EqualTo("NEXT"));
                }
                Assert.That(handled, Is.EqualTo(3));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 7)]
        public async Task EarlyResponseDoesNotRequireTheEntireUploadBeforeHeaders(HttpListenerMode mode, int read)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/post", HttpVerbs.Post, async c =>
                {
                    if (read > 0) await c.Request.InputStream.ReadExactlyAsync(new byte[read]);
                    c.Response.ContentLength64 = 5;
                    await c.SendStringAsync("EARLY", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithModule(new ActionModule("/next", HttpVerbs.Get, c => { c.Response.ContentLength64 = 4; return c.SendStringAsync("NEXT", "text/plain", WebServer.Utf8NoBomEncoding); }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var client = new TcpClient();
                var uri = new Uri(url);
                await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /post HTTP/1.1\r\nHost: {uri.Authority}\r\nContent-Length: 64\r\n\r\n1234567"), timeout.Token);
                Assert.That(await ReadResponse(reader, timeout.Token), Is.EqualTo("EARLY"));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(new string('x', 57) + $"GET /next HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                Assert.That(await ReadResponse(reader, timeout.Token), Is.EqualTo("NEXT"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task<string> ReadResponse(StreamReader reader, CancellationToken cancellation)
        {
            Assert.That(await reader.ReadLineAsync(cancellation), Does.StartWith("HTTP/1.1 200"));
            var length = 0;
            while (await reader.ReadLineAsync(cancellation) is { Length: > 0 } line)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15));
            Assert.That(length, Is.GreaterThan(0));
            var body = new char[length];
            Assert.That(await reader.ReadBlockAsync(body.AsMemory(), cancellation), Is.EqualTo(length));
            return new string(body);
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        public async Task IncompleteUploadDoesNotPreventShutdownOrOtherConnections(HttpListenerMode mode, bool disconnect)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new ActionModule("/post", HttpVerbs.Post, c =>
                {
                    c.Response.ContentLength64 = 5;
                    return c.SendStringAsync("EARLY", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithModule(new ActionModule("/health", HttpVerbs.Get, c => c.SendStringAsync("HEALTHY", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var socket = new TcpClient();
                var uri = new Uri(url);
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                var stream = socket.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /post HTTP/1.1\r\nHost: {uri.Authority}\r\nContent-Length: 1000000\r\n\r\nx"), timeout.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await ReadResponse(reader, timeout.Token), Is.EqualTo("EARLY"));
                if (disconnect) socket.Dispose();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("HEALTHY"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ListenerHandlesChunkedUploadsWithoutMandatoryBodyReading(bool read)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async c =>
                {
                    if (read && c.Request.HttpVerb == HttpVerbs.Post)
                        Assert.That(await c.GetRequestBodyAsStringAsync(), Is.EqualTo("{}"));
                    await c.SendStringAsync("OK", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent("{}") };
                request.Headers.TransferEncodingChunked = true;
                using var response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("OK"));
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("OK"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
}
