using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Application reads begin only after the bounded upload ends or is rejected.
    // This is a separate schedule control; the original concurrent-reader fixture remains unchanged.
    [NonParallelizable]
    public class Http1RejectedHeadDelayedReaderTest
    {
        [TestCase("target", 17, 414, false)]
        [TestCase("target", 8192, 414, false)]
        [TestCase("headers", 17, 431, false)]
        [TestCase("headers", 8192, 431, false)]
        [TestCase("version", 17, 400, false)]
        [TestCase("version", 8192, 400, false)]
        [TestCase("syntax", 17, 400, false)]
        [TestCase("syntax", 8192, 400, false)]
        [TestCase("target", 17, 414, true)]
        [TestCase("target", 8192, 414, true)]
        [TestCase("headers", 17, 431, true)]
        [TestCase("headers", 8192, 431, true)]
        [TestCase("version", 17, 400, true)]
        [TestCase("version", 8192, 400, true)]
        [TestCase("syntax", 17, 400, true)]
        [TestCase("syntax", 8192, 400, true)]
        public async Task ErrorResponseSurvivesDelayedApplicationRead(string kind, int fragment, int status, bool secure)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            var calls = 0;
            using var server = new WebServer(options => options.WithUrlPrefix(url)
                .WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate)).WithAction("/", HttpVerbs.Any,
                context => { Interlocked.Increment(ref calls); return context.SendStringAsync("healthy", "text/plain", Encoding.UTF8); });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            var clock = Stopwatch.StartNew();
            var address = new Uri(url);
            var endpoint = TcpRegistrationInventory.Find(IPAddress.Loopback, address.Port);
            try
            {
                var request = kind switch
                {
                    "target" => "GET /" + new string('x', 33000) + " HTTP/1.1\r\nHost: localhost\r\n\r\n",
                    "headers" => "GET / HTTP/1.1\r\nHost: localhost\r\nX-Large: " + new string('x', 33000) + "\r\n\r\n",
                    "version" => "GET / HTTP/" + new string('1', 33000) + "\r\nHost: localhost\r\n\r\n",
                    _ => "GET / HTTP/1.1\r\nHost: localhost\r\nX: bad\n" + new string('x', 33000) + "\r\n\r\n",
                };
                var bytes = Encoding.ASCII.GetBytes(request + "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n");
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, address.Port, stop.Token);
                var local = client.Client.LocalEndPoint;
                var remote = client.Client.RemoteEndPoint;
                var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                using var tls = secure ? new SslStream(client.GetStream(), false, (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == pin) : null;
                if (tls != null)
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = new System.Collections.Generic.List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                    }, stop.Token);
                var wire = (Stream?)tls ?? client.GetStream();
                var sent = 0;
                IOException? sendFailure = null;
                IOException? readFailure = null;
                var reads = 0;
                var eof = false;
                TestContext.Out.WriteLine($"REJECTION START pid={Environment.ProcessId} kind={kind} fragment={fragment} tls={secure} local={local} remote={remote} offered={bytes.Length} pending={PendingCount(endpoint)}");
                try
                {
                    for (var offset = 0; offset < bytes.Length; offset += fragment)
                    {
                        var count = Math.Min(fragment, bytes.Length - offset);
                        await wire.WriteAsync(bytes.AsMemory(offset, count), stop.Token);
                        sent += count;
                    }
                }
                catch (IOException error) { sendFailure = error; }
                TestContext.Out.WriteLine($"REJECTION SEND-END ms={clock.Elapsed.TotalMilliseconds:F3} completed={sent} offered={bytes.Length} failure={Describe(sendFailure)} pending={PendingCount(endpoint)}");

                // No application Receive/Read was started before this point.
                using var received = new MemoryStream();
                var buffer = new byte[8192];
                try
                {
                    while (true)
                    {
                        var count = await wire.ReadAsync(buffer.AsMemory(), stop.Token);
                        reads++;
                        if (count == 0) { eof = true; break; }
                        received.Write(buffer, 0, count);
                    }
                }
                catch (IOException error) { readFailure = error; }
                var response = Encoding.ASCII.GetString(received.ToArray());
                var rejectedCalls = Volatile.Read(ref calls);
                TestContext.Out.WriteLine($"REJECTION READ-END ms={clock.Elapsed.TotalMilliseconds:F3} bytes={received.Length} reads={reads} eof={eof} failure={Describe(readFailure)} pipelineCalls={rejectedCalls} pending={PendingCount(endpoint)} wire={response.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}");

                using var healthy = HttpsSmoke.CreateClient(certificate);
                healthy.Timeout = TimeSpan.FromSeconds(15);
                var successor = await healthy.GetStringAsync(url, stop.Token);
                TestContext.Out.WriteLine($"REJECTION SUCCESSOR ms={clock.Elapsed.TotalMilliseconds:F3} body={successor} calls={Volatile.Read(ref calls)} pending={PendingCount(endpoint)}");
                var detail = $"sent={sent}/{bytes.Length}, received={received.Length}, readFailure={Describe(readFailure)}, sendFailure={Describe(sendFailure)}, local={local}, remote={remote}";
                Assert.That(rejectedCalls, Is.Zero, detail);
                Assert.That(successor, Is.EqualTo("healthy"), detail);
                Assert.That(Volatile.Read(ref calls), Is.EqualTo(1), detail);
                Assert.That(response, Does.StartWith("HTTP/1.1 " + status + " "), detail);
                Assert.That(response, Does.Contain("Content-Length: 0\r\nConnection: close\r\n\r\n"), detail);
                Assert.That(response, Does.EndWith("\r\n\r\n"), detail);
                Assert.That(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4, Is.EqualTo(response.Length), detail);
                Assert.That(response.Split("HTTP/1.1 ", StringSplitOptions.None).Length, Is.EqualTo(2), detail);
                if (status != 400) Assert.That(response, Does.Contain("Cache-Control: no-store\r\n"), detail);

            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                TestContext.Out.WriteLine($"REJECTION CLEANUP pid={Environment.ProcessId} ms={clock.Elapsed.TotalMilliseconds:F3} state={server.State}");
            }
        }

        private static int PendingCount(object endpoint)
        {
            var method = endpoint.GetType().GetMethod("PendingConnections", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing pending connection snapshot.");
            return (method.Invoke(endpoint, null) as Array
                ?? throw new AssertionException("Missing pending connection inventory.")).Length;
        }

        private static string Describe(IOException? error)
            => error == null ? "none" : error.InnerException is SocketException socket
                ? $"{error.GetType().Name}/{socket.SocketErrorCode}/{socket.ErrorCode}" : $"{error.GetType().Name}/{error.Message}";
    }
}
