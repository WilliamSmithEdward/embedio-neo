using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RequestAbortHttp3ResetIsStreamLocalAndInputEndIsNormal(bool inputEnd)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(options => options.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    var token = context.GetRequestAbortedToken(inputEnd);
                    Assert.That(context.GetRequestAbortedToken(!inputEnd), Is.EqualTo(token));
                    if (context.Request.Url.AbsolutePath == "/blocked")
                    {
                        entered.TrySetResult(true);
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { canceled.TrySetResult(true); }
                    }
                    else
                    {
                        Assert.That(token.IsCancellationRequested, Is.False);
                        await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                    }
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                using var cancel = new CancellationTokenSource();
                var blocked = client.GetAsync(prefix + "blocked", cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetStringAsync(prefix + "sibling"), Is.EqualTo("healthy"));
                cancel.Cancel();
                await Assert.ThatAsync(async () => await blocked, Throws.InstanceOf<OperationCanceledException>());
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetStringAsync(prefix + "after-reset"), Is.EqualTo("healthy"));
            }
            finally { stop.Cancel(); await running; }
        }
    }
}
