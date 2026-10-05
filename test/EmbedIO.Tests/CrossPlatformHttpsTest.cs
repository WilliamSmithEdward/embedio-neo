using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class CrossPlatformHttpsTest
    {
        [Test]
        public async Task PrivateKeyCertificateServesHttpsAndRejectsUntrustedClients()
            => await HttpsSmoke.RunAsync();

        [Test]
        public async Task ClientRejectsAnUnexpectedCertificate()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            using var different = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            using var server = HttpsSmoke.CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = HttpsSmoke.CreateClient(different);
                await Assert.ThatAsync(async () => await client.GetStringAsync(url), Throws.TypeOf<HttpRequestException>());
                using var trusted = HttpsSmoke.CreateClient(certificate);
                Assert.That(await trusted.GetStringAsync(url), Is.EqualTo("encrypted"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public async Task ClientDetectsAnIncorrectCertificateHostname()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            using var server = HttpsSmoke.CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
                var errors = SslPolicyErrors.None;
                using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, observed) =>
                {
                    errors = observed;
                    return false;
                });
                await Assert.ThatAsync(async () => await tls.AuthenticateAsClientAsync("wrong.example"),
                    Throws.TypeOf<System.Security.Authentication.AuthenticationException>());
                Assert.That(errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch), Is.True);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task IncompleteOrPlaintextHandshakeDoesNotBlockOtherClients(bool plaintext)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            using var server = HttpsSmoke.CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                using var stalled = new TcpClient();
                await stalled.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
                if (plaintext)
                    await stalled.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"));
                using var client = HttpsSmoke.CreateClient(certificate);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("encrypted"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [Test]
        public async Task StopClosesAnIncompleteTlsHandshake()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            using var server = HttpsSmoke.CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var stalled = new TcpClient();
            try
            {
                await stalled.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
                using var client = HttpsSmoke.CreateClient(certificate);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("encrypted"));
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                var count = await stalled.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(count, Is.Zero);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
