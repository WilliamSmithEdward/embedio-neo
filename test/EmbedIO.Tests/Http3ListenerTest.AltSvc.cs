using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Combined hosting advertises its HTTP/3 endpoint as Kestrel does: every response
    // carries Alt-Svc for the request's port unless the application set its own (#525).
    public partial class Http3ListenerTest
    {
        private static string Advertised(string prefix) => $"h3=\":{new Uri(prefix).Port}\"; ma=86400";

        // HttpClient parses Alt-Svc and writes it back without "ma=86400", the default
        // lifetime (RFC 7838 section 3.1), so values read through it may take this form.
        private static string AdvertisedAsParsed(string prefix) => $"h3=\":{new Uri(prefix).Port}\"";

        private static WebServer AltSvcServer(
            HttpListenerMode mode,
            X509Certificate2 certificate,
            string prefix,
            bool advertise = true,
            string? applicationValue = null
        )
        {
            var options = new WebServerOptions().WithMode(mode).WithCertificate(certificate).WithUrlPrefix(prefix);
            options.AdvertiseHttp3 = advertise;

            return new WebServer(options).WithAction(
                "/",
                HttpVerbs.Get,
                async context =>
                {
                    if (applicationValue != null) context.Response.Headers["Alt-Svc"] = applicationValue;

                    await context.SendStringAsync(
                        context.Request.ProtocolVersion.ToString(),
                        "text/plain",
                        WebServer.Utf8NoBomEncoding
                    );
                }
            );
        }

        // Every Alt-Svc field line the client received, unparsed, and the protocol that served the request.
        private static async Task<(string[] AltSvc, string Served)> GetAltSvcAsync(
            X509Certificate2 certificate,
            string prefix,
            Version version,
            CancellationToken token
        )
        {
            using var client = Client(certificate);
            client.DefaultRequestVersion = version;
            using var response = await client.GetAsync(prefix, token);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Version, Is.EqualTo(version));
            string[] values = response.Headers.NonValidated.TryGetValues("Alt-Svc", out var found) ? [.. found] : [];
            return (values, await response.Content.ReadAsStringAsync(token));
        }

        private static async Task<(string[] AltSvc, string Served)> ServeAltSvcAsync(
            WebServer server,
            X509Certificate2 certificate,
            string prefix,
            Version version
        )
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);

            try
            {
                return await GetAltSvcAsync(certificate, prefix, version, stop.Token);
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase("1.1")]
        [TestCase("2.0")]
        [TestCase("3.0")]
        public async Task CombinedHostingAdvertisesHttp3OnEveryProtocol(string version)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = AltSvcServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            var (altSvc, served) = await ServeAltSvcAsync(server, certificate, prefix, Version.Parse(version));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(served, Is.EqualTo(version));
                Assert.That(
                    altSvc,
                    Is.EqualTo(new[] { Advertised(prefix) }).Or.EqualTo(new[] { AdvertisedAsParsed(prefix) })
                );
            }
        }

        // The field line as it leaves the server, which HttpClient would rewrite.
        [Test]
        public async Task CombinedHostingSendsKestrelsAltSvcValue()
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            var uri = new Uri(prefix);
            using var server = AltSvcServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(uri.Host, uri.Port, stop.Token);
                await using var tls = new SslStream(tcp.GetStream());
                await tls.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions
                    {
                        TargetHost = uri.Host,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                        RemoteCertificateValidationCallback = (_, peer, _, _) =>
                            peer?.GetCertHashString() == certificate.GetCertHashString()
                    },
                    stop.Token
                );
                await tls.WriteAsync(
                    Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"),
                    stop.Token
                );
                using var reader = new StreamReader(tls, Encoding.ASCII);
                var head = new List<string>();

                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                    head.Add(line);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(head[0], Is.EqualTo("HTTP/1.1 200 OK"));
                    Assert.That(
                        head.Where(line => line.StartsWith("Alt-Svc:", StringComparison.OrdinalIgnoreCase)),
                        Is.EqualTo(new[] { "Alt-Svc: " + Advertised(prefix) })
                    );
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase("1.1", "clear")]
        [TestCase("2.0", "clear")]
        [TestCase("3.0", "h3=\"other.example:443\"; ma=60")]
        public async Task CombinedHostingSendsTheApplicationsAltSvcUnchanged(string version, string value)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = AltSvcServer(HttpListenerMode.EmbedIOCombined, certificate, prefix, applicationValue: value);
            var (altSvc, _) = await ServeAltSvcAsync(server, certificate, prefix, Version.Parse(version));
            Assert.That(altSvc, Is.EqualTo(new[] { value }));
        }

        [TestCase("1.1")]
        [TestCase("2.0")]
        [TestCase("3.0")]
        public async Task CombinedHostingWithoutAdvertisementSendsNoAltSvc(string version)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = AltSvcServer(HttpListenerMode.EmbedIOCombined, certificate, prefix, advertise: false);
            var (altSvc, served) = await ServeAltSvcAsync(server, certificate, prefix, Version.Parse(version));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(served, Is.EqualTo(version));
                Assert.That(altSvc, Is.Empty);
            }
        }

        // EmbedIO serves no HTTP/3 and EmbedIOHttp3 serves nothing else, so neither has an alternative to name.
        [TestCase(HttpListenerMode.EmbedIO, "1.1")]
        [TestCase(HttpListenerMode.EmbedIO, "2.0")]
        [TestCase(HttpListenerMode.EmbedIOHttp3, "3.0")]
        public async Task SingleTransportModesSendNoAltSvc(HttpListenerMode mode, string version)
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = AltSvcServer(mode, certificate, prefix);
            var (altSvc, served) = await ServeAltSvcAsync(server, certificate, prefix, Version.Parse(version));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(served, Is.EqualTo(version));
                Assert.That(altSvc, Is.Empty);
            }
        }

        // The discovery the header exists for: a .NET client allowed to go higher starts on
        // HTTP/2, reads Alt-Svc and sends the following requests over HTTP/3.
        [Test]
        public async Task DotNetClientsMoveToHttp3AfterTheFirstCombinedResponse()
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            using var server = AltSvcServer(HttpListenerMode.EmbedIOCombined, certificate, prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);

            try
            {
                using var client = Client(certificate);
                client.DefaultRequestVersion = HttpVersion.Version20;
                client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                var served = new List<(Version Client, string Server)>();

                for (var request = 0; request < 11; request++)
                {
                    using var response = await client.GetAsync(prefix, stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    served.Add((response.Version, await response.Content.ReadAsStringAsync(stop.Token)));
                }

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(served[0], Is.EqualTo((HttpVersion.Version20, "2.0")));
                    Assert.That(served.Skip(1), Is.All.EqualTo((HttpVersion.Version30, "3.0")));
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
