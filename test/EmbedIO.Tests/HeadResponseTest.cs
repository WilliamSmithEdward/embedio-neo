using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HeadResponseTest
    {
        [TestCase(HttpListenerMode.EmbedIO, false, "identity")]
        [TestCase(HttpListenerMode.EmbedIO, true, "identity")]
        [TestCase(HttpListenerMode.Microsoft, false, "identity")]
        [TestCase(HttpListenerMode.Microsoft, true, "identity")]
        [TestCase(HttpListenerMode.EmbedIO, false, "gzip")]
        [TestCase(HttpListenerMode.EmbedIO, true, "gzip")]
        [TestCase(HttpListenerMode.Microsoft, false, "gzip")]
        [TestCase(HttpListenerMode.Microsoft, true, "gzip")]
        public async Task HeadLengthDescribesGetRepresentationOrIsOmitted(HttpListenerMode mode, bool cacheContent, string encoding)
        {
            var directory = Directory.CreateTempSubdirectory("embedio-head-");
            var url = Resources.GetServerAddress();
            File.WriteAllText(Path.Combine(directory.FullName, "file.txt"), new string('a', 2048));
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new FileModule("/", new FileSystemProvider(directory.FullName, isImmutable: true)) { ContentCaching = cacheContent });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var beforeRequest = new HttpRequestMessage(HttpMethod.Head, url + "file.txt");
                beforeRequest.Headers.AcceptEncoding.ParseAdd(encoding);
                using var before = await client.SendAsync(beforeRequest);
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, url + "file.txt");
                getRequest.Headers.AcceptEncoding.ParseAdd(encoding);
                using var get = await client.SendAsync(getRequest);
                var representation = await get.Content.ReadAsByteArrayAsync();
                using var afterRequest = new HttpRequestMessage(HttpMethod.Head, url + "file.txt");
                afterRequest.Headers.AcceptEncoding.ParseAdd(encoding);
                using var after = await client.SendAsync(afterRequest);
                foreach (var head in new[] { before, after })
                {
                    Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(head.Headers.ETag, Is.EqualTo(get.Headers.ETag));
                    Assert.That(head.Content.Headers.ContentEncoding, Is.EqualTo(get.Content.Headers.ContentEncoding));
                    Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                    if (encoding == "identity")
                        Assert.That(head.Content.Headers.ContentLength, Is.EqualTo(representation.Length));
                    else if (head.Content.Headers.TryGetValues(HttpHeaderNames.ContentLength, out var lengths))
                        Assert.That(lengths, Is.EqualTo(new[] { representation.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                }

                if (encoding == "gzip")
                    Assert.That(representation.Length, Is.LessThan(2048));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                directory.Delete(true);
            }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task EmptyOrdinaryResponseDoesNotAdvertiseUnsentBytes(HttpListenerMode mode)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .OnGet("/empty", context =>
                {
                    if (mode == HttpListenerMode.EmbedIO)
                        context.Response.ContentLength64 = 123;
                    return Task.CompletedTask;
                })
                .OnGet("/next", context => context.SendStringAsync("next", "text/plain", System.Text.Encoding.UTF8));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                using var response = await client.GetAsync(url + "empty");
                Assert.That(response.Content.Headers.ContentLength, Is.EqualTo(0));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(await client.GetStringAsync(url + "next"), Is.EqualTo("next"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
