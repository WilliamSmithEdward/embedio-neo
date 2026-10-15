using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        // Serve a request, stop, and start a new server on the same endpoint at once.
        // Accepted connections share the listener's MsQuic binding, which is released
        // only when MsQuic frees the last of them.
        [Test]
        public async Task ServedListenerRebindsItsEndpointImmediately()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var cycle = 0;
            var stage = "start";
            try
            {
                for (; cycle < 32; ++cycle)
                {
                    stage = "start";
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                        .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync("served", "text/plain", WebServer.Utf8NoBomEncoding));
                    var running = server.RunAsync(stop.Token);
                    try
                    {
                        stage = "serve";
                        using (var client = Client(certificate))
                            Assert.That(await client.GetStringAsync(prefix, stop.Token), Is.EqualTo("served"));
                        stage = "stop";
                    }
                    finally
                    {
                        // A failed start faults the run task; its error replaces the request's.
                        stop.Cancel();
                        await running.WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }
            }
            finally
            {
                TestContext.Out.WriteLine($"EmbedIO HTTP/3 served rebind: cycle={cycle}, stage={stage}, prefix={prefix}");
            }
        }
    }
}
