using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Http1ClosureProbe
{
    internal static class Program
    {
        private static async Task Main()
        {
            var assembly = typeof(WebServer).Assembly.Location;
            Console.WriteLine(RuntimeInformation.OSDescription);
            Console.WriteLine(RuntimeInformation.FrameworkDescription);
            Console.WriteLine(assembly);
            Console.WriteLine(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))));
            await ObserveAsync(false);
            await ObserveAsync(true);
        }

        private static async Task ObserveAsync(bool secure)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = new UriBuilder(HttpsSmoke.GetUrl())
            { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url.ToString());
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, url.Port, deadline.Token);
            using var wire = peer.GetStream();
            using var tls = secure ? new SslStream(wire, true, (_, remote, _, errors) =>
                remote != null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                && remote.GetCertHashString() == certificate?.GetCertHashString()) : null;
            if (tls != null)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "localhost", ApplicationProtocols = new() { SslApplicationProtocol.Http11 } }, deadline.Token);
            Stream transport = tls ?? (Stream)wire;
            var accepting = listener.GetContextAsync(deadline.Token);
            await transport.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {url.Authority}\r\n\r\n"), deadline.Token);
            var context = await accepting;
            context.Response.StatusCode = 204;
            context.Response.ContentLength64 = 0;
            context.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
            context.Close();
            var head = new StringBuilder();
            var next = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.That(head.Length, Is.LessThan(8192));
                Assert.That(await transport.ReadAsync(next, deadline.Token), Is.EqualTo(1));
                head.Append((char)next[0]);
            }
            Assert.That(head.ToString(), Does.StartWith("HTTP/1.1 204 "));
            Assert.That(head.ToString(), Does.Contain("timeout=15"));
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                Assert.That(await transport.ReadAsync(next, deadline.Token), Is.Zero);
                Console.WriteLine($"{secure}: peer EOF after {started.Elapsed}");
            }
            catch (IOException error)
            {
                Console.WriteLine($"{secure}: terminal read failed after {started.Elapsed}; {error}");
            }
            Assert.That(started.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(12)), "The idle observation must not pass on premature closure.");
            Assert.That(listener.IsListening, Is.True);
        }
    }
}
