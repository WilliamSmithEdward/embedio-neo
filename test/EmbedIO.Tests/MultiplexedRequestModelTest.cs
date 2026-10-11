using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The HTTP/2 and HTTP/3 request model creates an empty query collection, items
    // and close-callback storage on first use, and parses a present query without
    // splitting it into parts. These cases pin the values, identity and mutability
    // those members had when they were built eagerly, through the public WebServer
    // on both protocols.
    public class MultiplexedRequestModelTest
    {
        public enum Transport
        {
            Http2,
            Http3,
        }

        private static readonly Transport[] Transports = { Transport.Http2, Transport.Http3 };

        [TestCaseSource(nameof(QueryCases))]
        public async Task QueryStringMatchesSplitParsing(Transport transport, string target)
        {
            var observed = new TaskCompletionSource<(List<KeyValuePair<string?, string[]?>> Actual, List<KeyValuePair<string?, string[]?>> Expected)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await RunAsync(transport, async context =>
            {
                observed.TrySetResult((Pairs(context.Request.QueryString), Pairs(SplitParse(context.Request.Url.Query))));
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }, async (client, prefix) => Assert.That(await client.GetStringAsync(prefix + target.TrimStart('/')), Is.EqualTo("ok")));
            var (actual, expected) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(actual, Is.EqualTo(expected));
            if (target == "/q?x=a+b&x=c") Assert.That(actual, Is.EqualTo(new[] { new KeyValuePair<string?, string[]?>("x", new[] { "a b", "c" }) }));
            if (target == "/q?a=1&&b=2") Assert.That(actual.Select(pair => pair.Key), Is.EqualTo(new[] { "a", null, "b" }));
        }

        private static IEnumerable<TestCaseData> QueryCases()
            => from transport in Transports
               from target in new[]
               {
                   "/q", "/q?x=a+b&x=c", "/q?a=1&&b=2", "/q?flag&k=", "/q?=v&=w", "/q?a=%3D%26&b=c=d",
                   "/q?a=1&", "/q?A=1&a=2", "/q?%E4%B8%96=%E7%95%8C",
               }
               select new TestCaseData(transport, target);

        [TestCaseSource(nameof(TransportsWithQuery))]
        public async Task ConcurrentFirstAccessPublishesStableMutableRequestLocalMembers(Transport transport, bool withQuery)
        {
            var contexts = new ConcurrentQueue<IHttpContext>();
            await RunAsync(transport, async context =>
            {
                var access = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
                    () => (Items: context.Items, Query: context.Request.QueryString, Referrer: context.Request.UrlReferrer))));
                foreach (var entry in access)
                {
                    Assert.That(entry.Items, Is.SameAs(access[0].Items));
                    Assert.That(entry.Query, Is.SameAs(access[0].Query));
                    Assert.That(entry.Referrer, Is.SameAs(access[0].Referrer));
                }
                Assert.That(context.Items, Is.Empty);
                Assert.That(context.Request.QueryString.GetValues("x"), withQuery ? Is.EqualTo(new[] { "a b", "c" }) : Is.Null);
                Assert.That(context.Request.UrlReferrer, Is.EqualTo(new Uri("https://origin.test/page?from=1")));
                var ordinal = contexts.Count;
                context.Items["ordinal"] = ordinal;
                context.Request.QueryString.Add("added", ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                contexts.Enqueue(context);
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }, async (client, prefix) =>
            {
                for (var request = 0; request < 2; request++)
                {
                    using var message = Message(client, prefix + (withQuery ? "?x=a+b&x=c" : string.Empty));
                    message.Headers.Referrer = new Uri("https://origin.test/page?from=1");
                    using var response = await client.SendAsync(message);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                }
            });
            var served = contexts.ToArray();
            Assert.That(served, Has.Length.EqualTo(2));
            Assert.That(served[0].Items, Is.Not.SameAs(served[1].Items));
            Assert.That(served[0].Request.QueryString, Is.Not.SameAs(served[1].Request.QueryString));
            Assert.That(served[0].Request.UrlReferrer, Is.Not.SameAs(served[1].Request.UrlReferrer));
            Assert.That(served[0].Items["ordinal"], Is.EqualTo(0));
            Assert.That(served[1].Items["ordinal"], Is.EqualTo(1));
            served[0].Request.QueryString["added"] = "retained";
            Assert.That(served[0].Request.QueryString["added"], Is.EqualTo("retained"));
            Assert.That(served[1].Request.QueryString["added"], Is.EqualTo("1"));
        }

        private static IEnumerable<TestCaseData> TransportsWithQuery()
            => from transport in Transports from withQuery in new[] { false, true } select new TestCaseData(transport, withQuery);

        [TestCaseSource(nameof(ReferrerCases))]
        public async Task ReferrerKeepsTheHeaderValueAtRequestStart(Transport transport, string? referrer, string? expected)
        {
            var observed = new TaskCompletionSource<(Uri? First, Uri? Second)>(TaskCreationOptions.RunContinuationsAsynchronously);
            await RunAsync(transport, async context =>
            {
                // A later header change does not alter the referrer, as when it was parsed at construction.
                context.Request.Headers[HttpHeaderNames.Referer] = "https://changed.test/";
                observed.TrySetResult((context.Request.UrlReferrer, context.Request.UrlReferrer));
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }, async (client, prefix) =>
            {
                using var message = Message(client, prefix);
                if (referrer != null) Assert.That(message.Headers.TryAddWithoutValidation("Referer", referrer), Is.True);
                using var response = await client.SendAsync(message);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            });
            var (first, second) = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(first?.OriginalString, Is.EqualTo(expected));
            if (referrer == "/relative/page" && !OperatingSystem.IsWindows())
                Assert.That(first?.Scheme, Is.EqualTo(Uri.UriSchemeFile), "Preserve the existing Unix absolute-file URI interpretation.");
            Assert.That(second, Is.SameAs(first));
        }

        private static IEnumerable<TestCaseData> ReferrerCases()
            => from transport in Transports
               from pair in new (string? Referrer, string? Expected)[]
               {
                   ("https://origin.test/page", "https://origin.test/page"),
                   ("/relative/page", OperatingSystem.IsWindows() ? null : "/relative/page"),
                   ("not a uri", null),
                   (null, null),
               }
               select new TestCaseData(transport, pair.Referrer, pair.Expected);

        [TestCaseSource(nameof(Transports))]
        public async Task IdsKeepTheirFormatAndAgeAdvances(Transport transport)
        {
            var ids = new ConcurrentBag<string>();
            var ages = new ConcurrentBag<(long Before, long After)>();
            await RunAsync(transport, async context =>
            {
                ids.Add(context.Id);
                var before = context.Age;
                await Task.Delay(60);
                ages.Add((before, context.Age));
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }, async (client, prefix) =>
            {
                var responses = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => client.GetStringAsync(prefix)));
                Assert.That(responses, Is.All.EqualTo("ok"));
            });
            Assert.That(ids, Has.Count.EqualTo(24));
            Assert.That(ids.Distinct().Count(), Is.EqualTo(24));
            foreach (var id in ids) Assert.That(id, Does.Match("^[A-Za-z0-9+/]{22}$"));
            foreach (var (before, after) in ages)
            {
                Assert.That(before, Is.GreaterThanOrEqualTo(0));
                Assert.That(after - before, Is.InRange(50, 10_000));
            }
        }

        [TestCaseSource(nameof(Transports))]
        public async Task ConcurrentCloseRegistrationsRunOnceInReverseOrderAndLateOnesAreRejected(Transport transport)
        {
            var calls = new ConcurrentQueue<int>();
            var closed = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var plain = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            await RunAsync(transport, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/plain")
                {
                    // No registration: closing must not need callback storage.
                    plain.TrySetResult(context);
                    await context.SendStringAsync("plain", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }
                context.OnClose(closing => closed.TrySetResult(closing));
                await Task.WhenAll(Enumerable.Range(1, 16).Select(index => Task.Run(() => context.OnClose(_ => calls.Enqueue(index)))));
                await context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
            }, async (client, prefix) =>
            {
                Assert.That(await client.GetStringAsync(prefix + "plain"), Is.EqualTo("plain"));
                Assert.That(await client.GetStringAsync(prefix + "callbacks"), Is.EqualTo("ok"));
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });
            var context = await closed.Task;
            Assert.That(calls.OrderBy(index => index), Is.EqualTo(Enumerable.Range(1, 16)));
            Assert.That(() => context.OnClose(_ => { }), Throws.InvalidOperationException);
            var unregistered = await plain.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(unregistered.Request.QueryString, Is.Empty);
        }

        // The query rules the request model used when it parsed at construction.
        private static NameValueCollection SplitParse(string query)
        {
            var result = new NameValueCollection();
            if (query.Length == 0) return result;
            foreach (var part in query.Substring(1).Split('&'))
            {
                var equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0) result.Add(null, WebUtility.UrlDecode(part));
                else result.Add(WebUtility.UrlDecode(part.Substring(0, equals)), WebUtility.UrlDecode(part.Substring(equals + 1)));
            }
            return result;
        }

        // DefaultRequestVersion applies only to the convenience methods.
        private static HttpRequestMessage Message(HttpClient client, string url)
            => new(HttpMethod.Get, url) { Version = client.DefaultRequestVersion, VersionPolicy = HttpVersionPolicy.RequestVersionExact };

        private static List<KeyValuePair<string?, string[]?>> Pairs(NameValueCollection collection)
            => collection.AllKeys.Select(key => new KeyValuePair<string?, string[]?>(key, collection.GetValues(key))).ToList();

        private static async Task RunAsync(Transport transport, RequestHandlerCallback handler, Func<HttpClient, string, Task> verify)
        {
            X509Certificate2? certificate = null;
            string prefix;
            if (transport == Transport.Http3)
            {
                var supported = QuicListener.IsSupported && QuicConnection.IsSupported
                    && typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") != null;
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") Assert.That(supported, Is.True);
                if (!supported) Assert.Ignore("The selected asset or host has no HTTP/3 transport.");
                certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                prefix = $"https://localhost:{((IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port}/";
            }
            else prefix = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);

            using (certificate)
            {
                var server = transport == Transport.Http3
                    ? new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithoutAutoLoadCertificate().WithoutAutoRegisterCertificate().WithCertificate(certificate))
                    : new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO));
                using (server)
                {
                    server.WithAction("/", HttpVerbs.Get, handler);
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var running = server.RunAsync(stop.Token);
                    var expectedThumbprint = certificate?.GetCertHashString();
                    using var client = new HttpClient(new SocketsHttpHandler
                    {
                        UseProxy = false,
                        UseCookies = false,
                        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == expectedThumbprint },
                    })
                    {
                        DefaultRequestVersion = transport == Transport.Http3 ? HttpVersion.Version30 : HttpVersion.Version20,
                        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Timeout = TimeSpan.FromSeconds(15),
                    };
                    try { await verify(client, prefix); }
                    finally
                    {
                        stop.Cancel();
                        await running.WaitAsync(TimeSpan.FromSeconds(10));
                    }
                }
            }
        }
    }
}
