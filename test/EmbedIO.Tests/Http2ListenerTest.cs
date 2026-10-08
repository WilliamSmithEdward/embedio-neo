using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2ListenerTest
    {
        [Test]
        public async Task StopCancelsActiveHttp2Application()
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:");
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult(true);
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                    finally { exited.TrySetResult(true); }
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(15),
            };
            var pending = client.GetAsync(url);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<HttpRequestException>());
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                try { await pending; } catch (HttpRequestException) { }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RealWebServerMultiplexesUploadsAndRetainsHttp11(bool secure)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https:", "http:");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    Assert.That(context.Request.IsSecureConnection, Is.EqualTo(secure));
                    Assert.That(context.LocalEndPoint.Port, Is.EqualTo(new Uri(url).Port));
                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                    context.Response.ContentLength64 = body.Length;
                    await context.Response.OutputStream.WriteAsync(body.ToArray(), context.CancellationToken);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = secure ? HttpsSmoke.CreateClient(certificate) : new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 });
            client.Timeout = TimeSpan.FromSeconds(15);
            try
            {
                await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
                {
                    var bytes = Enumerable.Range(0, 70000 + index).Select(i => (byte)(i + index)).ToArray();
                    using var request = new HttpRequestMessage(HttpMethod.Post, url + "echo/" + index)
                    {
                        Version = HttpVersion.Version20,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Content = new ByteArrayContent(bytes),
                    };
                    using var response = await client.SendAsync(request);
                    Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
                }));
                using var fallback = new HttpRequestMessage(HttpMethod.Post, url) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new StringContent("fallback") };
                using var result = await client.SendAsync(fallback);
                Assert.That(result.Version, Is.EqualTo(HttpVersion.Version11));
                Assert.That(await result.Content.ReadAsStringAsync(), Is.EqualTo("fallback"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
