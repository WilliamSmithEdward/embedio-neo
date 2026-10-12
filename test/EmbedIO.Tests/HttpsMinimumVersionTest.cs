using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpsMinimumVersionTest
    {
        [TestCase(SslProtocols.Tls12)]
        [TestCase(SslProtocols.Tls13)]
        public async Task AllowedVersionPreservesExactResponsesAndKeepAlive(SslProtocols protocol)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var address = new Uri(HttpsSmoke.GetUrl());
            using var server = CreateServer(address, certificate, null);
            using var stop = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RequireServerProtocolAsync(certificate, protocol, deadline.Token);
            var running = server.RunAsync(stop.Token);
            try
            {
                await ReadTwoResponsesAsync(address, certificate, protocol, deadline.Token);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase("Tls")]
        [TestCase("Tls11")]
        public async Task LegacyClientDoesNotDispatchAndHealthyTls12StillWorks(string legacyName)
        {
            // This preservation case alone cannot attribute failure to the
            // server: a platform may refuse the client's obsolete offer first.
            // The separate permissive-provider baseline comparison is required.
            Assert.That(Enum.TryParse<SslProtocols>(legacyName, out var protocol), Is.True);
            using var certificate = HttpsSmoke.CreateCertificate();
            var address = new Uri(HttpsSmoke.GetUrl());
            var handled = 0;
            using var server = CreateServer(address, certificate, () => Interlocked.Increment(ref handled));
            using var stop = new CancellationTokenSource();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var running = server.RunAsync(stop.Token);
            try
            {
                using (var peer = new TcpClient())
                {
                    await peer.ConnectAsync(IPAddress.Loopback, address.Port, deadline.Token);
                    using var tls = CreatePeer(peer, certificate);
                    var rejected = false;
                    try { await tls.AuthenticateAsClientAsync(ClientOptions(protocol), deadline.Token); }
                    catch (AuthenticationException) { rejected = true; }
                    catch (IOException) { rejected = true; }
                    Assert.That(rejected, Is.True, "An obsolete-only connection must not finish authentication.");
                }
                Assert.That(Volatile.Read(ref handled), Is.Zero);
                await ReadTwoResponsesAsync(address, certificate, SslProtocols.Tls12, deadline.Token);
                Assert.That(Volatile.Read(ref handled), Is.EqualTo(2));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private static WebServer CreateServer(Uri address, X509Certificate2 certificate, Action? onRequest)
            => new WebServer(options => options.WithUrlPrefix(address.AbsoluteUri)
                .WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, context =>
                {
                    onRequest?.Invoke();
                    var body = Encoding.ASCII.GetBytes("encrypted");
                    context.Response.ContentLength64 = body.Length;
                    return context.Response.OutputStream.WriteAsync(body, 0, body.Length);
                });
        private static SslStream CreatePeer(TcpClient peer, X509Certificate2 certificate)
        {
            var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            return new SslStream(peer.GetStream(), false, (_, observed, _, errors) => observed != null
                && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && observed.GetCertHashString(HashAlgorithmName.SHA256) == expected);
        }

        private static SslClientAuthenticationOptions ClientOptions(SslProtocols protocol)
            => new()
            {
                TargetHost = "localhost",
                EnabledSslProtocols = protocol,
                ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
            };

        private static async Task RequireServerProtocolAsync(X509Certificate2 certificate,
            SslProtocols protocol, CancellationToken token)
        {
            // Enum availability is not provider support. In particular, the
            // current macOS server SslStream provider has no TLS 1.3 support.
            // Skip only its actual public-provider unsupported-version result;
            // authentication, certificate, I/O and deadline errors still fail.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = AuthenticateControlServerAsync(listener, certificate, protocol, release.Task, token);
            var client = AuthenticateControlClientAsync(((IPEndPoint)listener.LocalEndpoint).Port,
                certificate, protocol, release, token);
            var joined = Task.WhenAll(server, client);
            try { await joined; }
            catch (PlatformNotSupportedException) when (protocol == SslProtocols.Tls13 && UnsupportedProvider(server))
            {
                Assert.Ignore("The public server TLS provider does not support TLS 1.3; TLS 1.2 remains mandatory.");
            }
            catch (AuthenticationException) when (protocol == SslProtocols.Tls13 && UnsupportedProvider(server))
            {
                Assert.Ignore("The public server TLS provider does not support TLS 1.3; TLS 1.2 remains mandatory.");
            }
        }

        private static bool UnsupportedProvider(Task server)
        {
            for (var error = server.Exception?.InnerException; error != null; error = error.InnerException)
                if (error is PlatformNotSupportedException) return true;
            return false;
        }

        private static async Task AuthenticateControlServerAsync(TcpListener listener,
            X509Certificate2 certificate, SslProtocols protocol, Task release, CancellationToken token)
        {
            using var peer = await listener.AcceptTcpClientAsync(token);
            using var tls = new SslStream(peer.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = protocol,
            }, token);
            Assert.That(tls.SslProtocol, Is.EqualTo(protocol));
            await release.WaitAsync(token);
        }

        private static async Task AuthenticateControlClientAsync(int port, X509Certificate2 certificate,
            SslProtocols protocol, TaskCompletionSource<bool> release, CancellationToken token)
        {
            try
            {
                using var peer = new TcpClient();
                await peer.ConnectAsync(IPAddress.Loopback, port, token);
                using var tls = CreatePeer(peer, certificate);
                // Offer a provider-compatible range during the capability probe.
                // The control server still allows only the requested version and
                // both endpoints assert it; TLS 1.2 cannot make a TLS 1.3 probe pass.
                // This lets a TLS 1.2-only client reach the server's actual
                // unsupported-version result instead of failing locally first.
                var offer = protocol == SslProtocols.Tls13 ? SslProtocols.Tls12 | SslProtocols.Tls13 : protocol;
                await tls.AuthenticateAsClientAsync(ClientOptions(offer), token);
                Assert.That(tls.SslProtocol, Is.EqualTo(protocol));
            }
            finally { release.TrySetResult(true); }
        }

        private static async Task ReadTwoResponsesAsync(Uri address, X509Certificate2 certificate,
            SslProtocols protocol, CancellationToken token)
        {
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, address.Port, token);
            using var tls = CreatePeer(peer, certificate);
            await tls.AuthenticateAsClientAsync(ClientOptions(protocol), token);
            Assert.That(tls.SslProtocol, Is.EqualTo(protocol));
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {address.Authority}\r\n\r\n");
            var one = new byte[1];
            for (var response = 0; response < 2; response++)
            {
                await tls.WriteAsync(request, token);
                var head = new StringBuilder();
                while (true)
                {
                    await tls.ReadExactlyAsync(one, token);
                    head.Append((char)one[0]);
                    Assert.That(head.Length, Is.LessThanOrEqualTo(16384));
                    if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
                }
                Assert.That(head.ToString(), Does.StartWith("HTTP/1.1 200 "));
                Assert.That(head.ToString().Contains("\r\nContent-Length: 9\r\n", StringComparison.OrdinalIgnoreCase), Is.True);
                var body = new byte[9];
                await tls.ReadExactlyAsync(body, token);
                Assert.That(Encoding.ASCII.GetString(body), Is.EqualTo("encrypted"));
            }
        }
    }
}
