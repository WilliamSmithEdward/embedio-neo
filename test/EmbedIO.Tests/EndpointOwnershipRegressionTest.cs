using System;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using System.Reflection;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class EndpointOwnershipRegressionTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void StaleAliasRemovalPreservesReplacementEndpoint(bool secure)
        {
            var url = Resources.GetServerAddress();
            if (secure)
                url = url.Replace("http://", "https://", StringComparison.Ordinal);
            using var retiring = new Net.HttpListener();
            retiring.AddPrefix(url);
            retiring.AddPrefix(url.Replace("localhost", "LOCALHOST", StringComparison.Ordinal));
            retiring.Start();

            // Stop removes prefixes one at a time. Model a replacement starting after
            // removal of the first alias, before the old listener removes its second alias.
            var remove = typeof(Net.EndPointManager).GetMethod("RemovePrefix", BindingFlags.Static | BindingFlags.NonPublic);
            (remove ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { url, retiring });
            using var replacement = new Net.HttpListener();
            replacement.AddPrefix(url);
            replacement.Start();
            retiring.Stop();

            using var additional = new Net.HttpListener();
            additional.AddPrefix(url + "another/");
            Assert.DoesNotThrow(additional.Start,
                "A stale endpoint must not remove the replacement from the shared endpoint map.");
            Assert.That(replacement.IsListening, Is.True);
            Assert.That(additional.IsListening, Is.True);
        }

        [TestCase("*")]
        [TestCase("+")]
        public void WildcardRemovalPreservesOtherOwnersAndRejectsDuplicatePaths(string host)
        {
            using var first = new Net.HttpListener();
            using var second = new Net.HttpListener();
            using var endpoint = new RoutingEndpoint(first);
            endpoint.Add("http://localhost:9999/keep/", first);
            var prefix = $"http://{host}:9999/shared/";
            endpoint.Add(prefix, first);

            var error = Assert.Throws<TargetInvocationException>(() => endpoint.Add(prefix, second));
            Assert.That(error.InnerException, Is.InstanceOf<HttpListenerException>());
            Assert.That(((HttpListenerException)error.InnerException).ErrorCode, Is.EqualTo(400));
            endpoint.Remove(prefix, second);
            Assert.That(endpoint.Find("http://example.test:9999/shared/file"), Is.SameAs(first));
            endpoint.Remove(prefix, first);
            Assert.That(endpoint.Find("http://example.test:9999/shared/file"), Is.Null);
        }

        [Test]
        public void RouteSelectionPreservesNamedWildcardAndLongestPathPrecedence()
        {
            using var named = new Net.HttpListener();
            using var star = new Net.HttpListener();
            using var plus = new Net.HttpListener();
            using var deeper = new Net.HttpListener();
            using var endpoint = new RoutingEndpoint(named);
            endpoint.Add("http://localhost:9999/named/", named);
            endpoint.Add("http://*:9999/", star);
            endpoint.Add("http://+:9999/named/", plus);
            endpoint.Add("http://*:9999/deeper/", deeper);
            endpoint.Add("http://+:9999/", plus);

            Assert.That(endpoint.Find("http://localhost:9999/named/file"), Is.SameAs(named));
            Assert.That(endpoint.Find("http://localhost:9999/named"), Is.SameAs(named));
            Assert.That(endpoint.Find("http://example.test:9999/named/file"), Is.SameAs(star));
            Assert.That(endpoint.Find("http://example.test:9999/deeper/file"), Is.SameAs(deeper));
            // Wildcard paths only try the added slash when the original path has no match.
            Assert.That(endpoint.Find("http://example.test:9999/deeper"), Is.SameAs(star));
            Assert.That(endpoint.Find("http://example.test:9999/deep%65r/file"), Is.SameAs(deeper));
            endpoint.Remove("http://*:9999/", star);
            Assert.That(endpoint.Find("http://example.test:9999/deeper"), Is.SameAs(deeper));
            endpoint.Remove("http://*:9999/deeper/", deeper);
            Assert.That(endpoint.Find("http://example.test:9999/named/file"), Is.SameAs(plus));
            Assert.That(endpoint.Find("http://example.test:9999/another/file"), Is.SameAs(plus));
        }

        private static System.Collections.Generic.IEnumerable<TestCaseData> NamedPathCases()
        {
            yield return new TestCaseData(new Uri("http://localhost:9999/case/file"), true);
            yield return new TestCaseData(new Uri("http://LOCALHOST:9999/case/file"), true);
            yield return new TestCaseData(new Uri("http://localhost:9999/Case/file"), false);
            yield return new TestCaseData(new Uri("http://localhost:9999/case"), true);
            yield return new TestCaseData(new Uri("http://localhost:9999/cases"), false);
            yield return new TestCaseData(new Uri("http://localhost:9999/%63ase/file"), true);
            yield return new TestCaseData(new Uri("http://localhost:9999/case%2Ffile"), true);
            yield return new TestCaseData(new Uri("http://localhost:9999/case/file?ignored=/other/"), true);
            yield return new TestCaseData(new Uri("http://localhost:9998/case/file"), false);
            yield return new TestCaseData(new Uri("http://other.test:9999/case/file"), false);
        }

        [TestCaseSource(nameof(NamedPathCases))]
        public void NamedPathsPreserveDecodingCaseBoundaryAndAuthority(Uri url, bool matches)
        {
            using var owner = new Net.HttpListener();
            using var endpoint = new RoutingEndpoint(owner);
            endpoint.Add("http://localhost:9999/case/", owner);
            Assert.That(endpoint.Find(url), matches ? Is.SameAs(owner) : Is.Null);
        }

        [TestCase("localhost")]
        [TestCase("*")]
        [TestCase("+")]
        public async Task ConcurrentRoutePublicationAndRemovalPreserveEveryOwner(string host)
        {
            using var anchor = new Net.HttpListener();
            using var first = new Net.HttpListener();
            using var second = new Net.HttpListener();
            using var endpoint = new RoutingEndpoint(anchor);
            endpoint.Add("http://localhost:9999/anchor/", anchor);
            var owners = new[] { first, second };
            // Keep one stable named route while independent writers publish/remove
            // disjoint registrations. Readers must not see missing or partial entries.
            var writing = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 32; index++)
                {
                    var owner = owners[worker % 2];
                    var registration = $"http://{host}:9999/worker{worker}/item{index}/";
                    endpoint.Add(registration, owner);
                    Assert.That(endpoint.Find($"http://localhost:9999/worker{worker}/item{index}/file"), Is.SameAs(owner));
                    Assert.That(endpoint.Find("http://localhost:9999/anchor/file"), Is.SameAs(anchor));
                }
            })).ToArray();
            await Task.WhenAll(writing).WaitAsync(TimeSpan.FromSeconds(10));
            for (var worker = 0; worker < 8; worker++)
                for (var index = 0; index < 32; index++)
                    Assert.That(endpoint.Find($"http://localhost:9999/worker{worker}/item{index}/file"), Is.SameAs(owners[worker % 2]));
            var removing = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var index = 0; index < 32; index++)
                {
                    var owner = owners[worker % 2];
                    var registration = $"http://{host}:9999/worker{worker}/item{index}/";
                    // A stale/wrong owner cannot remove another registration.
                    endpoint.Remove(registration, owners[1 - worker % 2]);
                    Assert.That(endpoint.Find($"http://localhost:9999/worker{worker}/item{index}/file"), Is.SameAs(owner));
                    endpoint.Remove(registration, owner);
                    Assert.That(endpoint.Find($"http://localhost:9999/worker{worker}/item{index}/file"), Is.Null);
                    Assert.That(endpoint.Find("http://localhost:9999/anchor/file"), Is.SameAs(anchor));
                }
            })).ToArray();
            await Task.WhenAll(removing).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(endpoint.Find("http://localhost:9999/anchor/file"), Is.SameAs(anchor));
        }

        private sealed class RoutingEndpoint : IDisposable
        {
            private static readonly Type EndpointType = (typeof(Net.HttpListener).Assembly
                .GetType("EmbedIO.Net.Internal.EndPointListener") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            private static readonly Type PrefixType = (typeof(Net.HttpListener).Assembly
                .GetType("EmbedIO.Net.Internal.ListenerPrefix") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            private readonly object _endpoint;

            public RoutingEndpoint(Net.HttpListener listener)
            {
                // Routing tests bind an ephemeral socket and invoke lookup directly,
                // without depending on client DNS, firewall rules or certificate trust.
                _endpoint = Activator.CreateInstance(EndpointType, listener, IPAddress.Loopback, 0, false) ?? throw new AssertionException("The endpoint was not created.");
            }

            public void Add(string prefix, Net.HttpListener listener)
                => ((EndpointType).GetMethod("AddPrefix") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(_endpoint,
                    new[] { Activator.CreateInstance(PrefixType, prefix), listener });

            public void Remove(string prefix, Net.HttpListener listener)
                => ((EndpointType).GetMethod("RemovePrefix") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(_endpoint,
                    new[] { Activator.CreateInstance(PrefixType, prefix), listener });

            public object? Find(string url) => Find(new Uri(url));

            public object? Find(Uri url)
                => ((EndpointType).GetMethod("SearchListener", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))
                    .Invoke(_endpoint, new object?[] { url, null });

            public void Dispose() => ((IDisposable)_endpoint).Dispose();
        }
    }
}
