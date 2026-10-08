using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue510_ChunkedStreaming
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task OriginalTwoHelpersFinishTheFirstResponseRatherThanCreateTwoChunks(HttpListenerMode mode)
        {
            var seen = new TaskCompletionSource<(Version Request, Version Response, Exception? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                var requestVersion = context.Request.ProtocolVersion;
                var responseVersion = context.Response.ProtocolVersion;
                Exception? error = null;
                try
                {
                    context.Response.SendChunked = true;
                    await context.SendStringAsync("chunk1,", "text/plain", Encoding.UTF8);
                    await context.SendStringAsync("chunk2", "text/plain", Encoding.UTF8);
                }
                catch (Exception e) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(e)) { error = e; }
                finally { seen.TrySetResult((requestVersion, responseVersion, error)); }
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                var wire = await ReadWire(url, "1.1");
                var result = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(result.Request, Is.EqualTo(HttpVersion.Version11));
                Assert.That(result.Response, Is.EqualTo(HttpVersion.Version11));
                Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
                Assert.That(wire, Does.Contain("Transfer-Encoding: chunked").IgnoreCase);
                Assert.That(wire, Does.Contain("chunk1,"));
                Assert.That(wire, Does.Not.Contain("chunk2"));
                Assert.That(result.Error, Is.Not.Null);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, "1.1", false)]
        [TestCase(HttpListenerMode.Microsoft, "1.1", false)]
        [TestCase(HttpListenerMode.EmbedIO, "1.0", false)]
        [TestCase(HttpListenerMode.EmbedIO, "1.1", true)]
        [TestCase(HttpListenerMode.EmbedIO, "1.0", true)]
        public async Task OneWriterPreservesProtocolAndCompletePayload(HttpListenerMode mode, string protocol, bool https)
        {
            var url = https ? HttpsSmoke.GetUrl() : Resources.GetServerAddress();
            using var certificate = https ? HttpsSmoke.CreateCertificate() : null;
            using var stop = new CancellationTokenSource();
            var options = new WebServerOptions().WithUrlPrefix(url).WithMode(mode);
            if (certificate != null) options.WithCertificate(certificate);
            using var server = new WebServer(options);
            server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                context.Response.ContentType = "text/plain";
                context.Response.SendChunked = true;
                using var writer = context.OpenResponseText(WebServer.Utf8NoBomEncoding, preferCompression: false);
                await writer.WriteAsync("chunk1,");
                await writer.FlushAsync();
                await writer.WriteAsync(string.Empty);
                await writer.FlushAsync();
                await writer.WriteAsync("chunk2");
                await writer.FlushAsync();
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                var wire = await ReadWire(url, protocol, certificate);
                Assert.That(wire, Does.StartWith("HTTP/" + protocol + " 200"));
                var body = wire.Substring(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                if (protocol == "1.1")
                {
                    Assert.That(wire, Does.Contain("Transfer-Encoding: chunked").IgnoreCase);
                    Assert.That(body, Is.EqualTo("7\r\nchunk1,\r\n6\r\nchunk2\r\n0\r\n\r\n"));
                }
                else
                {
                    Assert.That(wire, Does.Not.Contain("Transfer-Encoding:").IgnoreCase);
                    Assert.That(body, Is.EqualTo("chunk1,chunk2"));
                }
                using var client = certificate == null ? new HttpClient { Timeout = TimeSpan.FromSeconds(5) } : HttpsSmoke.CreateClient(certificate);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("chunk1,chunk2"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task EmptyAndUnicodeResponsesHaveValidTerminalFraming(HttpListenerMode mode, bool empty)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode));
            var payload = empty ? string.Empty : "caf\u00e9\u6f22\u5b57";
            server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                context.Response.ContentType = "text/plain";
                context.Response.SendChunked = true;
                using var writer = context.OpenResponseText(WebServer.Utf8NoBomEncoding, preferCompression: false);
                await writer.WriteAsync(payload);
                await writer.FlushAsync();
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                var wire = await ReadWire(url, "1.1");
                var body = wire.Substring(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
                Assert.That(wire, Does.StartWith("HTTP/1.1 200"));
                if (!empty)
                {
                    var boundary = body.IndexOf("\r\n", StringComparison.Ordinal);
                    Assert.That(Convert.ToInt32(body.Substring(0, boundary), 16), Is.EqualTo(Encoding.UTF8.GetByteCount(payload)));
                    Assert.That(body.Substring(boundary + 2), Is.EqualTo(payload + "\r\n0\r\n\r\n"));
                }
                else if (wire.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase)) Assert.That(body, Is.EqualTo("0\r\n\r\n"));
                else { Assert.That(wire, Does.Contain("Content-Length: 0")); Assert.That(body, Is.Empty); }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase("1.1", null, null, true)]
        [TestCase("1.1", "close", null, false)]
        [TestCase("1.1", "keep-alive", null, true)]
        [TestCase("1.0", null, null, false)]
        [TestCase("1.0", "close", null, false)]
        [TestCase("1.0", "keep-alive", null, true)]
        [TestCase("1.1", null, false, false)]
        [TestCase("1.0", "keep-alive", false, false)]
        [TestCase("1.1", null, true, true)]
        [TestCase("1.0", "keep-alive", true, true)]
        public async Task ParsedRequestPolicyAndExplicitOverridesControlConnectionLifetime(string protocol, string? connection, bool? applicationOverride, bool persists)
        {
            var seen = new TaskCompletionSource<(bool Request, bool Response)>(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                var next = context.Request.RawTarget == "/next";
                if (!next && applicationOverride.HasValue) context.Response.KeepAlive = applicationOverride.Value;
                if (!next) seen.TrySetResult((context.Request.KeepAlive, context.Response.KeepAlive));
                context.Response.ContentLength64 = 1;
                await context.Response.OutputStream.WriteAsync(new[] { (byte)(next ? 'B' : 'A') }, context.CancellationToken);
            }));
            var running = server.RunAsync(stop.Token);
            try
            {
                var uri = new Uri(url);
                using var socket = new TcpClient();
                await socket.ConnectAsync(uri.Host, uri.Port, stop.Token);
                var stream = socket.GetStream();
                var header = connection == null ? "" : "Connection: " + connection + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/{protocol}\r\nHost: {uri.Authority}\r\n{header}\r\n"), stop.Token);
                var first = await ReadFixedResponse(stream, stop.Token);
                var policy = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var requested = protocol == "1.1" ? connection != "close" : connection == "keep-alive";
                Assert.That(policy.Request, Is.EqualTo(requested));
                Assert.That(policy.Response, Is.EqualTo(applicationOverride ?? requested));
                Assert.That(first.Header, Does.StartWith("HTTP/" + protocol + " 200"));
                Assert.That(first.Header, Does.Contain("Connection: " + (persists ? "keep-alive" : "close")));
                Assert.That(first.Body, Is.EqualTo('A'));
                if (persists)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /next HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), stop.Token);
                    var second = await ReadFixedResponse(stream, stop.Token);
                    Assert.That(second.Body, Is.EqualTo('B'));
                    Assert.That(second.Header, Does.Contain("Connection: close"));
                }
                var end = new byte[1];
                Assert.That(await stream.ReadAsync(end, stop.Token), Is.Zero);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static async Task<(string Header, char Body)> ReadFixedResponse(Stream stream, CancellationToken token)
        {
            var header = new StringBuilder();
            var b = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(b, token);
                header.Append((char)b[0]);
                Assert.That(header.Length, Is.LessThan(16384));
            }
            Assert.That(header.ToString(), Does.Contain("Content-Length: 1"));
            await stream.ReadExactlyAsync(b, token);
            return (header.ToString(), (char)b[0]);
        }

        private static async Task<string> ReadWire(string url, string protocol, X509Certificate2? certificate = null)
        {
            var uri = new Uri(url);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var socket = new TcpClient();
            await socket.ConnectAsync(uri.Host, uri.Port, stop.Token);
            Stream stream = socket.GetStream();
            using var tls = certificate == null ? null : new SslStream(stream, false, (_, peer, _, _) => peer?.GetCertHashString() == certificate.Thumbprint);
            if (tls != null)
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host }, stop.Token);
                stream = tls;
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/{protocol}\r\nHost: {uri.Authority}\r\nConnection: close\r\nAccept-Encoding: identity\r\n\r\n"), stop.Token);
            using var body = new MemoryStream();
            await stream.CopyToAsync(body, stop.Token);
            return Encoding.UTF8.GetString(body.ToArray());
        }
    }
}
