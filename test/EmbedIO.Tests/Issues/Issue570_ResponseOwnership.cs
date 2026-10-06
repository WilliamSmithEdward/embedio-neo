using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue570_ResponseOwnership
    {
        [TestCase("close")]
        [TestCase("dispose")]
        [TestCase("concurrent-dispose")]
        public async Task LateCloseOrDisposeOfCompletedResponseDoesNotCloseNextKeepAliveRequest(string operation)
        {
            var firstReady = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondReady = new TaskCompletionSource<IHttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var url = Resources.GetServerAddress();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
                {
                    if (context.Request.Url.AbsolutePath == "/first")
                    {
                        await context.SendDataAsync(new { Value = "first" });
                        firstReady.TrySetResult(context);
                        await firstRelease.Task;
                    }
                    else if (context.Request.Url.AbsolutePath == "/second")
                    {
                        secondReady.TrySetResult(context);
                        await secondRelease.Task;
                        await context.SendDataAsync(new { Value = "second" });
                    }
                    else await context.SendDataAsync(new { Value = "healthy" });
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 1 };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var first = await client.GetAsync(url + "first");
                first.EnsureSuccessStatusCode();
                var old = await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var body = new StringContent("{}");
                var secondRequest = client.PostAsync(url + "second", body);
                var current = await secondReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(current.Request.RemoteEndPoint!.Port, Is.EqualTo(old.Request.RemoteEndPoint!.Port), "Exercise actual TCP reuse.");
                Assert.That(current, Is.Not.SameAs(old));
                if (operation == "concurrent-dispose")
                    await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => ((IDisposable)old.Response).Dispose())));
                else if (operation == "dispose") ((IDisposable)old.Response).Dispose();
                else old.Response.Close();
                firstRelease.TrySetResult();
                secondRelease.TrySetResult();
                using var second = await secondRequest;
                Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await second.Content.ReadAsStringAsync(), Does.Contain("second"));
                using var healthy = await client.GetAsync(url + "health");
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            }
            finally
            {
                firstRelease.TrySetResult();
                secondRelease.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
