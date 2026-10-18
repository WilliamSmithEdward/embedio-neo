using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Health;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class SecurityHeadersTest
    {
        [TestCase(10, false)]
        [TestCase(11, false)]
        [TestCase(20, false)]
        [TestCase(10, true)]
        [TestCase(11, true)]
        [TestCase(20, true)]
        public async Task SecurityHeadersProtectOwnedTcpResponses(int protocol, bool tls)
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();
            var prefix = (tls ? "https" : "http") + "://127.0.0.1:" + port + "/";
            using var certificate = WireCertificate();
            using var server = new WebServer(o =>
            {
                o.WithUrlPrefix(prefix);

                if (tls) o.WithCertificate(certificate);

                o.SecurityHeaders = SecurityHeadersWireSmoke.DefaultPolicy();
            }).WithModule(SecurityHeadersWireSmoke.Application());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var ready = new HealthCheckService(server);
            var running = server.RunAsync(stop.Token);

            try
            {
                await ready.WaitForReadyAsync(TimeSpan.FromSeconds(5));
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                    {
                        RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString()
                            == certificate.GetCertHashString()
                    }
                };
                using var client = new HttpClient(handler);
                var version = protocol == 10
                    ? HttpVersion.Version10
                    : protocol == 20 ? HttpVersion.Version20 : HttpVersion.Version11;
                await SecurityHeadersWireSmoke.Sequence(client, prefix, version, tls, stop.Token);
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static X509Certificate2 WireCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            );
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1)
            );
            return X509CertificateLoader.LoadPkcs12(
                generated.Export(X509ContentType.Pkcs12),
                null,
                X509KeyStorageFlags.Exportable
            );
        }
    }

    internal static class SecurityHeadersWireSmoke
    {
        internal static SecurityHeadersPolicy DefaultPolicy() =>
            new("default-src 'self'", hstsMaxAge: TimeSpan.FromSeconds(42));

        internal static ActionModule Application() => new(async context =>
            {
                var path = context.Request.Url.AbsolutePath;

                if (path == "/nonce/bounded")
                    new SecurityHeadersPolicy("script-src " + string.Concat(Enumerable.Repeat("'nonce-{nonce}' ", 77))).Apply(context);

                else if (path.StartsWith("/nonce", StringComparison.Ordinal))
                    SecurityHeadersFixtures
                        .NoncePolicy()
                        .Apply(context);

                if (path == "/error")
                {
                    context.Response.Headers.Clear();
                    throw new InvalidOperationException("fixture error");
                }

                if (path == "/nonce/range-header")
                {
                    context.Response.Headers["Content-Range"] = "bytes 0-1/2";
                    await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2 }, context.CancellationToken);
                    return;
                }

                if (path == "/nonce/304")
                {
                    context.Response.StatusCode = 304;
                    return;
                }

                if (path == "/nonce/206")
                {
                    context.Response.StatusCode = 206;
                    return;
                }

                if (path == "/nonce/empty")
                {
                    context.Response.StatusCode = 204;
                    return;
                }

                var body = path.StartsWith("/nonce", StringComparison.Ordinal) ? context.GetSecurityHeadersNonce() : "static";
                context.Response.Headers["Content-Security-Policy"] = "default-src *";
                context.Response.Headers["Cache-Control"] = "public, max-age=600";
                context.Response.Headers["ETag"] = "\"stale\"";
                context.Response.ContentType = "text/plain";
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes, context.CancellationToken);
                await context.Response.OutputStream.FlushAsync(context.CancellationToken);
                Assert.That(() => new SecurityHeadersPolicy().Apply(context), Throws.InvalidOperationException);
            });

        internal static async Task Sequence(
            HttpClient client,
            string prefix,
            Version version,
            bool tls,
            CancellationToken token
        )
        {
            string? previous = null;

            foreach (var path in new[]
            {
                "nonce", "nonce", "nonce/bounded", "static", "error", "nonce/304", "nonce/206", "nonce/range-header",
                "nonce/empty"
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, prefix + path)
                {
                    Version = version,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                request.Headers.Add("X-Forwarded-Proto", tls ? "http" : "https");
                using var response = await client.SendAsync(request, token);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(response.Version, Is.EqualTo(version));
                    Assert.That(
                        SecurityHeadersFixtures.Header(response, "X-Content-Type-Options"),
                        Is.EqualTo("nosniff")
                    );
                    Assert.That(
                        SecurityHeadersFixtures.Header(response, "Strict-Transport-Security"),
                        Is.EqualTo(tls && !path.StartsWith("nonce", StringComparison.Ordinal) ? "max-age=42" : "")
                    );
                }

                var csp = SecurityHeadersFixtures.Header(response, "Content-Security-Policy");

                if (path.StartsWith("nonce", StringComparison.Ordinal))
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(SecurityHeadersFixtures.Header(response, "Cache-Control"), Is.EqualTo("no-store"));
                        Assert.That(SecurityHeadersFixtures.Header(response, "ETag"), Is.Empty);
                    }

                    if (path is "nonce" or "nonce/bounded")
                    {
                        var body = await response.Content.ReadAsStringAsync(token);

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(Convert.FromBase64String(body), Has.Length.EqualTo(32));
                            Assert.That(csp.Length, Is.LessThanOrEqualTo(4096));
                            Assert.That(body, Is.Not.EqualTo(previous));
                        }

                        previous = body;

                        using (Assert.EnterMultipleScope())
                        {
                            Assert.That(csp, Does.Contain("'nonce-" + body + "'"));
                            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        }
                    }

                    else
                        Assert.That(
                            response.StatusCode,
                            Is.EqualTo(
                                path == "nonce/empty" ? HttpStatusCode.NoContent : HttpStatusCode.InternalServerError
                            )
                        );
                }

                else
                {
                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(csp, Is.EqualTo("default-src 'self'"));
                        Assert.That(
                            response.StatusCode,
                            Is.EqualTo(path == "error" ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
                        );
                    }
                }
            }
        }
    }

    public partial class Http3ListenerTest
    {
        [Test]
        public async Task SecurityHeadersProtectOwnedHttp3Responses()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o =>
            {
                o
                    .WithUrlPrefix(prefix)
                    .WithMode(HttpListenerMode.EmbedIOHttp3)
                    .WithCertificate(certificate);
                o.SecurityHeaders = SecurityHeadersWireSmoke.DefaultPolicy();
            }).WithModule(SecurityHeadersWireSmoke.Application());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);

            try
            {
                await SecurityHeadersWireSmoke.Sequence(client, prefix, HttpVersion.Version30, true, stop.Token);
            }

            finally
            {
                stop.Cancel();
                await running;
            }
        }
    }
}
