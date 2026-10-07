using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue495_BenchmarkEndpoints
    {
        [TestCase(HttpListenerMode.EmbedIO, "/json")]
        [TestCase(HttpListenerMode.Microsoft, "/json")]
        [TestCase(HttpListenerMode.EmbedIO, "/plaintext")]
        [TestCase(HttpListenerMode.Microsoft, "/plaintext")]
        public async Task SequentialRequestsReuseTheSameTransport(HttpListenerMode mode, string path)
            => await WithServer(mode, async (uri, token) =>
            {
                using var client = await Connect(uri, token);
                var stream = client.GetStream();
                for (var index = 0; index < 20; index++)
                {
                    await Write(stream, uri, path, false, token);
                    Validate(await Read(stream, token), path, false);
                }
            });

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task SixteenPipelinedResponsesRemainFramedAndOrdered(HttpListenerMode mode, bool mixed)
            => await WithServer(mode, async (uri, token) =>
            {
                using var client = await Connect(uri, token);
                var stream = client.GetStream();
                var paths = Enumerable.Range(0, 16).Select(index => mixed && index % 2 == 0 ? "/json" : "/plaintext").ToArray();
                var requests = string.Concat(paths.Select(path => Request(uri, path, false)
                    .Replace("Connection:", "X-Padding: " + new string('x', 1024) + "\r\nConnection:", StringComparison.Ordinal)));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(requests), token);
                foreach (var path in paths) Validate(await Read(stream, token), path, false);
                await Write(stream, uri, "/json", false, token);
                Validate(await Read(stream, token), "/json", false);
            });

        [TestCase(HttpListenerMode.EmbedIO, "/json")]
        [TestCase(HttpListenerMode.Microsoft, "/json")]
        [TestCase(HttpListenerMode.EmbedIO, "/plaintext")]
        [TestCase(HttpListenerMode.Microsoft, "/plaintext")]
        public async Task ExplicitCloseCompletesTheBodyAndAllowsFreshConnections(HttpListenerMode mode, string path)
            => await WithServer(mode, async (uri, token) =>
            {
                using (var client = await Connect(uri, token))
                {
                    var stream = client.GetStream();
                    await Write(stream, uri, path, true, token);
                    Validate(await Read(stream, token), path, true);
                    Assert.That(await stream.ReadAsync(new byte[1], token), Is.Zero);
                }
                using var fresh = await Connect(uri, token);
                await Write(fresh.GetStream(), uri, path, false, token);
                Validate(await Read(fresh.GetStream(), token), path, false);
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task UnknownChildPathsDoNotMasqueradeAsBenchmarkEndpoints(HttpListenerMode mode)
            => await WithServer(mode, async (uri, token) =>
            {
                using var client = new System.Net.Http.HttpClient();
                using var response = await client.GetAsync(new Uri(uri, "json/child"), token);
                Assert.That((int)response.StatusCode, Is.EqualTo(404));
                Assert.That(await client.GetStringAsync(new Uri(uri, "plaintext"), token), Is.EqualTo("Hello, World!"));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task ConcurrentClientsHaveIndependentReusableConnections(HttpListenerMode mode)
            => await WithServer(mode, async (uri, token) =>
            {
                await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
                {
                    using var client = await Connect(uri, token);
                    var stream = client.GetStream();
                    foreach (var path in new[] { "/json", "/plaintext" })
                    {
                        await Write(stream, uri, path, false, token);
                        Validate(await Read(stream, token), path, false);
                    }
                }));
            });

        [TestCase(HttpListenerMode.EmbedIO, 0)]
        [TestCase(HttpListenerMode.Microsoft, 0)]
        [TestCase(HttpListenerMode.EmbedIO, 2)]
        [TestCase(HttpListenerMode.Microsoft, 2)]
        [TestCase(HttpListenerMode.EmbedIO, 5)]
        [TestCase(HttpListenerMode.Microsoft, 5)]
        public async Task PipelinedSuccessorSurvivesFullPartialAndUnreadBodies(HttpListenerMode mode, int readCount)
        {
            var observed = string.Empty;
            await WithServer(mode, async (uri, token) =>
            {
                using var client = await Connect(uri, token);
                var stream = client.GetStream();
                var requests = "POST /plaintext HTTP/1.1\r\nHost: " + uri.Authority
                    + "\r\nContent-Length: 5\r\nConnection: keep-alive\r\n\r\nhello"
                    + Request(uri, "/plaintext", false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(requests), token);
                Validate(await Read(stream, token), "/plaintext", false);
                Validate(await Read(stream, token), "/plaintext", false);
                Assert.That(observed, Is.EqualTo("hello".Substring(0, readCount)));
            }, (url, listenerMode) => new WebServer(options => options.WithUrlPrefix(url).WithMode(listenerMode))
                .WithModule(new EmbedIO.Actions.ActionModule("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.HttpMethod == "POST" && readCount > 0)
                    {
                        var body = new byte[readCount];
                        await context.Request.InputStream.ReadExactlyAsync(body, context.CancellationToken);
                        observed = Encoding.ASCII.GetString(body);
                    }
                    var response = Encoding.ASCII.GetBytes("Hello, World!");
                    context.Response.ContentType = "text/plain";
                    context.Response.ContentLength64 = response.Length;
                    await context.Response.OutputStream.WriteAsync(response, context.CancellationToken);
                })));
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task BufferedPartialSuccessorCombinesWithLaterSocketInput(HttpListenerMode mode)
            => await WithServer(mode, async (uri, token) =>
            {
                using var client = await Connect(uri, token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(Request(uri, "/plaintext", false) + "GET /js"), token);
                Validate(await Read(stream, token), "/plaintext", false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("on HTTP/1.1\r\nHost: " + uri.Authority + "\r\nConnection: keep-alive\r\n\r\n"), token);
                Validate(await Read(stream, token), "/json", false);
            });

        [TestCase(false)]
        [TestCase(true)]
        public async Task ManagedHttpsPreservesPipelinedResponseOrder(bool mixed)
        {
            var url = HttpsSmoke.GetUrl();
            var uri = new Uri(url);
            using var certificate = HttpsSmoke.CreateCertificate();
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(options => options.WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithModule(new EmbedIO.Actions.ActionModule("/", HttpVerbs.Get, BenchmarkEndpoints.RespondAsync));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = await Connect(uri, timeout.Token);
                using var stream = new System.Net.Security.SslStream(client.GetStream(), false, (_, peer, _, errors)
                    => peer != null && (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) == System.Net.Security.SslPolicyErrors.None
                       && peer.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)
                       == certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256));
                await stream.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = uri.Host }, timeout.Token);
                var paths = Enumerable.Range(0, 16).Select(index => mixed && index % 2 == 0 ? "/json" : "/plaintext").ToArray();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(string.Concat(paths.Select(path => Request(uri, path, false)))), timeout.Token);
                foreach (var path in paths) Validate(await Read(stream, timeout.Token), path, false);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task WithServer(HttpListenerMode mode, Func<Uri, CancellationToken, Task> verify,
            Func<string, HttpListenerMode, WebServer>? createServer = null)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = (createServer ?? BenchmarkEndpoints.CreateServer)(url, mode);
            var running = server.RunAsync(stop.Token);
            try { await verify(new Uri(url), timeout.Token); }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task<TcpClient> Connect(Uri uri, CancellationToken token)
        {
            var client = new TcpClient();
            try { await client.ConnectAsync(uri.Host, uri.Port, token); return client; }
            catch { client.Dispose(); throw; }
        }

        private static string Request(Uri uri, string path, bool close)
            => "GET " + path + " HTTP/1.1\r\nHost: " + uri.Authority
               + "\r\nAccept-Encoding: gzip\r\nConnection: " + (close ? "close" : "keep-alive") + "\r\n\r\n";

        private static async Task Write(Stream stream, Uri uri, string path, bool close, CancellationToken token)
            => await stream.WriteAsync(Encoding.ASCII.GetBytes(Request(uri, path, close)), token);

        private static async Task<(string Status, Dictionary<string, string> Headers, string Body)> Read(Stream stream, CancellationToken token)
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length > 16384 || await stream.ReadAsync(single, token) == 0)
                    throw new IOException("Incomplete or oversized response headers.");
                header.Append((char)single[0]);
            }
            var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None);
            var headers = lines.Skip(1).Where(line => line.Length > 0).Select(line => line.Split(new[] { ':' }, 2))
                .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
            var length = int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture);
            if (length < 0 || length > 1024) throw new IOException("Unexpected benchmark response length.");
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, token);
            return (lines[0], headers, Encoding.UTF8.GetString(body));
        }

        private static void Validate((string Status, Dictionary<string, string> Headers, string Body) response, string path, bool close)
        {
            Assert.That(response.Status, Does.StartWith("HTTP/1.1 200"));
            Assert.That(response.Headers["Content-Type"], Does.StartWith(path == "/json" ? "application/json" : "text/plain"));
            Assert.That(response.Body, Is.EqualTo(path == "/json" ? "{\"message\":\"Hello, World!\"}" : "Hello, World!"));
            Assert.That(response.Headers.ContainsKey("Content-Encoding"), Is.False);
            Assert.That(response.Headers.ContainsKey("Transfer-Encoding"), Is.False);
            Assert.That(response.Headers.ContainsKey("Server"), Is.True);
            Assert.That(DateTimeOffset.TryParse(response.Headers["Date"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date), Is.True);
            Assert.That((DateTimeOffset.UtcNow - date).Duration(), Is.LessThan(TimeSpan.FromSeconds(30)));
            var closes = response.Headers.TryGetValue("Connection", out var connection) && connection.Equals("close", StringComparison.OrdinalIgnoreCase);
            Assert.That(closes, Is.EqualTo(close));
        }
    }
}
