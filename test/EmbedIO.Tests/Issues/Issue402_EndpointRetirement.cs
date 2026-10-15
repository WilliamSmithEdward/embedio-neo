using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue402_EndpointRetirement
    {
        [TestCase(false, "stop")]
        [TestCase(true, "stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "drain")]
        [TestCase(true, "drain")]
        public async Task FinalOwnerRetirementReleasesBindingAndCreatesAFreshReplacement(bool secure, string reason)
            => await RetireAndReplace(secure, reason, false);

        [TestCase(false, "stop")]
        [TestCase(true, "stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "drain")]
        [TestCase(true, "drain")]
        public async Task UnusablePhysicalListeningSocketIsNotReusedAfterLogicalRetirement(bool secure, string reason)
            => await RetireAndReplace(secure, reason, true);

        private static async Task RetireAndReplace(bool secure, string reason, bool breakSocket)
        {
            ArgumentNullException.ThrowIfNull(reason);
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = Address(secure);
            using var old = new Net.HttpListener(certificate);
            var prefix = url + "app/";
            old.AddPrefix(prefix);
            old.Start();
            var retired = TcpRegistrationInventory.Owned(old).Single();
            var socket = ListeningSocket(retired);
            await Serve(old, new Uri(prefix), certificate, "old");
            if (breakSocket) socket.Dispose();
            await Retire(old, reason);
            Assert.That(TcpRegistrationInventory.Owned(old), Is.Empty);
            Assert.That(Find(IPAddress.Loopback, url.Port), Is.Null);
            Assert.That(socket.SafeHandle.IsClosed, Is.True);
            AssertPortFree(IPAddress.Loopback, url.Port);

            using var replacement = new Net.HttpListener(certificate);
            // A newly allocated but equal prefix must belong to a fresh endpoint.
            replacement.AddPrefix(new string(prefix.ToCharArray()));
            replacement.Start();
            Assert.That(TcpRegistrationInventory.Owned(replacement).Single(), Is.Not.SameAs(retired));
            await Serve(replacement, new Uri(prefix), certificate, "replacement");
            old.Dispose();
            Assert.That(Find(IPAddress.Loopback, url.Port), Is.SameAs(TcpRegistrationInventory.Owned(replacement).Single()));
            await Serve(replacement, new Uri(prefix), certificate, "after-stale-dispose");
        }

        [TestCase(false, "stop")]
        [TestCase(true, "stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "drain")]
        [TestCase(true, "drain")]
        public async Task RetiringOneOwnerPreservesOtherRoutesUntilTheLastOwnerLeaves(bool secure, string reason)
        {
            ArgumentNullException.ThrowIfNull(reason);
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = Address(secure);
            using var first = new Net.HttpListener(certificate);
            using var survivor = new Net.HttpListener(certificate);
            first.AddPrefix(url + "first/");
            survivor.AddPrefix(url + "survivor/");
            first.Start();
            survivor.Start();
            var endpoint = TcpRegistrationInventory.Owned(first).Single();
            Assert.That(TcpRegistrationInventory.Owned(survivor).Single(), Is.SameAs(endpoint));
            await Retire(first, reason);
            Assert.That(TcpRegistrationInventory.Owned(first), Is.Empty);
            Assert.That(Find(IPAddress.Loopback, url.Port), Is.SameAs(endpoint));
            Assert.That(ListeningSocket(endpoint).SafeHandle.IsClosed, Is.False);
            await Serve(survivor, new Uri(url + "survivor/"), certificate, "survivor");
            using (var replacement = new Net.HttpListener(certificate))
            {
                replacement.AddPrefix(new string((url + "first/").ToCharArray()));
                replacement.Start();
                await Serve(replacement, new Uri(url + "first/"), certificate, "new-owner");
            }
            await Serve(survivor, new Uri(url + "survivor/"), certificate, "still-live");
            survivor.Stop();
            Assert.That(Find(IPAddress.Loopback, url.Port), Is.Null);
            Assert.That(ListeningSocket(endpoint).SafeHandle.IsClosed, Is.True);
            AssertPortFree(IPAddress.Loopback, url.Port);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RemovalUsesRegisteredAddressesAfterAddressFamilySettingChanges(bool secure)
        {
            var previous = Net.EndPointManager.UseIpv6;
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = new UriBuilder(Address(secure)) { Host = "localhost" }.Uri;
            using var listener = new Net.HttpListener(certificate);
            try
            {
                Net.EndPointManager.UseIpv6 = true;
                listener.AddPrefix(url.ToString());
                listener.AddPrefix(url.ToString().Replace("localhost", "LOCALHOST", StringComparison.Ordinal));
                listener.Start();
                var endpoints = TcpRegistrationInventory.Owned(listener);
                Assert.That(endpoints.Length, Is.EqualTo(Socket.OSSupportsIPv6 ? 2 : 1));
                await Serve(listener, url, certificate, "both-aliases");
                Net.EndPointManager.UseIpv6 = false;
                listener.Stop();
                Assert.That(TcpRegistrationInventory.Owned(listener), Is.Empty);
                foreach (var endpoint in endpoints) Assert.That(ListeningSocket(endpoint).SafeHandle.IsClosed, Is.True);
                Assert.That(Find(IPAddress.Loopback, url.Port), Is.Null);
                AssertPortFree(IPAddress.Loopback, url.Port);
                if (Socket.OSSupportsIPv6)
                {
                    Assert.That(Find(IPAddress.IPv6Loopback, url.Port), Is.Null);
                    AssertPortFree(IPAddress.IPv6Loopback, url.Port);
                }
            }
            finally { Net.EndPointManager.UseIpv6 = previous; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AwaitedServerCancellationAllowsFreshServerOnTheSamePrefix(bool secure)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = Address(secure);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = Client(certificate);
            async Task RunOne(string body)
            {
                var options = new WebServerOptions().WithUrlPrefix(url.ToString()).WithMode(HttpListenerMode.EmbedIO);
                if (certificate != null) options.WithCertificate(certificate);
                using var server = new WebServer(options).OnGet("/", c => c.SendStringAsync(body, "text/plain", Encoding.UTF8));
                using var stop = new CancellationTokenSource();
                var running = server.RunAsync(stop.Token);
                try { Assert.That(await client.GetStringAsync(url, deadline.Token), Is.EqualTo(body)); }
                finally { stop.Cancel(); await running.WaitAsync(deadline.Token); }
                Assert.That(Find(IPAddress.Loopback, url.Port), Is.Null);
            }
            await RunOne("old-server");
            await RunOne("fresh-server");
        }

        private static Uri Address(bool secure)
            => new UriBuilder(HttpsSmoke.GetUrl()) { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;

        private static Socket ListeningSocket(object endpoint)
            => endpoint.GetType().GetProperty("ListeningSocket", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint) as Socket
                ?? throw new AssertionException("Missing listening-socket inventory.");

        private static object? Find(IPAddress address, int port)
            => (typeof(Net.EndPointManager).GetMethod("FindEndpoint", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing endpoint lookup.")).Invoke(null, new object[] { address, port });

        private static void AssertPortFree(IPAddress address, int port)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(address, port));
            socket.Listen(1);
        }

        private static Task Retire(Net.HttpListener listener, string reason)
        {
            if (reason == "drain")
            {
                var contract = listener.GetType().GetInterface("EmbedIO.Net.Internal.IGracefulHttpListener")
                    ?? throw new AssertionException("Missing graceful-listener contract.");
                return contract.GetMethod("DrainAsync")?.Invoke(listener, new object[] { TimeSpan.FromSeconds(2), CancellationToken.None }) as Task
                    ?? throw new AssertionException("Missing drain task.");
            }
            if (reason == "dispose") listener.Dispose();
            else listener.Stop();
            return Task.CompletedTask;
        }

        private static HttpClient Client(X509Certificate2? certificate)
        {
            var handler = new HttpClientHandler { UseProxy = false };
            if (certificate != null) handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(certificate.RawData);
            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.ConnectionClose = true;
            return client;
        }

        private static async Task Serve(Net.HttpListener listener, Uri url, X509Certificate2? certificate, string body)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var client = Client(certificate);
            var accepting = listener.GetContextAsync(deadline.Token);
            var receiving = client.GetStringAsync(url, deadline.Token);
            if (await Task.WhenAny(accepting, receiving) == receiving) await receiving;
            var context = await accepting;
            try { await context.SendStringAsync(body, "text/plain", Encoding.UTF8); }
            finally { context.Close(); }
            Assert.That(await receiving, Is.EqualTo(body));
        }
    }
}
