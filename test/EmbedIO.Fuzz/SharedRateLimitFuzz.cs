using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.RateLimiting;
using EmbedIO.Routing;
using EmbedIO.Sessions;

// Drives the actual module admission boundary; fake contexts expose only the
// lifetime surface it uses. Three actors can contend on each of three keys.
internal static class SharedRateLimitFuzz
{
    private const int Permits = 2, Queue = 2;
    private static readonly MethodInfo Admission = typeof(RateLimitingModule).GetMethod("OnRequestAsync", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing rate limiter admission.");
    private enum Phase { Waiting, Admitted, Rejected, Cancelled, RegistrationFailed, Closed }
    private sealed class RegistrationFailure : InvalidOperationException { }
    private sealed class Request(Context context, int actor, Task pending)
    {
        internal Context Context { get; } = context;
        internal int Actor { get; } = actor;
        internal Task Pending { get; } = pending;
        internal Phase State { get; set; }
    }

    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(trace);
        using var limiter = PartitionedRateLimiter.Create<IHttpContext, int>(context =>
            RateLimitPartition.GetConcurrencyLimiter(((Context)context).Key, _ => new ConcurrencyLimiterOptions
            { PermitLimit = Permits, QueueLimit = Queue, QueueProcessingOrder = QueueProcessingOrder.OldestFirst }));
        var module = new RateLimitingModule("/", limiter);
        var requests = new List<Request>();
        var slots = new Request?[12];
        var probes = Enumerable.Range(0, 3).Select(key => new Context(key, false)).ToArray();
        var finished = false;
        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var index = step.Actor * 4 + step.Value % 4;
                var request = slots[index];
                switch (step.Command)
                {
                    case 0:
                    case 1:
                    case 8:
                    case 9:
                        if (request != null && (request.State == Phase.Admitted || request.State == Phase.Waiting))
                        { trace.Cover("rate.skip.occupied"); break; }
                        var key = (step.Value / 4) % 3;
                        var context = new Context(key, step.Command == 1 || step.Command == 9);
                        var pending = (Task)(Admission.Invoke(module, new object[] { context })
                            ?? throw new InvalidOperationException("Missing admission task."));
                        request = new Request(context, step.Actor, pending);
                        slots[index] = request;
                        requests.Add(request);
                        if (Count(requests, key, Phase.Admitted) < Permits)
                            request.State = context.RejectClose ? Phase.RegistrationFailed : Phase.Admitted;
                        else if (Count(requests, key, Phase.Waiting) > Queue)
                            request.State = Phase.Rejected;
                        else request.State = Phase.Waiting;
                        if (request.State != Phase.Waiting) await Observe(request, token).ConfigureAwait(false);
                        trace.Cover("rate.admission." + request.State);
                        break;
                    case 2:
                    case 3:
                        if (request == null) { trace.Cover("rate.skip.close"); break; }
                        if (request.State == Phase.Waiting) { trace.Cover("rate.skip.close-waiting"); break; }
                        request.Context.Close();
                        if (request.State == Phase.Admitted) request.State = Phase.Closed;
                        trace.Cover("rate.close");
                        break;
                    case 4:
                    case 5:
                        if (request == null) { trace.Cover("rate.skip.cancel"); break; }
                        request.Context.Cancel();
                        if (request.State == Phase.Waiting)
                        {
                            request.State = Phase.Cancelled;
                            await Observe(request, token).ConfigureAwait(false);
                        }
                        // Cancellation after admission must not release the permit;
                        // application completion still owns the close callback.
                        trace.Cover("rate.cancel." + request.State);
                        break;
                    case 6:
                        if (request == null || request.State != Phase.Waiting) { trace.Cover("rate.skip.reject-registration"); break; }
                        request.Context.RejectClose = true;
                        trace.Cover("rate.registration-will-fail");
                        break;
                    case 7:
                        foreach (var held in requests.Where(item => item.Actor == step.Actor && item.State == Phase.Admitted))
                        {
                            held.Context.Close();
                            held.State = Phase.Closed;
                        }
                        trace.Cover("rate.actor.completed");
                        break;
                    case 10:
                    case 11:
                        foreach (var waiting in requests.Where(item => item.Actor == step.Actor && item.State == Phase.Waiting))
                        {
                            waiting.Context.Cancel();
                            waiting.State = Phase.Cancelled;
                            await Observe(waiting, token).ConfigureAwait(false);
                        }
                        trace.Cover("rate.actor.cancelled");
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(trace), "Unknown rate limit command.");
                }
                await Settle(requests, token).ConfigureAwait(false);
                Verify(limiter, probes, requests);
                if (requests.Where(item => item.State == Phase.Admitted || item.State == Phase.Waiting).Select(item => item.Actor).Distinct().Count() > 1)
                    trace.Cover("rate.actors.overlap");
            }
            finished = true;
        }
        finally
        {
            // Cancel all queued waiters before releasing held permits, so cleanup
            // cannot accidentally admit a context that the trace no longer owns.
            foreach (var request in requests.Where(item => !item.Pending.IsCompleted)) request.Context.Cancel();
            try
            {
                await Task.WhenAll(requests.Select(async request =>
                {
                    try
                    {
                        // No detached timeout: the campaign watchdog owns cleanup too.
                        try { await request.Pending.ConfigureAwait(false); }
                        catch (RegistrationFailure) { }
                        catch (HttpException error) when (error.StatusCode == 429) { }
                        catch (OperationCanceledException) when (request.Context.CancellationToken.IsCancellationRequested) { }
                        if (request.State == Phase.Waiting) request.State = Phase.Cancelled;
                    }
                    finally
                    {
                        try
                        {
                            request.Context.Close();
                            if (request.State == Phase.Admitted) request.State = Phase.Closed;
                        }
                        finally { request.Context.Dispose(); }
                    }
                })).ConfigureAwait(false);
                if (finished) Verify(limiter, probes, requests);
            }
            finally { foreach (var probe in probes) probe.Dispose(); }
        }
    }

    private static async Task Settle(List<Request> requests, CancellationToken token)
    {
        for (var key = 0; key < 3; key++)
        {
            while (Count(requests, key, Phase.Admitted) < Permits)
            {
                var next = requests.FirstOrDefault(item => item.Context.Key == key && item.State == Phase.Waiting);
                if (next == null) break;
                next.State = next.Context.RejectClose ? Phase.RegistrationFailed : Phase.Admitted;
                await Observe(next, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task Observe(Request request, CancellationToken token)
    {
        var result = Phase.Admitted;
        try { await request.Pending.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false); }
        catch (RegistrationFailure) { result = Phase.RegistrationFailed; }
        catch (HttpException error) when (error.StatusCode == 429) { result = Phase.Rejected; }
        catch (OperationCanceledException) when (request.Context.CancellationToken.IsCancellationRequested && !token.IsCancellationRequested)
        { result = Phase.Cancelled; }
        Check(result == request.State, "rate.admission-result", $"Expected {request.State}, got {result} on key {request.Context.Key}.");
    }

    private static int Count(List<Request> requests, int key, Phase phase)
        => requests.Count(item => item.Context.Key == key && item.State == phase);

    private static void Verify(PartitionedRateLimiter<IHttpContext> limiter, Context[] probes, List<Request> requests)
    {
        foreach (var probe in probes)
        {
            var stats = limiter.GetStatistics(probe) ?? throw new InvalidOperationException("Missing concurrency limiter statistics.");
            var held = Count(requests, probe.Key, Phase.Admitted);
            var queued = Count(requests, probe.Key, Phase.Waiting);
            Check(stats.CurrentAvailablePermits == Permits - held && stats.CurrentQueuedCount == queued,
                "rate.ledger", $"Key {probe.Key}: expected available={Permits - held}, queued={queued}; actual {stats.CurrentAvailablePermits}, {stats.CurrentQueuedCount}.");
        }
        foreach (var request in requests)
        {
            if (request.State == Phase.Waiting)
                Check(!request.Pending.IsCompleted && request.Context.Registrations == 0, "rate.pending-isolation", "Queued request completed or owns a close callback.");
            else if (request.State == Phase.Admitted || request.State == Phase.Closed)
                Check(request.Context.Registrations == 1, "rate.close-registration", "Admitted request did not register exactly one close callback.");
            else Check(request.Context.Registrations == 0, "rate.failed-registration", "Unadmitted request owns a close callback.");
        }
    }

    private static void Check(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }

    private sealed class Context(int key, bool rejectClose) : IHttpContext, IDisposable
    {
        private readonly CancellationTokenSource _cancel = new();
        private Action<IHttpContext>? _close;
        internal int Key { get; } = key;
        internal bool RejectClose { get; set; } = rejectClose;
        internal int Registrations { get; private set; }
        public CancellationToken CancellationToken => _cancel.Token;
        public void OnClose(Action<IHttpContext> callback)
        {
            if (RejectClose) throw new RegistrationFailure();
            Registrations++;
            _close += callback;
        }
        internal void Close() => _close?.Invoke(this);
        internal void Cancel() => _cancel.Cancel();
        public void Dispose() => _cancel.Dispose();
        public string Id => throw new NotSupportedException();
        public IPEndPoint LocalEndPoint => throw new NotSupportedException();
        public IPEndPoint RemoteEndPoint => throw new NotSupportedException();
        public IHttpRequest Request => throw new NotSupportedException();
        public RouteMatch Route => throw new NotSupportedException();
        public string RequestedPath => throw new NotSupportedException();
        public IHttpResponse Response => throw new NotSupportedException();
        public IPrincipal User => throw new NotSupportedException();
        public ISessionProxy Session => throw new NotSupportedException();
        public bool SupportCompressedRequests => false;
        public IDictionary<object, object> Items => throw new NotSupportedException();
        public long Age => 0;
        public bool IsHandled => false;
        public void SetHandled() => throw new NotSupportedException();
        public string? GetMimeType(string extension) => throw new NotSupportedException();
        public bool TryDetermineCompression(string mimeType, out bool preferCompression) => throw new NotSupportedException();
    }
}
