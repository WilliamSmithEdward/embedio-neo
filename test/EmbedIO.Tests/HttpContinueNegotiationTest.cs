using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpContinueNegotiationTest
    {
        [TestCase(1, false, true)]
        [TestCase(2, false, true)]
        [TestCase(3, false, true)]
        [TestCase(1, true, true)]
        [TestCase(2, true, true)]
        [TestCase(3, true, true)]
        [TestCase(2, false, false)]
        [TestCase(2, true, false)]
        public async Task ContinueAllowsBodyWithoutClientFallbackTimer(int major, bool list, bool secure)
        {
            var target = typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (major == 2 && secure && target?.StartsWith(".NETStandard", StringComparison.Ordinal) == true)
                Assert.Ignore("The .NET Standard asset does not advertise HTTP/2 through TLS ALPN; cleartext prior knowledge is covered separately.");
            if (major == 3)
            {
                var supported = QuicListener.IsSupported && QuicConnection.IsSupported
                    && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") != null;
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
                if (!supported) Assert.Ignore("The selected asset or host has no HTTP/3 transport.");
            }
            // The non-Schannel QUIC backend exports the server credential to PKCS#12.
            using var certificate = HttpsSmoke.CreateCertificate(major == 3
                ? X509KeyStorageFlags.Exportable
                : X509KeyStorageFlags.DefaultKeySet);
            var url = HttpsSmoke.GetUrl();
            if (major == 3)
            {
                using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                url = $"https://localhost:{((IPEndPoint)(probe.LocalEndPoint ?? throw new AssertionException("Missing UDP endpoint."))).Port}/";
            }
            if (!secure) url = url.Replace("https:", "http:", StringComparison.Ordinal);
            var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                Expect100ContinueTimeout = TimeSpan.FromSeconds(30),
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected,
                },
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var server = new WebServer(major == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO, certificate, url)
                .WithAction("/", HttpVerbs.Post, async context =>
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    var text = await reader.ReadToEndAsync(context.CancellationToken);
                    await context.SendStringAsync(text, "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                if (running.IsCompleted) await running;
                for (var round = 0; round < 2; ++round)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Version = major == 1 ? HttpVersion.Version11 : new Version(major, 0),
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = new StringContent("continue-payload-" + round),
                    };
                    request.Headers.ExpectContinue = true;
                    if (list) request.Headers.TryAddWithoutValidation("Expect", "100-continue");
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var response = await client.SendAsync(request, deadline.Token);
                    Assert.That(response.Version.Major, Is.EqualTo(major));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(deadline.Token), Is.EqualTo("continue-payload-" + round));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
