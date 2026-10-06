using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class ColdStartListenerRegressionTest
    {
        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task FailedStartupReleasesPortBeforeServerIsDisposed(HttpListenerMode mode, bool failInEvent)
        {
            if (mode == HttpListenerMode.Microsoft && !OperatingSystem.IsWindows())
                Assert.Ignore("HTTP.sys listener cases require Windows.");
            var url = Resources.GetServerAddress();
            using var failed = new WebServer(mode, url);
            var expected = new InvalidOperationException("startup failed");
            if (failInEvent)
                failed.StateChanged += (_, e) => { if (e.NewState == WebServerState.Listening) throw expected; };
            else
                failed.WithModule(new FailingModule(expected));
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => failed.RunAsync());
            Assert.That(actual, Is.SameAs(expected));
            Assert.That(failed.Listener.IsListening, Is.False);

            // Keep the failed server alive; cleanup must not depend on consumer Dispose/GC.
            using var replacement = new WebServer(mode, url);
            replacement.WithModule(new ActionModule("/", HttpVerbs.Any, c => c.SendStringAsync("ready", "text/plain", System.Text.Encoding.UTF8)));
            using var stop = new CancellationTokenSource();
            var running = replacement.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                Assert.That(await client.GetStringAsync(url), Is.EqualTo("ready"));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task AlreadyCanceledStartupDoesNotOpenListener(HttpListenerMode mode)
        {
            if (mode == HttpListenerMode.Microsoft && !OperatingSystem.IsWindows())
                Assert.Ignore("HTTP.sys listener cases require Windows.");
            using var server = new WebServer(mode, Resources.GetServerAddress());
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            await server.RunAsync(stop.Token);
            Assert.That(server.Listener.IsListening, Is.False);
            Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
        }

        private sealed class FailingModule : WebModuleBase
        {
            private readonly Exception _error;
            internal FailingModule(Exception error) : base("/") => _error = error;
            public override bool IsFinalHandler => false;
            protected override void OnStart(CancellationToken token) => throw _error;
            protected override Task OnRequestAsync(IHttpContext context) => Task.CompletedTask;
        }
    }
}
