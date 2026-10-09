using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase("text/plain", HttpStatusCode.OK)]
        [TestCase("application/json", HttpStatusCode.UnsupportedMediaType)]
        public async Task QueryPolicyAdvertisesFormatsOnHttp3(string mediaType, HttpStatusCode expected)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var policy = new QueryFormatPolicy("text/plain;charset=utf-8");
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Query, async context =>
                {
                    policy.Apply(context);
                    await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                using var request = Request(new HttpMethod("QUERY"), prefix, new StringContent("query", WebServer.Utf8NoBomEncoding, mediaType));
                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version30));
                Assert.That(response.StatusCode, Is.EqualTo(expected));
                Assert.That(response.Headers.GetValues(HttpHeaderNames.AcceptQuery), Is.EqualTo(new[] { policy.AcceptQueryHeaderValue }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
