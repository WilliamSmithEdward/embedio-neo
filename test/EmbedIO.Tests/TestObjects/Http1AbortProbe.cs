using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    internal static class Http1AbortProbe
    {
        // Observe the accepted connection itself: HttpClient can retry a GET after
        // an empty response and then spend seconds connecting to the stopped port.
        internal static async Task AssertClosedWithoutResponseAsync(string prefix, X509Certificate2? certificate = null)
        {
            var uri = new Uri(prefix);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, deadline.Token);
            using var wire = client.GetStream();
            var expected = certificate?.GetCertHashString(HashAlgorithmName.SHA256);
            using var tls = certificate == null ? null : new SslStream(wire, true, (_, peer, _, errors) =>
                peer != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected);
            if (tls != null)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.Host,
                    ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
                }, deadline.Token);
            Stream transport = tls ?? (Stream)wire;
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n"), deadline.Token);
            var first = new byte[1];
            int count;
            try { count = await transport.ReadAsync(first, deadline.Token); }
            catch (IOException) { return; } // Reset is also a terminal response failure.
            Assert.That(count, Is.Zero, "Aborting an unwritten response must not synthesize a status line.");
        }
    }
}
