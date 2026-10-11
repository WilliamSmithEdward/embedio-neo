using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1ContextCollectionsTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentFirstAccessPublishesStableMutableRequestLocalCollections(bool withQuery)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            var contexts = new List<IHttpContext>();
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithAction("/", HttpVerbs.Any, async context =>
            {
                var access = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(
                    () => (Items: context.Items, Query: context.Request.QueryString))));
                foreach (var pair in access)
                {
                    Assert.That(pair.Items, Is.SameAs(access[0].Items));
                    Assert.That(pair.Query, Is.SameAs(access[0].Query));
                }
                Assert.That(context.Items, Is.Empty);
                Assert.That(context.Request.QueryString.GetValues("x"),
                    withQuery ? Is.EqualTo(new[] { "a b", "c" }) : Is.Null);
                var ordinal = contexts.Count;
                context.Items["ordinal"] = ordinal;
                context.Request.QueryString.Add("added", ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                contexts.Add(context);
                await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                var target = url + (withQuery ? "?x=a+b&x=c" : "");
                Assert.That(await client.GetStringAsync(target, stop.Token), Is.EqualTo("ok"));
                Assert.That(await client.GetStringAsync(target, stop.Token), Is.EqualTo("ok"));
                Assert.That(contexts, Has.Count.EqualTo(2));
                Assert.That(contexts[0].Items, Is.Not.SameAs(contexts[1].Items));
                Assert.That(contexts[0].Request.QueryString, Is.Not.SameAs(contexts[1].Request.QueryString));
                Assert.That(contexts[0].Items["ordinal"], Is.EqualTo(0));
                Assert.That(contexts[1].Items["ordinal"], Is.EqualTo(1));
                contexts[0].Request.QueryString["added"] = "retained";
                Assert.That(contexts[1].Request.QueryString["added"], Is.EqualTo("1"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
