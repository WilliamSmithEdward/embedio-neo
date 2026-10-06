using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue554_UwpHostAccess
    {
        [TestCase("*", false)]
        [TestCase("*", true)]
        [TestCase("+", false)]
        [TestCase("+", true)]
        [TestCase("localhost", false)]
        [TestCase("localhost", true)]
        [TestCase("127.0.0.1", false)]
        [TestCase("127.0.0.1", true)]
        public async Task HtmlApiAndSessionsRemainAccessibleToIndependentClients(string host, bool cache)
        {
            var port = new Uri(Resources.GetServerAddress()).Port;
            var prefix = $"http://{host}:{port}/";
            var url = $"http://127.0.0.1:{port}/";
            var folder = Path.Combine(Path.GetTempPath(), "embedio-554-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html><title>EmbedIO</title><p>external-browser-554</p>");
            File.WriteAllText(Path.Combine(folder, "asset.txt"), "asset-554");
            var states = new ConcurrentQueue<WebServerState>();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO))
                .WithLocalSessionManager()
                .WithWebApi("/api", m => m.WithController<ProbeController>())
                .WithStaticFolder("/", folder, true, m => m.WithContentCaching(cache));
            server.StateChanged += (_, e) => states.Enqueue(e.NewState);
            using var stopping = new CancellationTokenSource();
            var running = server.RunAsync(stopping.Token);
            try
            {
                using var first = Client(url);
                using var second = Client(url);
                Assert.That(await first.GetStringAsync("/"), Does.Contain("external-browser-554"));
                Assert.That(await second.GetStringAsync("/"), Does.Contain("external-browser-554"));
                Assert.That(await first.GetStringAsync("/asset.txt"), Is.EqualTo("asset-554"));
                var a = JsonSerializer.Deserialize<JsonElement>(await first.GetStringAsync("/api/probe"));
                var b = JsonSerializer.Deserialize<JsonElement>(await first.GetStringAsync("/api/probe"));
                var other = JsonSerializer.Deserialize<JsonElement>(await second.GetStringAsync("/api/probe"));
                Assert.That(a.GetProperty("session").GetString(), Is.EqualTo(b.GetProperty("session").GetString()));
                Assert.That(other.GetProperty("session").GetString(), Is.Not.EqualTo(a.GetProperty("session").GetString()));
                Assert.That(IPAddress.Parse(a.GetProperty("localAddress").GetString()!).MapToIPv4(), Is.EqualTo(IPAddress.Loopback));
                Assert.That(IPAddress.Parse(a.GetProperty("remoteAddress").GetString()!).MapToIPv4(), Is.EqualTo(IPAddress.Loopback));
                using var absent = await first.GetAsync("/missing.txt");
                Assert.That(absent.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(states.ToArray(), Does.Contain(WebServerState.Listening));
            }
            finally
            {
                stopping.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                Directory.Delete(folder, true);
            }
        }

        private static HttpClient Client(string url)
            => new HttpClient(new HttpClientHandler { UseProxy = false, CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(5) };

        public sealed class ProbeController : WebApiController
        {
            [Route(HttpVerbs.Get, "/probe")]
            public object Probe()
            {
                HttpContext.Session["visits"] = HttpContext.Session.GetValue<int>("visits") + 1;
                return new
                {
                    session = HttpContext.Session.Id,
                    localAddress = HttpContext.Request.LocalEndPoint.Address.ToString(),
                    remoteAddress = HttpContext.Request.RemoteEndPoint.Address.ToString(),
                };
            }
        }
    }
}
