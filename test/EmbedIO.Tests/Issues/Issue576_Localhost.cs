using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.PlatformTests;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue576_Localhost
    {
        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task LocalhostRoutesOnBothLoopbacks(bool explicitAddresses, bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            var url = Resources.GetServerAddress();
            var port = new Uri(url).Port;
            var prefixes = explicitAddresses ? new[] { url, $"http://127.0.0.1:{port}/", $"http://[::1]:{port}/" } : new[] { url };
            using var server = new WebServer(o => o.WithUrlPrefixes(prefixes).WithMode(HttpListenerMode.EmbedIO));
            server.WithModule(new ActionModule("/", HttpVerbs.Any, c => c.SendStringAsync("hello", "text/plain", Encoding.UTF8)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = CreateClient(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("hello"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HttpsLocalhostWorksOnBothFamilies(bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = Resources.GetServerAddress().Replace("http:", "https:", StringComparison.Ordinal);
            using var server = HttpsSmoke.CreateServer(url, certificate);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = CreateClient(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, certificate);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("encrypted"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RemovingOneHostDoesNotRemoveAnotherAtTheSamePath(bool removeLocalhost)
        {
            var url = Resources.GetServerAddress();
            var literal = url.Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var named = CreateServer(url, "named");
            using var numeric = CreateServer(literal, "numeric");
            using var stopNamed = new CancellationTokenSource();
            using var stopNumeric = new CancellationTokenSource();
            var namedRun = named.RunAsync(stopNamed.Token);
            var numericRun = numeric.RunAsync(stopNumeric.Token);
            try
            {
                using var client = CreateClient(IPAddress.Loopback);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("named"));
                Assert.That(await client.GetStringAsync(literal), Is.EqualTo("numeric"));
                if (removeLocalhost) { stopNamed.Cancel(); await namedRun.WaitAsync(TimeSpan.FromSeconds(10)); }
                else { stopNumeric.Cancel(); await numericRun.WaitAsync(TimeSpan.FromSeconds(10)); }
                Assert.That(await client.GetStringAsync(removeLocalhost ? literal : url), Is.EqualTo(removeLocalhost ? "numeric" : "named"));
            }
            finally
            {
                stopNamed.Cancel(); stopNumeric.Cancel();
                await Task.WhenAll(namedRun, numericRun).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task UnregisteredHostIsNotAcceptedAsALocalhostAlias(bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            var url = Resources.GetServerAddress();
            using var server = CreateServer(url, "named");
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = CreateClient(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);
                await Assert.ThatAsync(() => client.GetStringAsync(url.Replace("localhost", "unregistered.invalid", StringComparison.Ordinal)), Throws.InstanceOf<HttpRequestException>());
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("named"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public void FailureOnSecondFamilyRollsBackFirstFamily()
        {
            if (!Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            var url = Resources.GetServerAddress();
            var port = new Uri(url).Port;
            using var occupied = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            occupied.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port)); occupied.Listen(1);
            using var listener = new Net.HttpListener();
            listener.AddPrefix(url);
            Assert.Throws<SocketException>(() => listener.Start());
            AssertPortFree(IPAddress.Loopback, port);
            occupied.Dispose();
            listener.Start(); listener.Stop();
            AssertPortFree(IPAddress.Loopback, port);
            AssertPortFree(IPAddress.IPv6Loopback, port);
        }

        [Test]
        public void ConflictingRegistrationLeavesTheOriginalOwnerIntact()
        {
            var url = Resources.GetServerAddress();
            var port = new Uri(url).Port;
            using var owner = new Net.HttpListener(); owner.AddPrefix(url); owner.Start();
            using var other = new Net.HttpListener(); other.AddPrefix(url);
            Assert.Throws<HttpListenerException>(() => other.Start());
            other.Dispose();
            Assert.That(owner.IsListening, Is.True);
            using var blocked = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Assert.Throws<SocketException>(() => blocked.Bind(new IPEndPoint(IPAddress.Loopback, port)));
            owner.Stop();
            AssertPortFree(IPAddress.Loopback, port);
            if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
        }

        [Test]
        public void Ipv4OnlyOptionAndRemovalUseTheActualRegistration()
        {
            var previous = Net.EndPointManager.UseIpv6;
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var listener = new Net.HttpListener(); listener.AddPrefix(url);
            try
            {
                Net.EndPointManager.UseIpv6 = false;
                listener.Start();
                if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
                Net.EndPointManager.UseIpv6 = true;
                listener.Stop();
                AssertPortFree(IPAddress.Loopback, port);
                if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
            }
            finally { Net.EndPointManager.UseIpv6 = previous; }
        }

        [Test]
        public void RestartReleasesBothFamiliesEveryTime()
        {
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var listener = new Net.HttpListener(); listener.AddPrefix(url);
            for (var iteration = 0; iteration < 3; iteration++)
            {
                listener.Start(); listener.Stop();
                AssertPortFree(IPAddress.Loopback, port);
                if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
            }
        }

        [Test]
        public async Task ConcurrentRegistrationCanShareAnEndpointByPath()
        {
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var first = new Net.HttpListener(); first.AddPrefix(url + "first/");
            using var second = new Net.HttpListener(); second.AddPrefix(url + "second/");
            await Task.WhenAll(Task.Run(first.Start), Task.Run(second.Start)).WaitAsync(TimeSpan.FromSeconds(10));
            first.Stop(); second.Stop();
            AssertPortFree(IPAddress.Loopback, port);
            if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LiteralPrefixesStillWorkWhenLocalhostIsRegisteredLast(bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable.");
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var server = new WebServer(o => o.WithUrlPrefixes($"http://127.0.0.1:{port}/", $"http://[::1]:{port}/", url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, c => c.SendStringAsync("hello", "text/plain", Encoding.UTF8)));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            try
            {
                using var client = CreateClient(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback);
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("hello"));
                Assert.That(await client.GetStringAsync(url.Replace("localhost", ipv6 ? "[::1]" : "127.0.0.1", StringComparison.Ordinal)), Is.EqualTo("hello"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public void FailedLivePrefixCanBeRetriedAfterConflictIsRemoved()
        {
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var owner = new Net.HttpListener(); owner.AddPrefix(url); owner.Start();
            using var other = new Net.HttpListener(); other.AddPrefix(url + "other/"); other.Start();
            Assert.Throws<HttpListenerException>(() => other.AddPrefix(url));
            Assert.That(other.Prefixes, Does.Not.Contain(url));
            owner.Stop(); other.AddPrefix(url);
            Assert.That(other.Prefixes, Does.Contain(url));
            other.Stop(); AssertPortFree(IPAddress.Loopback, port);
            if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
        }

        [Test]
        public void FailedMultiPrefixStartRollsBackOnlyItsOwnRegistrations()
        {
            var url = Resources.GetServerAddress(); var port = new Uri(url).Port;
            using var owner = new Net.HttpListener(); owner.AddPrefix(url); owner.Start();
            using var other = new Net.HttpListener(); other.AddPrefix(url + "other/"); other.AddPrefix(url);
            Assert.Throws<HttpListenerException>(() => other.Start());
            owner.Stop(); AssertPortFree(IPAddress.Loopback, port);
            if (Socket.OSSupportsIPv6) AssertPortFree(IPAddress.IPv6Loopback, port);
        }

        [Test]
        public void HttpCannotAttachToAnExistingTlsEndpoint()
        {
            var url = Resources.GetServerAddress();
            using var certificate = HttpsSmoke.CreateCertificate();
            using var secure = new Net.HttpListener(certificate); secure.AddPrefix(url.Replace("http:", "https:", StringComparison.Ordinal)); secure.Start();
            using var clear = new Net.HttpListener(); clear.AddPrefix(url);
            Assert.Throws<HttpListenerException>(() => clear.Start());
            Assert.That(secure.IsListening, Is.True);
        }

        private static WebServer CreateServer(string url, string body)
            => new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, c => c.SendStringAsync(body, "text/plain", Encoding.UTF8)));

        private static void AssertPortFree(IPAddress address, int port)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(address, port)); socket.Listen(1);
        }

        private static HttpClient CreateClient(IPAddress address, X509Certificate2? certificate = null)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            };
            if (certificate != null)
            {
                var expected = certificate.GetCertHashString(HashAlgorithmName.SHA256);
                handler.SslOptions.RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString(HashAlgorithmName.SHA256) == expected;
            }
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }
    }
}
