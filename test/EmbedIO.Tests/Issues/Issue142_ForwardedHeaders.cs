using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Forwarding;
using EmbedIO.RateLimiting;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Opt-in resolution of the original client behind trusted proxies.
    [TestFixture]
    public sealed class Issue142_ForwardedHeaders
    {
        private const ForwardedHeaderSources XForwarded = ForwardedHeaderSources.XForwardedFor | ForwardedHeaderSources.XForwardedProto;

        // The loopback addresses a test client connects from.
        private static readonly string[] Loopback = { "127.0.0.1", "::1" };

        // Reports the effective values next to the connection's own, one "name=value" per line.
        private static ActionModule Report() => new(context =>
        {
            var forwarded = context.GetForwardedRequest();
            var lines = new[]
            {
                "outcome=" + forwarded.Outcome,
                "client=" + forwarded.ClientAddress,
                "scheme=" + forwarded.Scheme,
                "secure=" + forwarded.IsSecure,
                "host=" + (forwarded.Host ?? "(none)"),
                "hops=" + forwarded.HopsApplied,
                "hostRejected=" + forwarded.HostRejected,
                "physicalClient=" + forwarded.PhysicalRemoteEndPoint.Address,
                "physicalSecure=" + forwarded.PhysicalIsSecure,
                "requestRemote=" + context.Request.RemoteEndPoint.Address,
                "requestScheme=" + context.Request.Url.Scheme,
            };
            return context.SendStringAsync(string.Join("\n", lines), "text/plain", WebServer.DefaultEncoding);
        });

        private static ForwardedHeadersModule Module(ForwardedHeaderSources sources, params string[] trusted)
            => new ForwardedHeadersModule("/", sources).WithTrustedProxies(trusted.Length == 0 ? Loopback : trusted);

        private static async Task<Dictionary<string, string>> Send(HttpClient client, string url, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var (name, value) in headers)
                Assert.That(request.Headers.TryAddWithoutValidation(name, value), Is.True, name);

            using var response = await client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "The module never rejects a request.");
            var text = await response.Content.ReadAsStringAsync();
            return text.Split('\n').Select(line => line.Split(new[] { '=' }, 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        }

        private static async Task<Dictionary<string, string>> Through(ForwardedHeadersModule module, params (string Name, string Value)[] headers)
        {
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module, Report());
            using var client = new HttpClient();
            return await Send(client, server.Url + "x", headers);
        }

        private static void AssertUnchanged(Dictionary<string, string> result, string outcome)
        {
            Assert.That(result["outcome"], Is.EqualTo(outcome));
            Assert.That(result["client"], Is.EqualTo(result["physicalClient"]));
            Assert.That(result["scheme"], Is.EqualTo("http"));
            Assert.That(result["host"], Is.EqualTo("(none)"));
            Assert.That(result["hops"], Is.EqualTo("0"));
        }

        [Test]
        public async Task TrustedProxyHeadersAreAppliedBesideTheConnectionValues()
        {
            var result = await Through(Module(XForwarded), ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https"));
            Assert.That(result["outcome"], Is.EqualTo("Applied"));
            Assert.That(result["client"], Is.EqualTo("203.0.113.9"));
            Assert.That(result["scheme"], Is.EqualTo("https"));
            Assert.That(result["secure"], Is.EqualTo("True"));
            Assert.That(result["hops"], Is.EqualTo("1"));

            // The connection's own values are untouched.
            Assert.That(Loopback, Does.Contain(result["physicalClient"]));
            Assert.That(result["requestRemote"], Is.EqualTo(result["physicalClient"]));
            Assert.That(result["physicalSecure"], Is.EqualTo("False"));
            Assert.That(result["requestScheme"], Is.EqualTo("http"));
        }

        [Test]
        public async Task UntrustedPeerHeadersAreIgnored()
        {
            var result = await Through(Module(XForwarded, "10.0.0.0/8", "fd00::/8"), ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https"));
            AssertUnchanged(result, "UntrustedPeer");
        }

        [Test]
        public async Task TrustedProxyWithoutHeadersChangesNothing()
            => AssertUnchanged(await Through(Module(XForwarded)), "NoHeaders");

        // Only the hops the limit allows, and only while each named sender is itself trusted.
        [TestCase("203.0.113.9, 10.0.0.5", 1, "10.0.0.5", 1)]
        [TestCase("203.0.113.9, 10.0.0.5", 2, "203.0.113.9", 2)]
        [TestCase("203.0.113.9, 198.51.100.7", 2, "198.51.100.7", 1)]
        [TestCase("1.2.3.4, 203.0.113.9, 10.0.0.5", 2, "203.0.113.9", 2)]
        [TestCase("1.2.3.4, 10.0.0.6, 10.0.0.5", 16, "1.2.3.4", 3)]
        public async Task HopsAreWalkedFromTheNearestProxy(string forwardedFor, int limit, string client, int hops)
        {
            var module = Module(ForwardedHeaderSources.XForwardedFor, "127.0.0.1", "::1", "10.0.0.0/8");
            module.ForwardLimit = limit;
            var result = await Through(module, ("X-Forwarded-For", forwardedFor));
            Assert.That(result["outcome"], Is.EqualTo("Applied"));
            Assert.That(result["client"], Is.EqualTo(client));
            Assert.That(result["hops"], Is.EqualTo(hops.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        // A client cannot choose its address by prepending entries the trusted proxy appends to.
        [Test]
        public async Task ClientSuppliedEntriesBeyondTheTrustedChainAreIgnored()
        {
            var result = await Through(Module(ForwardedHeaderSources.XForwardedFor), ("X-Forwarded-For", "127.0.0.1, 203.0.113.9"));
            Assert.That(result["client"], Is.EqualTo("203.0.113.9"));
            Assert.That(result["hops"], Is.EqualTo("1"));
        }

        [TestCase("2001:db8::7", "2001:db8::7")]
        [TestCase("[2001:db8::7]:4711", "2001:db8::7")]
        [TestCase("203.0.113.9:4711", "203.0.113.9")]
        [TestCase("::ffff:203.0.113.9", "203.0.113.9")]
        public async Task XForwardedForAcceptsIpv4AndIpv6(string value, string client)
        {
            var result = await Through(Module(ForwardedHeaderSources.XForwardedFor), ("X-Forwarded-For", value));
            Assert.That(result["client"], Is.EqualTo(client));
        }

        [Test]
        public async Task SeparateHeaderLinesAreReadInOrder()
        {
            var module = Module(ForwardedHeaderSources.XForwardedFor, "127.0.0.1", "::1", "10.0.0.0/8");
            module.ForwardLimit = 2;
            var result = await Through(module, ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-For", "10.0.0.5"));
            Assert.That(result["client"], Is.EqualTo("203.0.113.9"));
            Assert.That(result["hops"], Is.EqualTo("2"));
        }

        [Test]
        public async Task ForwardedHeaderCarriesClientSchemeAndAllowedHost()
        {
            var module = Module(ForwardedHeaderSources.Forwarded).WithAllowedHosts("example.com");
            var result = await Through(module, ("Forwarded", "for=\"[2001:db8::7]:4711\";proto=HTTPS;host=\"Example.com:8443\";by=10.0.0.5"));
            Assert.That(result["outcome"], Is.EqualTo("Applied"));
            Assert.That(result["client"], Is.EqualTo("2001:db8::7"));
            Assert.That(result["scheme"], Is.EqualTo("https"));
            Assert.That(result["host"], Is.EqualTo("example.com:8443"));
            Assert.That(result["hostRejected"], Is.EqualTo("False"));
        }

        [Test]
        public async Task ForwardedHostsMustBeAllowed()
        {
            // No allowed hosts: the forwarded host is reported as rejected, never used.
            var noPolicy = await Through(Module(ForwardedHeaderSources.Forwarded), ("Forwarded", "for=203.0.113.9;host=evil.example"));
            Assert.That(noPolicy["host"], Is.EqualTo("(none)"));
            Assert.That(noPolicy["hostRejected"], Is.EqualTo("True"));
            Assert.That(noPolicy["client"], Is.EqualTo("203.0.113.9"));

            var portPinned = Module(ForwardedHeaderSources.XForwardedFor | ForwardedHeaderSources.XForwardedHost).WithAllowedHosts("example.com:443");
            var wrongPort = await Through(portPinned, ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Host", "example.com:8443"));
            Assert.That(wrongPort["host"], Is.EqualTo("(none)"));
            Assert.That(wrongPort["hostRejected"], Is.EqualTo("True"));

            var allowed = Module(ForwardedHeaderSources.XForwardedFor | ForwardedHeaderSources.XForwardedHost).WithAllowedHosts("example.com:443");
            var rightPort = await Through(allowed, ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Host", "example.com:443"));
            Assert.That(rightPort["host"], Is.EqualTo("example.com:443"));
        }

        // "unknown" and obfuscated identifiers keep the scheme but leave the client as it was.
        [TestCase("for=unknown;proto=https")]
        [TestCase("for=_hidden;proto=https")]
        [TestCase("proto=https")]
        public async Task HopsWithoutAnAddressApplyOnlyTheirScheme(string forwarded)
        {
            var result = await Through(Module(ForwardedHeaderSources.Forwarded), ("Forwarded", forwarded));
            Assert.That(result["outcome"], Is.EqualTo("Applied"));
            Assert.That(result["scheme"], Is.EqualTo("https"));
            Assert.That(result["client"], Is.EqualTo(result["physicalClient"]));
        }

        [Test]
        public async Task ForwardedAndXForwardedTogetherConflict()
        {
            var module = Module(ForwardedHeaderSources.Forwarded | ForwardedHeaderSources.XForwardedFor);
            AssertUnchanged(await Through(module, ("Forwarded", "for=203.0.113.9"), ("X-Forwarded-For", "198.51.100.7")), "Conflict");
        }

        // Headers the module was not told to read are ignored, whoever sent them.
        [Test]
        public async Task UnselectedHeadersAreIgnored()
        {
            var result = await Through(Module(ForwardedHeaderSources.XForwardedFor), ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "https"), ("Forwarded", "for=198.51.100.7"));
            Assert.That(result["client"], Is.EqualTo("203.0.113.9"));
            Assert.That(result["scheme"], Is.EqualTo("http"));
        }

        [TestCase("X-Forwarded-For", "not-an-ip")]
        [TestCase("X-Forwarded-For", "300.1.1.1")]
        [TestCase("X-Forwarded-For", "1")]
        [TestCase("X-Forwarded-For", "0x7f.0.0.1")]
        [TestCase("X-Forwarded-For", "203.0.113.9, ")]
        [TestCase("X-Forwarded-For", "203.0.113.9:99999")]
        [TestCase("X-Forwarded-For", "[fe80::1%eth0]")]
        [TestCase("X-Forwarded-Proto", "ftp")]
        [TestCase("Forwarded", "for=[2001:db8::7]")]
        [TestCase("Forwarded", "for=\"203.0.113.9")]
        [TestCase("Forwarded", "for=203.0.113.9;for=198.51.100.7")]
        [TestCase("Forwarded", "host=\"example.com/evil\"")]
        [TestCase("Forwarded", "host=\"user@example.com\"")]
        [TestCase("Forwarded", "for")]
        [TestCase("Forwarded", "proto=https;proto=http")]
        public async Task MalformedValuesApplyNothing(string header, string value)
        {
            var module = Module(ForwardedHeaderSources.Forwarded, "127.0.0.1", "::1").WithAllowedHosts("example.com");
            if (header != "Forwarded")
                module = Module(XForwarded);

            var result = await Through(module, (header, value));
            AssertUnchanged(result, "Malformed");
        }

        [Test]
        public async Task OversizedChainsApplyNothing()
        {
            var tooMany = string.Join(", ", Enumerable.Range(1, 33).Select(i => "10.0.0." + i));
            AssertUnchanged(await Through(Module(ForwardedHeaderSources.XForwardedFor), ("X-Forwarded-For", tooMany)), "Malformed");

            // One syntactically valid entry, an obfuscated identifier, longer than the module reads.
            var tooLong = "_" + new string('a', 9000);
            AssertUnchanged(await Through(Module(ForwardedHeaderSources.XForwardedFor), ("X-Forwarded-For", tooLong)), "Malformed");
        }

        [Test]
        public async Task RequestsOutsideTheBaseRouteAreNotProcessed()
        {
            var module = new ForwardedHeadersModule("/api", ForwardedHeaderSources.XForwardedFor).WithTrustedProxies(Loopback);
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(module, Report());
            using var client = new HttpClient();

            var inside = await Send(client, server.Url + "api/x", ("X-Forwarded-For", "203.0.113.9"));
            Assert.That(inside["client"], Is.EqualTo("203.0.113.9"));
            var outside = await Send(client, server.Url + "other", ("X-Forwarded-For", "203.0.113.9"));
            AssertUnchanged(outside, "NotProcessed");
        }

        // Each server applies its own policy to the same request.
        [Test]
        public async Task IndependentServersApplyTheirOwnPolicies()
        {
            await using var trusting = Issue138_GeneratedBinding.RunningServer.Start(Module(XForwarded), Report());
            await using var distrusting = Issue138_GeneratedBinding.RunningServer.Start(Module(XForwarded, "10.0.0.0/8"), Report());
            using var client = new HttpClient();
            var header = ("X-Forwarded-For", "203.0.113.9");

            Assert.That((await Send(client, trusting.Url + "x", header))["client"], Is.EqualTo("203.0.113.9"));
            Assert.That((await Send(client, distrusting.Url + "x", header))["outcome"], Is.EqualTo("UntrustedPeer"));
        }

        // A proxy that terminates TLS forwards over plain HTTP; this one forwards over TLS instead.
        [Test]
        public async Task PhysicalTlsIsReportedSeparately()
        {
            using var certificate = Certificate();
            var url = Resources.GetServerAddress().Replace("http://", "https://", StringComparison.Ordinal);
            using var cancellation = new CancellationTokenSource();
            using var server = new WebServer(HttpListenerMode.EmbedIO, certificate, url)
                .WithModule(Module(XForwarded))
                .WithModule(Report());
            var running = server.RunAsync(cancellation.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() },
                });

                var forwardedHttp = await Send(client, url + "x", ("X-Forwarded-For", "203.0.113.9"), ("X-Forwarded-Proto", "http"));
                Assert.That(forwardedHttp["scheme"], Is.EqualTo("http"));
                Assert.That(forwardedHttp["physicalSecure"], Is.EqualTo("True"));
                Assert.That(forwardedHttp["requestScheme"], Is.EqualTo("https"));

                var direct = await Send(client, url + "x");
                Assert.That(direct["scheme"], Is.EqualTo("https"));
                Assert.That(direct["outcome"], Is.EqualTo("NoHeaders"));
            }
            finally
            {
                await cancellation.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        // Behind one proxy, a rate limit keyed on the forwarded client gives each client its own budget.
        [Test]
        public async Task RateLimitsCanKeyOnTheForwardedClient()
        {
            using var limiter = PartitionedRateLimiter.Create<IHttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                RateLimitingModule.ClientKey(context.GetForwardedRequest().ClientAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
            await using var server = Issue138_GeneratedBinding.RunningServer.Start(Module(XForwarded), new RateLimitingModule("/", limiter), Report());
            using var client = new HttpClient();

            async Task<HttpStatusCode> Status(string forwardedFor)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "x");
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
                using var response = await client.SendAsync(request);
                return response.StatusCode;
            }

            Assert.That(await Status("203.0.113.9"), Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await Status("203.0.113.9"), Is.EqualTo((HttpStatusCode)429));
            Assert.That(await Status("198.51.100.7"), Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public void ConfigurationIsValidatedAndLockedAtStart()
        {
            Assert.Throws<ArgumentException>(() => _ = new ForwardedHeadersModule("/", ForwardedHeaderSources.None));
            Assert.Throws<ArgumentException>(() => _ = new ForwardedHeadersModule("/", (ForwardedHeaderSources)64));
            var module = new ForwardedHeadersModule("/", XForwarded);
            Assert.Throws<ArgumentException>(() => module.WithTrustedProxies("proxy.example"));
            Assert.Throws<ArgumentException>(() => module.WithTrustedProxies("10.0.0.0/33"));
            Assert.Throws<ArgumentException>(() => module.WithAllowedHosts("example.com/path"));
            Assert.Throws<ArgumentOutOfRangeException>(() => module.ForwardLimit = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => module.ForwardLimit = 17);
            Assert.That(module.IsFinalHandler, Is.False);
            Assert.That(module.ForwardLimit, Is.EqualTo(1));

            // Without trusted proxies the module cannot do anything, so starting it is an error.
            Assert.Throws<InvalidOperationException>(() => module.Start(CancellationToken.None));

            var hostWithoutPolicy = new ForwardedHeadersModule("/", ForwardedHeaderSources.XForwardedHost).WithTrustedProxies("10.0.0.1");
            Assert.Throws<InvalidOperationException>(() => hostWithoutPolicy.Start(CancellationToken.None));

            var started = new ForwardedHeadersModule("/", XForwarded).WithTrustedProxies("10.0.0.1");
            started.Start(CancellationToken.None);
            Assert.Throws<InvalidOperationException>(() => started.WithTrustedProxies("10.0.0.2"));
            Assert.Throws<InvalidOperationException>(() => started.WithAllowedHosts("example.com"));
            Assert.Throws<InvalidOperationException>(() => started.ForwardLimit = 2);
        }

        private static X509Certificate2 Certificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }
    }
}
