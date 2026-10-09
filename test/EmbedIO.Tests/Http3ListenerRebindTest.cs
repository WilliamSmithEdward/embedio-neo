using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        // Restart on the same endpoint without pausing. On macOS, MsQuic 2.6.2 closes a
        // disposed listener's socket after disposal has returned.
        [Test]
        public async Task StoppedListenerRebindsItsEndpointImmediately()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("rebound", "text/plain", WebServer.Utf8NoBomEncoding));
            var cycle = 0;
            var stage = "start";
            try
            {
                for (; cycle < 64; ++cycle)
                {
                    stage = "start";
                    server.Listener.Start();
                    Assert.That(server.Listener.IsListening, Is.True);
                    stage = "stop";
                    server.Listener.Stop();
                }
                stage = "serve";
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var running = server.RunAsync(stop.Token);
                try
                {
                    using var client = Client(certificate);
                    Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("rebound"));
                }
                finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            finally
            {
                TestContext.Out.WriteLine($"EmbedIO HTTP/3 rebind: cycle={cycle}, stage={stage}, prefix={prefix}");
            }
        }
    }
}
