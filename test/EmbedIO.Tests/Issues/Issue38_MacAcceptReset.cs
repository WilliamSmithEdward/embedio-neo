using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue38_MacAcceptReset
    {
        [TestCase(false, false, false, false)]
        [TestCase(false, false, true, false)]
        [TestCase(false, true, false, false)]
        [TestCase(false, true, true, false)]
        [TestCase(true, false, false, false)]
        [TestCase(true, false, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(true, true, true, false)]
        [TestCase(false, true, false, true)]
        [TestCase(false, true, true, true)]
        [TestCase(true, true, false, true)]
        [TestCase(true, true, true, true)]
        public async Task ImmediateResetsKeepFreshHttpAndHttpsConnectionsAvailable(bool secure, bool ipv6, bool partialRequest, bool dualStack)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = GetUrl(secure, ipv6);
            using var server = HttpsSmoke.CreateServer(dualStack ? url.Replace("[::1]", "+", StringComparison.Ordinal) : url, certificate);
            if (dualStack)
                url = url.Replace("[::1]", "127.0.0.1", StringComparison.Ordinal);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = HttpsSmoke.CreateClient(certificate);
            // Every healthy probe must exercise another accept, rather than pooled keep-alive.
            client.DefaultRequestHeaders.ConnectionClose = true;
            var address = new Uri(url);
            try
            {
                for (var batch = 0; batch < 20; batch++)
                {
                    await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
                    {
                        using var socket = new TcpClient(ipv6 && !dualStack ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
                        await socket.ConnectAsync(ipv6 && !dualStack ? IPAddress.IPv6Loopback : IPAddress.Loopback, address.Port);
                        if (partialRequest)
                            await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost:"));
                        // Deliberately do NOT wait for a server accept or parser progress.
                        socket.Client.LingerState = new LingerOption(true, 0);
                        socket.Close();
                    }));
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo(secure ? "encrypted" : "plaintext"));
                }

                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task IdleAcceptWorkerCompletesWhenEndpointStops(bool ipv6, bool dispose)
        {
            var url = GetUrl(false, ipv6);
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url);
            listener.Start();
            // Observe the internal worker so shutdown cannot pass while leaking a thread.
            var manager = typeof(Net.EndPointManager);
            var endpoints = (IDictionary)((((manager).GetField("IPToEndpoints", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            var ports = (IDictionary)((endpoints)[ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback] ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            var endpoint = (ports)[new Uri(url).Port];
            var worker = (Task?)((endpoint ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetType().GetField("_acceptWorker", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(endpoint);
            Assert.That(worker != null, Is.EqualTo(ipv6 && RuntimeInformation.IsOSPlatform(OSPlatform.OSX)));
            if (dispose)
                listener.Dispose();
            else
                listener.Stop();
            if (worker != null)
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(listener.IsListening, Is.False);
        }

        private static string GetUrl(bool secure, bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6)
                Assert.Ignore("IPv6 is unavailable on this host.");
            using var port = new TcpListener(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0);
            port.Start();
            return $"{(secure ? "https" : "http")}://{(ipv6 ? "[::1]" : "127.0.0.1")}:{((IPEndPoint)port.LocalEndpoint).Port}/";
        }
    }
}
