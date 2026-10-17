using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.PlatformTests
{
    internal static class RequestAbortSmoke
    {
        internal static readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static readonly TaskCompletionSource<bool> Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static void Configure(WebServer server)
        {
            var module = new WebApiModule("/abort", ResponseSerializer.None(false));
#if NETFRAMEWORK
            module.WithController<RequestAbortSmokeController>();
#else
            module.WithController(RequestAbortSmokeBindings.RequestAbortSmokeController);
#endif
            server.WithModule(module);
        }
        internal static async Task RunAsync(HttpClient client, string url)
        {
            using var caller = new CancellationTokenSource();
            var pending = client.GetAsync(url + "abort/hold", caller.Token);
            await Wait(Entered.Task);
            caller.Cancel();
            try { using var unexpected = await pending; throw new InvalidOperationException("Canceled request unexpectedly completed."); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
            await Wait(Canceled.Task);
            if (await client.GetStringAsync(url + "abort/token") != "request-token") throw new InvalidOperationException("Request-abort controller binding failed.");
        }
        private static async Task Wait(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task) throw new TimeoutException("Request-abort smoke did not complete.");
            await task;
        }
    }
#if !NETFRAMEWORK
    [GeneratedControllerBinding(typeof(RequestAbortSmokeController))]
    internal static partial class RequestAbortSmokeBindings { }
#endif
    public sealed class RequestAbortSmokeController : WebApiController
    {
        [Route(HttpVerbs.Get, "/hold")]
        public async Task<string> Hold([RequestAborted(CancelOnHttp1InputEnd = true)] CancellationToken token)
        {
            RequestAbortSmoke.Entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, token); return "unreachable"; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { RequestAbortSmoke.Canceled.TrySetResult(true); throw; }
        }
        [Route(HttpVerbs.Get, "/token")]
        public string Token([RequestAborted] CancellationToken token)
            => token.CanBeCanceled && token == HttpContext.GetRequestAbortedToken() ? "request-token" : "wrong";
    }
}
