using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Health;

// Three independently held physical connections and an external generation/outcome ledger.
internal static class RequestAbortFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--request-abort-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --request-abort-selftest");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("request-abort", 1729, Array.Empty<StateMachineStep>()), timeout.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "abort.owner-isolation")
        { Console.WriteLine("Rejected injected foreign request token: " + failure.Invariant); return true; }
        throw new StateMachineFailure("abort.negative-oracle", "Injected foreign request token escaped the ledger.");
    }
    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var ledger = new ConcurrentDictionary<string, Request>();
        var history = new List<Request>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer($"http://127.0.0.1:{port}/").WithModule(new ActionModule(async context =>
        {
            var id = context.Request.Url.AbsolutePath;
            if (!ledger.TryGetValue(id, out var request)) throw new StateMachineFailure("abort.request-identity", id);
            request.Context = context;
            request.Normal = context.GetRequestAbortedToken();
            request.Strict = context.GetRequestAbortedToken(true);
            context.OnClose(_ => request.Closed.TrySetResult());
            request.Entered.TrySetResult();
            await request.Release.Task.WaitAsync(stop.Token);
            if (request.Reset) return;
            var body = Encoding.ASCII.GetBytes(id);
            context.Response.KeepAlive = false;
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body, stop.Token);
        }));
        using var health = new HealthCheckService(server);
        var running = server.RunAsync(stop.Token);
        var actors = new Request[3];
        var generations = new int[3];
        try
        {
            await health.WaitForReadyAsync(TimeSpan.FromSeconds(3), token);
            for (var actor = 0; actor < 3; actor++) actors[actor] = await Start(actor);
            Require(actors.Select(request => request.Socket.LocalEndPoint).Distinct().Count() == 3, "abort.physical-connections");
            trace.Cover("abort.three-overlapping-connections");
            await Reset(actors[0]);
            for (var actor = 0; actor < 3; actor++)
            {
                var observed = injectFault && actor == 1 ? actors[0] : actors[actor];
                Require(observed.Normal.IsCancellationRequested == (actor == 0), "abort.owner-isolation");
            }
            actors[0] = await Start(0);
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var request = actors[step.Actor];
                switch (step.Command % 4)
                {
                    case 0:
                        await Finish(request); actors[step.Actor] = await Start(step.Actor); trace.Cover("abort.complete-and-replace"); break;
                    case 1:
                        request.InputEnded = true;
                        request.Socket.Shutdown(SocketShutdown.Send);
                        await Canceled(request.Strict, "input-end", request);
                        Require(!request.Normal.IsCancellationRequested, "abort.half-close-safe-default");
                        await Finish(request); actors[step.Actor] = await Start(step.Actor); trace.Cover("abort.explicit-input-end"); break;
                    case 2:
                        await Reset(request); actors[step.Actor] = await Start(step.Actor); trace.Cover("abort.reset-and-replace"); break;
                    default:
                        var captured = request.Context ?? throw new StateMachineFailure("abort.context-present", request.Id);
                        await Task.WhenAll(Enumerable.Range(0, 2 + step.Value % 8).Select(_ => Task.Run(() =>
                        {
                            Require(captured.GetRequestAbortedToken() == request.Normal, "abort.stable-token");
                            Require(captured.GetRequestAbortedToken(true) == request.Strict, "abort.stable-policy-token");
                        }, token)));
                        trace.Cover("abort.concurrent-acquisition"); break;
                }
                foreach (var old in history)
                {
                    Require(old.Normal.IsCancellationRequested == old.Reset, "abort.retained-generation");
                    Require(old.Strict.IsCancellationRequested == (old.Reset || old.InputEnded), "abort.retained-policy");
                }
            }
            foreach (var request in actors) await Finish(request);
            stop.Cancel(); await running;
            foreach (var old in history) Require(old.Normal.IsCancellationRequested == old.Reset, "abort.stop-after-completion");
        }
        finally
        {
            foreach (var request in ledger.Values) { request.Release.TrySetResult(); request.Dispose(); }
            stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(3));
        }
        async Task<Request> Start(int actor)
        {
            var request = new Request($"/{actor}/{generations[actor]++}");
            if (!ledger.TryAdd(request.Id, request)) throw new StateMachineFailure("abort.unique-generation", request.Id);
            await request.Socket.ConnectAsync(IPAddress.Loopback, port, token);
            request.Stream = new NetworkStream(request.Socket, false);
            var bytes = Encoding.ASCII.GetBytes($"GET {request.Id} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
            var sent = 0;
            while (sent != bytes.Length) sent += await request.Socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, token);
            await Observe(request.Entered.Task, "handler-entry", request);
            Require(!request.Normal.IsCancellationRequested && !request.Strict.IsCancellationRequested, "abort.fresh-generation");
            return request;
        }
        async Task Finish(Request request)
        {
            request.Release.TrySetResult();
            var stream = request.Stream ?? throw new StateMachineFailure("abort.stream-present", request.Id);
            using var reader = new StreamReader(stream, leaveOpen: true);
            var response = await reader.ReadToEndAsync(token);
            Require(response.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && response.EndsWith(request.Id, StringComparison.Ordinal), "abort.complete-body");
            await Observe(request.Closed.Task, "context-close", request);
            Require(!request.Normal.IsCancellationRequested, "abort.normal-completion");
            history.Add(request); request.Dispose();
        }
        async Task Reset(Request request)
        {
            request.Reset = true;
            request.Socket.LingerState = new LingerOption(true, 0);
            request.Socket.Dispose();
            await Canceled(request.Normal, "reset-normal", request); await Canceled(request.Strict, "reset-strict", request);
            request.Release.TrySetResult(); await Observe(request.Closed.Task, "context-close", request);
            history.Add(request); request.Dispose();
        }
        async Task Observe(Task operation, string stage, Request request)
        {
            try { await operation.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Preserve the exact wait and independent public token state on a case deadline.
                trace.Cover("abort.deadline." + stage + ".normal-" + request.Normal.IsCancellationRequested
                    + ".strict-" + request.Strict.IsCancellationRequested + ".closed-" + request.Closed.Task.IsCompleted
                    + ".reset-" + request.Reset + ".input-ended-" + request.InputEnded);
                throw;
            }
        }
        async Task Canceled(CancellationToken cancellation, string stage, Request request)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellation.Register(() => completion.TrySetResult());
            await Observe(completion.Task, stage, request);
        }
    }
    private static void Require(bool condition, string invariant)
    { if (!condition) throw new StateMachineFailure(invariant, "The independent connection/generation ledger disagrees."); }
    private sealed class Request(string id) : IDisposable
    {
        internal readonly string Id = id;
        internal readonly Socket Socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        internal NetworkStream? Stream;
        internal IHttpContext? Context;
        internal CancellationToken Normal;
        internal CancellationToken Strict;
        internal bool Reset;
        internal bool InputEnded;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose() { Stream?.Dispose(); Socket.Dispose(); }
    }
}
