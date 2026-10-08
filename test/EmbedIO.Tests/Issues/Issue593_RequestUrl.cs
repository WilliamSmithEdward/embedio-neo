using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue593_RequestUrl
    {
        private const string Target = "/Mixed/Case%20Path?Token=AbC%2BDef&Other=UPPER";

        [TestCase(false)]
        [TestCase(true)]
        public async Task OriginTargetKeepsTransportAndPathAcrossKeepAlive(bool secure)
        {
            await using var host = new Host(secure);
            using var client = HttpsSmoke.CreateClient(host.Certificate);
            for (var i = 0; i < 3; i++)
                Check(await client.GetStringAsync(host.Url + Target.Substring(1)), host, Target);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AbsoluteTargetPreservesPathAndQueryCase(bool secure)
        {
            await using var host = new Host(secure);
            var target = host.Url.Replace(secure ? "https:" : "http:", secure ? "HTTPS:" : "HTTP:", StringComparison.Ordinal) + Target.Substring(1);
            Check(await Fetch(host, target, new Uri(host.Url).Authority), host, target);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task HostWithoutPortSupportsIpv4AndBracketedIpv6(bool secure, bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            await using var host = new Host(secure, ipv6);
            Check(await Fetch(host, Target, ipv6 ? "[::1]" : "127.0.0.1"), host, Target);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http10WithoutHostUsesLocalAuthority(bool secure)
        {
            await using var host = new Host(secure);
            Check(await Fetch(host, Target, null, "HTTP/1.0"), host, Target);
        }

        [Test]
        public async Task SimultaneousHttpAndHttpsListenersKeepSchemesIndependent()
        {
            await using var http = new Host(false);
            await using var https = new Host(true);
            using var plainClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var tlsClient = HttpsSmoke.CreateClient(https.Certificate);
            for (var batch = 0; batch < 10; batch++)
            {
                var responses = await Task.WhenAll(plainClient.GetStringAsync(http.Url + Target.Substring(1)),
                    tlsClient.GetStringAsync(https.Url + Target.Substring(1)));
                Check(responses[0], http, Target);
                Check(responses[1], https, Target);
            }
        }

        private static void Check(string response, Host host, string rawTarget)
        {
            var fields = response.Split('\n');
            Assert.That(fields.Length, Is.EqualTo(4));
            var url = new Uri(fields[0]);
            Assert.Multiple(() =>
            {
                Assert.That(url.Scheme, Is.EqualTo(host.Secure ? "https" : "http"));
                Assert.That(bool.Parse(fields[1]), Is.EqualTo(host.Secure));
                Assert.That(url.Host, Is.EqualTo(new Uri(host.Url).Host));
                Assert.That(url.Port, Is.EqualTo(new Uri(host.Url).Port));
                Assert.That(url.PathAndQuery, Is.EqualTo(Target));
                Assert.That(fields[2], Is.EqualTo(rawTarget));
                Assert.That(fields[3], Is.EqualTo("AbC+Def"));
            });
        }

        private static async Task<string> Fetch(Host host, string target, string? authority, string version = "HTTP/1.1")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient();
            await client.ConnectAsync(host.Address, new Uri(host.Url).Port, timeout.Token);
            using Stream stream = host.Secure
                ? new SslStream(client.GetStream(), false, (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == host.Certificate.GetCertHashString(HashAlgorithmName.SHA256))
                : client.GetStream();
            if (stream is SslStream tls)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
            var header = authority == null ? string.Empty : $"Host: {authority}\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} {version}\r\n{header}Connection: close\r\n\r\n"), timeout.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
            var status = await reader.ReadLineAsync(timeout.Token);
            Assert.That(status, Does.Contain(" 200 "));
            var length = -1;
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = int.Parse(line.Substring("Content-Length:".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(length, Is.InRange(1, 4096));
            var body = new char[length];
            Assert.That(await reader.ReadBlockAsync(body.AsMemory(), timeout.Token), Is.EqualTo(length));
            return new string(body);
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop = new();
            private readonly Task _running;

            public Host(bool secure, bool ipv6 = false)
            {
                Secure = secure;
                Address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
                Certificate = HttpsSmoke.CreateCertificate();
                using var port = new TcpListener(Address, 0);
                port.Start();
                Url = $"{(secure ? "https" : "http")}://{(ipv6 ? "[::1]" : "127.0.0.1")}:{((IPEndPoint)port.LocalEndpoint).Port}/";
                port.Stop();
                _server = new WebServer(options => options.WithUrlPrefix(Url)
                    .WithMode(HttpListenerMode.EmbedIO).WithCertificate(Certificate))
                    .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                    {
                        var data = Encoding.UTF8.GetBytes($"{context.Request.Url.AbsoluteUri}\n{context.Request.IsSecureConnection}\n{context.Request.RawTarget}\n{context.Request.QueryString["Token"]}");
                        context.Response.ContentType = "text/plain";
                        context.Response.ContentLength64 = data.Length;
                        await context.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
                    }));
                _running = _server.RunAsync(_stop.Token);
            }

            public bool Secure { get; }
            public IPAddress Address { get; }
            public string Url { get; }
            public X509Certificate2 Certificate { get; }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
                finally { _server.Dispose(); _stop.Dispose(); Certificate.Dispose(); }
            }
        }
    }
}
