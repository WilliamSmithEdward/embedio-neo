using System;
using System.Collections.Generic;
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
    // The status line and the field section are written as the application left them, so text
    // that reaches them carries no control characters. HTTP/2, HTTP/3, informational sections
    // and chunked trailers already validate through Http2RequestHeaders.ValidateField; these
    // cases hold the HTTP/1 head to the same rule, at the wire rather than by inspection.
    public class ResponseFieldValidationTest
    {
        [Test]
        public async Task AControlCharacterInTheReasonPhraseDoesNotStartAField()
        {
            var response = await Exchange(
                server => server.OnGet("/phrase", async context =>
                {
                    context.Response.StatusDescription = "OK\r\nX-Injected: yes";
                    await context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
                }),
                "/phrase").ConfigureAwait(false);

            Assert.That(HeaderLines(response), Has.None.StartsWith("X-Injected"), response);
            Assert.That(response, Does.Not.StartWith("HTTP/1.1 200"), response);
        }

        [Test]
        public async Task AnOrdinaryReasonPhraseStillReachesTheStatusLine()
        {
            var response = await Exchange(
                server => server.OnGet("/phrase", async context =>
                {
                    context.Response.StatusDescription = "Totally Fine";
                    await context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
                }),
                "/phrase").ConfigureAwait(false);

            Assert.That(response, Does.StartWith("HTTP/1.1 200 Totally Fine\r\n"), response);
        }

        // WebHeaderCollection accepts a CRLF followed by a space, which is obs-fold, and
        // RFC 9112, Section 5.2 says a server must not generate it.
        [Test]
        public async Task AFoldedFieldValueIsNotWrittenToTheWire()
        {
            var response = await Exchange(
                server => server.OnGet("/fold", async context =>
                {
                    context.Response.Headers["X-Fold"] = "a\r\n b";
                    await context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
                }),
                "/fold").ConfigureAwait(false);

            Assert.That(response, Does.Not.Contain("X-Fold: a\r\n b"), response);
            Assert.That(response, Does.Not.StartWith("HTTP/1.1 200"), response);
        }

        // System.Net.Cookie does not refuse control characters in a value, and a cookie is
        // serialized into the field section without passing through WebHeaderCollection.
        [Test]
        public async Task AControlCharacterInACookieValueDoesNotStartAField()
        {
            var response = await Exchange(
                server => server.OnGet("/cookie", async context =>
                {
                    context.Response.SetCookie(new Cookie("session", "v\r\nX-Injected: yes") { Path = "/" });
                    await context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
                }),
                "/cookie").ConfigureAwait(false);

            Assert.That(HeaderLines(response), Has.None.StartsWith("X-Injected"), response);
            Assert.That(response, Does.Not.StartWith("HTTP/1.1 200"), response);
        }

        [Test]
        public async Task AnOrdinaryCookieIsStillSet()
        {
            var response = await Exchange(
                server => server.OnGet("/cookie", async context =>
                {
                    context.Response.SetCookie(new Cookie("session", "abc123") { Path = "/" });
                    await context.SendStringAsync("body", "text/plain", WebServer.Utf8NoBomEncoding);
                }),
                "/cookie").ConfigureAwait(false);

            Assert.That(response, Does.StartWith("HTTP/1.1 200"), response);
            Assert.That(HeaderLines(response), Has.One.StartsWith("Set-Cookie: session=abc123"), response);
        }

        private static IEnumerable<string> HeaderLines(string response)
        {
            var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = end < 0 ? response : response.Substring(0, end);
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (var index = 1; index < lines.Length; index++) yield return lines[index];
        }

        // One request, read to the server's close, so the assertions see the head as bytes.
        private static async Task<string> Exchange(Action<WebServer> configure, string path)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            configure(server);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True, $"The fixture must own its endpoint: {url}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var socket = new TcpClient();
                var uri = new Uri(url);
                await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
                var stream = socket.GetStream();
                await stream.WriteAsync(
                    Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"),
                    timeout.Token).ConfigureAwait(false);
                using var received = new MemoryStream();
                var buffer = new byte[4096];
                while (true)
                {
                    var count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                    if (count <= 0) break;
                    received.Write(buffer, 0, count);
                }

                return Encoding.Latin1.GetString(received.ToArray());
            }
            finally
            {
                stop.Cancel();
                try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }
}
