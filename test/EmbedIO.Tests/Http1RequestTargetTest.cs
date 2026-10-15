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
        [TestCase("GET", "@127.0.0.1/path", false)]
        [TestCase("GET", "@example.invalid/path", false)]
        [TestCase("GET", "path", false)]
        [TestCase("GET", "path/next", false)]
        [TestCase("GET", "?query=1", false)]
        [TestCase("GET", "127.0.0.1:80", false)]
        [TestCase("GET", "http:/127.0.0.1/path", false)]
        [TestCase("GET", "https:127.0.0.1/path", false)]
        [TestCase("GET", "/@127.0.0.1/path", true)]
        [TestCase("GET", "/?query=1", true)]
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
        [TestCase("GET", "http://name@127.0.0.1/", false)]
        [TestCase("GET", "https://name@127.0.0.1/", false)]
        [TestCase("GET", "http://name:value@127.0.0.1/", false)]
        [TestCase("GET", "http://@127.0.0.1/", false)]
        [TestCase("GET", "http://name%40name@127.0.0.1/", false)]
        [TestCase("GET", "http://127.0.0.1/path@name?other=@value", true)]
        [TestCase("GET", "http://127.0.0.1/path%40name", true)]
        public Task TargetSyntaxIsValidatedBeforeDispatch(string method, string target, bool valid)
            => CheckRequest(method, target, "127.0.0.1", valid);

        [TestCase("127.0.0.1")]
        [TestCase("127.0.0.1:")]
        [TestCase("127.0.0.1:0")]
        [TestCase("127.0.0.1:65536")]
        [TestCase("127.0.0.1:abc")]
        [TestCase("127.0.0.1:80/path")]
        [TestCase("user@127.0.0.1:80")]
        [TestCase("http://127.0.0.1:80/")]
        [TestCase("/path")]
        [TestCase("[::1]")]
        [TestCase("[::1]:")]
        public Task ConnectRequiresAuthorityWithAValidExplicitPort(string target)
            => CheckRequest("CONNECT", target, "127.0.0.1", false);
        [TestCase("/x[0]", false)]
        [TestCase("http://127.0.0.1/x[0]", false)]
        [TestCase("/x?y={z}", false)]
        [TestCase("http://127.0.0.1/x?y={z}", false)]
        [TestCase("/x|y", false)]
        [TestCase("http://127.0.0.1/x|y", false)]
        [TestCase("/x^y", false)]
        [TestCase("http://127.0.0.1/x^y", false)]
        [TestCase("/x`y", false)]
        [TestCase("http://127.0.0.1/x`y", false)]
        [TestCase("/\"x\"", false)]
        [TestCase("http://127.0.0.1/\"x\"", false)]
        [TestCase("/x%5B0%5D?y=%7Bz%7D", true)]
        [TestCase("http://127.0.0.1/x%5B0%5D?y=%7Bz%7D", true)]
        [TestCase("/a:b@c!$&'()*+,;=~-._/next?x=/?:@%2F", true)]
        [TestCase("http://127.0.0.1/a:b@c!$&'()*+,;=~-._/next?x=/?:@%2F", true)]
        public Task PathGrammarIsValidatedBeforeUriNormalization(string target, bool valid)
            => CheckRequest("GET", target, "127.0.0.1", valid);

        [TestCase("127.0.0.1:65536", false)]
        [TestCase("127.0.0.1:2147483648", false)]
        [TestCase("127.0.0.1:999999999999999999999999", false)]
        [TestCase("127.0.0.1:0", true)]
        [TestCase("127.0.0.1:65535", true)]
        [TestCase("127.0.0.1:000000000000000000000080", true)]
        [TestCase("127.0.0.1:abc", false)]
        [TestCase("127.0.0.1:-1", false)]
        [TestCase("127.0.0.1:+80", false)]
        [TestCase("127.0.0.1:80x", false)]
        [TestCase("127.0.0.1:80:90", false)]
        [TestCase("127.0.0.1/path", false)]
        [TestCase("127.0.0.1?query", false)]
        [TestCase("user@127.0.0.1", false)]
        [TestCase("127.0.0.1#fragment", false)]
        [TestCase("127.0.0.1:80", true)]
        [TestCase("127.0.0.1:", true)]
        public Task HostSyntaxIsValidatedBeforeDispatch(string authority, bool valid)
            => CheckRequest("GET", "/", authority, valid);

        [TestCase("localhost", false)]
        [TestCase("example.invalid", false)]
        [TestCase("127.0.0.2", false)]
        [TestCase("127.0.0.1", true)]
        public Task AbsoluteTargetHostTakesPrecedenceOverHostHeader(string authority, bool unregisteredTarget)
            => CheckRequest("GET", "/", authority, true, true, unregisteredTarget);

        [TestCase("HTTP/1.0")]
        [TestCase("HTTP/1.1")]
        [TestCase("HTTP/1.2")]
        [TestCase("HTTP/1.9")]
        public Task HigherMinorVersionsUseTheSupportedHttp1Semantics(string version)
            => CheckRequest("GET", "/", "127.0.0.1", true, version: version);

        [TestCase("HTTP/1.2", false)]
        [TestCase("HTTP/1.2", true)]
        [TestCase("HTTP/1.9", false)]
        [TestCase("HTTP/1.9", true)]
        public Task HigherMinorVersionsReadFixedAndChunkedBodies(string version, bool chunked)
            => CheckRequest("POST", "/", "127.0.0.1", true, version: version, payload: "abc", chunked: chunked);

        [TestCase("HTTP/1.10")]
        [TestCase("HTTP/1.x")]
        [TestCase("http/1.2")]
        [TestCase("HTTP/2.0")]
        [TestCase("HTTP/0.9")]
        public Task InvalidOrUnsupportedVersionStillFailsBeforeDispatch(string version)
            => CheckRequest("GET", "/", "127.0.0.1", false, version: version);

        [TestCase("HTTP/1.2")]
        [TestCase("HTTP/1.9")]
        public async Task HigherMinorVersionsSendContinueBeforeWaitingForTheBody(string version)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Post, async context =>
            {
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo("abc"));
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("POST / " + version
                    + "\r\nHost: 127.0.0.1\r\nConnection: close\r\nContent-Length: 3\r\nExpect: 100-continue\r\n\r\n"), stop.Token);
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                Assert.That(await reader.ReadLineAsync(stop.Token), Is.EqualTo("HTTP/1.1 100 Continue"));
                Assert.That(await reader.ReadLineAsync(stop.Token), Is.Empty);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("abc"), stop.Token);
                var response = await reader.ReadToEndAsync(stop.Token);
                Assert.That(response, Does.StartWith("HTTP/1.1 200 ").And.Contain("accepted"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase("HTTP/1.2", "missing-host")]
        [TestCase("HTTP/1.9", "missing-host")]
        [TestCase("HTTP/1.2", "duplicate-host")]
        [TestCase("HTTP/1.9", "duplicate-host")]
        [TestCase("HTTP/1.2", "length-and-transfer")]
        [TestCase("HTTP/1.9", "length-and-transfer")]
        public Task HigherMinorVersionsRetainStrictFraming(string version, string reason)
        {
            var headers = reason switch
            {
                "duplicate-host" => "Host: other.invalid\r\n",
                "length-and-transfer" => "Content-Length: 3\r\nTransfer-Encoding: chunked\r\n",
                _ => "",
            };
            return CheckRequest("POST", "/", "127.0.0.1", false, version: version,
                extraHeaders: headers, includeHost: reason != "missing-host");
        }

        private static async Task CheckRequest(string method, string target, string authority, bool valid, bool absoluteAuthority = false, bool unregisteredTarget = false, string version = "HTTP/1.1", string? payload = null, bool chunked = false, string extraHeaders = "", bool includeHost = true)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            if (absoluteAuthority) target = unregisteredTarget ? url.Replace("127.0.0.1", "example.invalid", StringComparison.Ordinal) : url;
            var calls = 0;
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any, async context =>
            {
                calls++;
                Assert.That(context.Request.Url.Host, Is.EqualTo("127.0.0.1"));
                Assert.That(context.Request.Headers["Host"], Is.EqualTo(authority));
                Assert.That(context.Request.RawTarget, Is.EqualTo(target));
                if (target == "*") Assert.That(context.Request.Url.AbsolutePath, Is.EqualTo("/"));
                Assert.That(context.Request.ProtocolVersion.ToString(), Is.EqualTo(version.Substring(5)));
                Assert.That(context.Response.ProtocolVersion.ToString(), Is.EqualTo(version == "HTTP/1.0" ? "1.0" : "1.1"));
                if (payload != null)
                {
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                    Assert.That(Encoding.ASCII.GetString(body.ToArray()), Is.EqualTo(payload));
                    Assert.That(context.Request.ContentLength64, Is.EqualTo(chunked ? -1 : payload.Length));
                }
                await context.SendStringAsync("accepted", "text/plain", Encoding.UTF8);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", new Uri(url).Port, stop.Token);
                var framing = payload == null ? "" : chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + payload.Length + "\r\n";
                var bodyWire = payload == null ? "" : chunked ? "3\r\n" + payload + "\r\n0\r\n\r\n" : payload;
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(method + " " + target
                    + " " + version + "\r\n" + (includeHost ? "Host: " + authority + "\r\n" : "")
                    + "Connection: close\r\n" + framing + extraHeaders + "\r\n" + bodyWire), stop.Token);
                using var received = new MemoryStream();
                await client.GetStream().CopyToAsync(received, stop.Token);
                var response = Encoding.ASCII.GetString(received.ToArray());
                if (unregisteredTarget) Assert.That(response, Is.Empty);
                else Assert.That(response, Does.StartWith(valid ? (version == "HTTP/1.0" ? "HTTP/1.0 200 " : "HTTP/1.1 200 ") : "HTTP/1.1 400 "));
                Assert.That(calls, Is.EqualTo(valid && !unregisteredTarget ? 1 : 0));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
