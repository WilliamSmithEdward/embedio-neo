using System;
using System.Net;
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
            var remove = typeof(Net.EndPointManager).GetMethod("RemovePrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
            remove.Invoke(null, new object[] { url, retiring });
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
            Assert.That(error!.InnerException, Is.InstanceOf<HttpListenerException>());
            Assert.That(((HttpListenerException)error.InnerException!).ErrorCode, Is.EqualTo(400));
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

        private sealed class RoutingEndpoint : IDisposable
        {
            private static readonly Type EndpointType = typeof(Net.HttpListener).Assembly
                .GetType("EmbedIO.Net.Internal.EndPointListener")!;
            private static readonly Type PrefixType = typeof(Net.HttpListener).Assembly
                .GetType("EmbedIO.Net.Internal.ListenerPrefix")!;
            private readonly object _endpoint;

            public RoutingEndpoint(Net.HttpListener listener)
            {
                // Routing tests bind an ephemeral socket and invoke lookup directly,
                // without depending on client DNS, firewall rules or certificate trust.
                _endpoint = Activator.CreateInstance(EndpointType, listener, IPAddress.Loopback, 0, false)!;
            }

            public void Add(string prefix, Net.HttpListener listener)
                => EndpointType.GetMethod("AddPrefix")!.Invoke(_endpoint,
                    new[] { Activator.CreateInstance(PrefixType, prefix)!, listener });

            public void Remove(string prefix, Net.HttpListener listener)
                => EndpointType.GetMethod("RemovePrefix")!.Invoke(_endpoint,
                    new[] { Activator.CreateInstance(PrefixType, prefix)!, listener });

            public object? Find(string url)
                => EndpointType.GetMethod("SearchListener", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(_endpoint, new object?[] { new Uri(url), null });

            public void Dispose() => ((IDisposable)_endpoint).Dispose();
        }
    }
}
