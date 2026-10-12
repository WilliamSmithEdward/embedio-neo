using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // Neo #136, fourth increment: per-route policies that replace the server-wide body
    // limit, body read deadline and request deadline under a base route.
    public partial class Issue136_RequestLimits
    {
        [TestCase(1)]
        [TestCase(2)]
        public async Task RoutePolicyLiftsTheServerBodyLimit(int protocol)
        {
            await using var host = Host.Start(o => o.WithMaximumRequestBodyBytes(100)
                .WithRouteRequestLimits("/upload", p => p.MaximumRequestBodyBytes = long.MaxValue), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            Assert.That(await PostLengthAsync(client, host.Url + "upload", 5000), Is.EqualTo("5000"));
            Assert.That(await PostLengthAsync(client, host.Url + "upload/nested", 5000), Is.EqualTo("5000"), "Descendants of the base route match.");
            if (protocol == 2)
            {
                using var refused = await PostAsync(client, host.Url + "other", 5000);
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge), "Other routes keep the server limit.");
                return;
            }
            // An HTTP/1 client still uploading sees the refusal's close as a reset; check the wire.
            using var socket = await host.ConnectAsync();
            await socket.SendAsync($"POST /other HTTP/1.1\r\nHost: {host.Authority}\r\nContent-Length: 5000\r\n\r\n");
            Assert.That(await socket.ReadToEndAsync(), Does.StartWith("HTTP/1.1 413"), "Other routes keep the server limit.");
        }

        [TestCase(1)]
        [TestCase(2)]
        public async Task RoutePolicyLowersTheBodyLimitWhereTheServerHasNone(int protocol)
        {
            await using var host = Host.Start(o => o.WithRouteRequestLimits("/api", p => p.MaximumRequestBodyBytes = 10), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(protocol);
            using (var refused = await PostAsync(client, host.Url + "api/items", 100))
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(await PostLengthAsync(client, host.Url + "files", 100), Is.EqualTo("100"));
        }

        [Test]
        public async Task FirstMatchingRoutePolicyApplies()
        {
            await using var host = Host.Start(o => o
                .WithRouteRequestLimits("/api/files", p => p.MaximumRequestBodyBytes = 1000)
                .WithRouteRequestLimits("/api", p => p.MaximumRequestBodyBytes = 10), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            Assert.That(await PostLengthAsync(client, host.Url + "api/files/a", 100), Is.EqualTo("100"));
            using var refused = await PostAsync(client, host.Url + "api/items", 100);
            Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        }

        [Test]
        public async Task ParameterizedBaseRouteMatches()
        {
            await using var host = Host.Start(o => o.WithRouteRequestLimits("/tenants/{id}", p => p.MaximumRequestBodyBytes = 10), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(1);
            using (var refused = await PostAsync(client, host.Url + "tenants/42/upload", 100))
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(await PostLengthAsync(client, host.Url + "tenants", 100), Is.EqualTo("100"), "The parameter segment is required.");
        }

        [Test]
        public async Task RoutePolicyReplacesTheRequestDeadline()
        {
            await using var host = Host.Start(o => o.WithRequestTimeout(TimeSpan.FromMilliseconds(300))
                .WithRouteRequestLimits("/reports", p => p.RequestTimeout = Timeout.InfiniteTimeSpan)
                .WithRouteRequestLimits("/quick", p => p.RequestTimeout = TimeSpan.FromMilliseconds(100)),
                async context =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(context.Request.Url.AbsolutePath.StartsWith("/quick", StringComparison.Ordinal) ? 5000 : 800), context.CancellationToken);
                    await context.SendStringAsync("done", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var client = Host.CreateClient(1);
            Assert.That(await client.GetStringAsync(host.Url + "reports"), Is.EqualTo("done"), "The lifted deadline lets a long request finish.");
            using (var server = await client.GetAsync(host.Url + "other"))
                Assert.That(server.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable), "Other routes keep the server deadline.");
            using var quick = await client.GetAsync(host.Url + "quick");
            Assert.That(quick.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        }

        [Test]
        public async Task RoutePolicyReplacesTheBodyReadDeadline()
        {
            await using var host = Host.Start(o => o.WithRequestBodyReadTimeout(Short)
                .WithRouteRequestLimits("/stream", p => p.RequestBodyReadTimeout = Timeout.InfiniteTimeSpan), Host.EchoLength, HttpVerbs.Any);
            using var client = Host.CreateClient(2);
            using (var lifted = await client.SendAsync(Post(client, host.Url + "stream", new TrickleContent(3, TimeSpan.FromMilliseconds(900)))))
                Assert.That(await lifted.Content.ReadAsStringAsync(), Is.EqualTo("30"));
            using var timed = await client.SendAsync(Post(client, host.Url + "other", new TrickleContent(3, TimeSpan.FromMilliseconds(900))));
            Assert.That(timed.StatusCode, Is.EqualTo(HttpStatusCode.RequestTimeout));
        }

        [Test]
        public async Task RouteBodyLimitAlsoDefersContinue()
        {
            await using var host = await MultiHost.StartAsync(2, o => o.WithRouteRequestLimits("/", p => p.MaximumRequestBodyBytes = 100), Host.EchoLength, HttpVerbs.Any);
            var content = new TrackingContent(1000);
            using var response = await host.SendAsync(HttpMethod.Post, content, request => request.Headers.ExpectContinue = true);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(content.Sent, Is.False);
        }

        [Test]
        public void RoutePolicyValidationOrderAndLocking()
        {
            var options = new WebServerOptions().WithUrlPrefix(Resources.GetServerAddress());
            Assert.That(options.RouteRequestLimits, Is.Empty);
            Assert.That(() => options.AddRouteRequestLimits("no-slash", _ => { }), Throws.ArgumentException);
            Assert.That(() => InvalidInput.Invoke((Func<string, Action<RequestLimitPolicy>, RequestLimitPolicy>)options.AddRouteRequestLimits, "/a", null), Throws.ArgumentNullException);
            Assert.That(() => options.AddRouteRequestLimits("/a", p => p.MaximumRequestBodyBytes = -1), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => options.AddRouteRequestLimits("/a", p => p.RequestTimeout = TimeSpan.Zero), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(options.RouteRequestLimits, Is.Empty, "A policy whose configuration failed is not added.");

            var first = options.AddRouteRequestLimits("/b", p => p.MaximumRequestBodyBytes = 5);
            options.WithRouteRequestLimits("/a", p => p.RequestBodyReadTimeout = Timeout.InfiniteTimeSpan);
            Assert.That(() => options.AddRouteRequestLimits("/b", _ => { }), Throws.ArgumentException, "One policy per base route.");
            Assert.That(options.RouteRequestLimits, Has.Count.EqualTo(2));
            Assert.That(options.RouteRequestLimits[0], Is.SameAs(first));
            Assert.That(options.RouteRequestLimits[1].BaseRoute, Is.EqualTo("/a/"));

            using var server = new WebServer(options);
            Assert.That(() => first.MaximumRequestBodyBytes = 6, Throws.InvalidOperationException);
            Assert.That(() => options.AddRouteRequestLimits("/c", _ => { }), Throws.InvalidOperationException);
        }

        private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, int length)
            => await client.SendAsync(Post(client, url, new ByteArrayContent(new byte[length])));

        private static async Task<string> PostLengthAsync(HttpClient client, string url, int length)
        {
            using var response = await PostAsync(client, url, length);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), url);
            return await response.Content.ReadAsStringAsync();
        }
    }
}
