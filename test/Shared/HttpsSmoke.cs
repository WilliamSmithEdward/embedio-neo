using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;

namespace EmbedIO.PlatformTests
{
    // Test-only certificate and trust pin; never used by production packages.
    internal static class HttpsSmoke
    {
        internal static X509Certificate2 CreateCertificate(X509KeyStorageFlags keyStorageFlags = X509KeyStorageFlags.DefaultKeySet)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            // Exercise private-key provisioning via PKCS#12 as a deployed app would.
            // Windows Schannel requires a key container for server authentication.
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, keyStorageFlags);
        }

        internal static string GetUrl()
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            return $"https://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
        }

        internal static HttpClient CreateClient(X509Certificate2 certificate)
        {
            var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            return new HttpClient(new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    // Trust exactly the generated leaf, retaining hostname validation.
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected,
                },
            })
            { Timeout = TimeSpan.FromSeconds(10) };
        }

        internal static WebServer CreateServer(string url, X509Certificate2 certificate)
            => new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    if (context.Request.Url.Scheme != (context.Request.IsSecureConnection ? "https" : "http"))
                        throw new InvalidOperationException("Request URL scheme disagrees with transport security.");
                    return context.SendStringAsync(context.Request.IsSecureConnection ? "encrypted" : "plaintext",
                        "text/plain", WebServer.Utf8NoBomEncoding);
                }));

        internal static async Task RunAsync()
        {
            using var certificate = CreateCertificate();
            var url = GetUrl();
            using var server = CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                if (server.State != WebServerState.Listening)
                    throw new InvalidOperationException("HTTPS listener did not start.");
                using var client = CreateClient(certificate);
                // Sequential requests also exercise TLS keep-alive reuse.
                for (var i = 0; i < 2; i++)
                    if (await client.GetStringAsync(url) != "encrypted")
                        throw new InvalidOperationException("Expected an encrypted HTTPS response.");

                using var untrusted = new HttpClient(new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(10) };
                try
                {
                    await untrusted.GetStringAsync(url);
                    throw new InvalidOperationException("An untrusted test certificate was accepted.");
                }
                catch (HttpRequestException) { }
                if (await client.GetStringAsync(url) != "encrypted")
                    throw new InvalidOperationException("Listener failed after a rejected client handshake.");
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
