using NUnit.Framework;
using EmbedIO.Internal;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace EmbedIO.Tests
{
    [TestFixture]
    public class IPv6Test
    {
        [TestCase("http://[::1]:8877")]
        [TestCase("http://127.0.0.1:8877")]
        public async Task WithUseIpv6_ReturnsValid(string urlTest)
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                Assert.Ignore("Only Windows");

            using var instance = new WebServer(HttpListenerMode.EmbedIO, "http://*:8877");
            instance.OnAny(ctx => ctx.SendDataAsync(DateTime.Now));

            var runTask = instance.RunAsync();
            if (runTask.IsCompleted) await runTask;

            using var client = new HttpClient();
            Assert.IsNotEmpty(await client.GetStringAsync(urlTest));
        }

        [Test]
        public async Task WithIpv6Loopback_ReturnsValid()
        {
            if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                Assert.Ignore("Only Windows");

            using var instance = new WebServer(HttpListenerMode.EmbedIO, "http://[::1]:8877");
            instance.OnAny(ctx => ctx.SendDataAsync(DateTime.Now));

            var runTask = instance.RunAsync();
            if (runTask.IsCompleted) await runTask;

            using var client = new HttpClient();
            Assert.IsNotEmpty(await client.GetStringAsync("http://[::1]:8877"));
        }
    }
}
