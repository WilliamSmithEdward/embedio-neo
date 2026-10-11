using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue464_EndpointConfiguration
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task PrefixPathSelectsRequestsWithoutRewritingModulePaths(HttpListenerMode mode)
        {
            var root = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefix(root + "scope/").WithMode(mode))
                .OnGet("/scope/hello", c => c.SendStringAsync(c.Request.Url.AbsolutePath,
                    "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient();
            try
            {
                Assert.That(await client.GetStringAsync(root + "scope/hello"), Is.EqualTo("/scope/hello"));
                Assert.That(await client.GetStringAsync(root + "scope/hello/child"), Is.EqualTo("/scope/hello/child"));
                try
                {
                    using var outside = await client.GetAsync(root + "outside");
                    Assert.That(outside.IsSuccessStatusCode, Is.False);
                }
                catch (HttpRequestException)
                {
                    // A listener with no matching prefix can close the connection
                    // instead of producing an HTTP error response.
                }
                Assert.That(await client.GetStringAsync(root + "scope/hello"), Is.EqualTo("/scope/hello"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task DistinctPathPrefixesSharePortAndLongestMatchWins(HttpListenerMode mode)
        {
            var root = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var general = CreateServer(root + "scope/", "general", mode);
            using var specific = CreateServer(root + "scope/nested/", "specific", mode);
            using var stopGeneral = new CancellationTokenSource();
            using var stopSpecific = new CancellationTokenSource();
            var generalRun = general.RunAsync(stopGeneral.Token);
            var specificRun = specific.RunAsync(stopSpecific.Token);
            using var client = CreateClient();
            try
            {
                Assert.That(await client.GetStringAsync(root + "scope/hello"), Is.EqualTo("general"));
                Assert.That(await client.GetStringAsync(root + "scope/nested/hello"), Is.EqualTo("specific"));
                stopSpecific.Cancel();
                await specificRun.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await client.GetStringAsync(root + "scope/nested/hello"), Is.EqualTo("general"));
            }
            finally
            {
                stopGeneral.Cancel(); stopSpecific.Cancel();
                await Task.WhenAll(generalRun, specificRun).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task OneServerCanRegisterIndependentPorts(HttpListenerMode mode)
        {
            var first = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            var second = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using var server = new WebServer(o => o.WithUrlPrefixes(first, second).WithMode(mode))
                .OnGet("/hello", c => c.SendStringAsync(c.Request.Url.Port.ToString(
                    System.Globalization.CultureInfo.InvariantCulture), "text/plain", WebServer.Utf8NoBomEncoding));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = CreateClient();
            try
            {
                Assert.That(await client.GetStringAsync(first + "hello"), Is.EqualTo(new Uri(first).Port.ToString()));
                Assert.That(await client.GetStringAsync(second + "hello"), Is.EqualTo(new Uri(second).Port.ToString()));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static WebServer CreateServer(string prefix, string body, HttpListenerMode mode)
            => new WebServer(o => o.WithUrlPrefix(prefix).WithMode(mode))
                .OnAny(c => c.SendStringAsync(body, "text/plain", WebServer.Utf8NoBomEncoding));

        [TestCase(HttpListenerMode.EmbedIO, "*")]
        [TestCase(HttpListenerMode.EmbedIO, "+")]
        public async Task WildcardPrefixAcceptsAnOtherwiseUnregisteredHost(HttpListenerMode mode, string host)
        {
            var target = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            var port = new Uri(target).Port;
            using var server = CreateServer($"http://{host}:{port}/", "wildcard", mode);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            if (mode == HttpListenerMode.Microsoft && OperatingSystem.IsWindows()
                && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
                && running.IsFaulted && running.Exception?.GetBaseException() is HttpListenerException denied
                && denied.NativeErrorCode == 5)
            {
                Assert.Ignore("Local HTTP.sys wildcard registration needs a URL reservation or elevation; CI must execute this case.");
            }
            using var client = CreateClient();
            client.DefaultRequestHeaders.Host = $"unregistered.invalid:{port}";
            try { Assert.That(await client.GetStringAsync(target + "hello"), Is.EqualTo("wildcard")); }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        public async Task ExplicitIpv6LoopbackPrefixPreservesListenerPlatformBehavior(HttpListenerMode mode)
        {
            if (!System.Net.Sockets.Socket.OSSupportsIPv6
                && (mode != HttpListenerMode.Microsoft || OperatingSystem.IsWindows()))
                Assert.Ignore("IPv6 is unavailable.");
            var port = new Uri(Resources.GetServerAddress()).Port;
            var target = $"http://[::1]:{port}/";
            using var server = CreateServer(target, "ipv6", mode);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            if (mode == HttpListenerMode.Microsoft && !OperatingSystem.IsWindows())
            {
                // .NET 10's Unix prefix parser mistakes the first IPv6 colon
                // for a port separator. Verify and document that native limit;
                // selecting the managed listener remains an explicit choice.
                await Assert.ThatAsync(() => running, Throws.TypeOf<HttpListenerException>()
                    .With.Property(nameof(HttpListenerException.NativeErrorCode)).EqualTo(400));
                return;
            }
            using var client = CreateClient();
            try { Assert.That(await client.GetStringAsync(target + "hello"), Is.EqualTo("ipv6")); }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private static HttpClient CreateClient() => new(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
    }
}
