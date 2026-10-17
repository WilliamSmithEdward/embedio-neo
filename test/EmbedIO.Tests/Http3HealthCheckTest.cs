using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Health;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [Test]
        public async Task HealthCheckHttp3ReadinessAndHead()
        {
            using var certificate = Certificate(); var prefix = Prefix();
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            var ready = 0;
            using var health = new HealthCheckService(server, new[] { new HealthCheckRegistration("application", _ => Task.FromResult(new HealthCheckResult(Volatile.Read(ref ready) == 0 ? HealthStatus.Unhealthy : HealthStatus.Healthy))) });
            server.WithModule(new HealthCheckModule(health));
            using var stop = new CancellationTokenSource(); var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                using var first = await client.GetAsync(prefix + "health/ready");
                Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable)); Assert.That(first.Version.Major, Is.EqualTo(3));
                Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("Unhealthy"));
                Volatile.Write(ref ready, 1); await health.WaitForReadyAsync(TimeSpan.FromSeconds(3));
                using var second = await client.GetAsync(prefix + "health/ready"); Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                using var request = Request(HttpMethod.Head, prefix + "health/ready"); using var head = await client.SendAsync(request);
                Assert.That(head.StatusCode, Is.EqualTo(HttpStatusCode.OK)); Assert.That(await head.Content.ReadAsByteArrayAsync(), Is.Empty);
                await server.DrainAsync(TimeSpan.FromSeconds(3));
                Assert.That((await health.CheckReadinessAsync()).Status, Is.EqualTo(HealthStatus.Unhealthy));
            }
            finally { stop.Cancel(); await running; }
        }
    }
}
