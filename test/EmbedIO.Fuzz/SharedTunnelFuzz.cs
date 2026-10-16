using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

internal static class SharedTunnelFuzz
{
    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        var actors = new[] { new Actor(), new Actor(), new Actor() };
        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = actors[step.Actor];
                switch (step.Command)
                {
                    case 0: actor.Complete(); trace.Cover("tunnel.complete"); break;
                    case 1: actor.Close(); trace.Cover("tunnel.close"); break;
                    case 2: actor.Tunnel.Dispose(); actor.Closing = true; trace.Cover("tunnel.dispose"); break;
                    case 3: actor.Gate.TrySetResult(true); trace.Cover("tunnel.backend-success"); break;
                    case 4: actor.Gate.TrySetException(new IOException("scripted backend failure")); trace.Cover("tunnel.backend-failure"); break;
                    case 5: await actor.Cancel.CancelAsync(); trace.Cover("tunnel.cancel-first-token"); break;
                    case 6:
                        actor.Complete();
                        using (var canceled = new CancellationTokenSource())
                        {
                            await canceled.CancelAsync();
                            Require(ReferenceEquals(actor.Completion, actor.Tunnel.CompleteOutputAsync(canceled.Token)), "completion-identity", "A competing token created another completion task.");
                        }
                        trace.Cover("tunnel.competing-token");
                        break;
                    case 7:
                        actor.Close();
                        Require(ReferenceEquals(actor.Closure, actor.Tunnel.CloseAsync()), "close-identity", "Repeated close created another retirement task.");
                        trace.Cover("tunnel.repeated-close");
                        break;
                    case 8:
                        if (actor.Closure?.IsCompleted == true)
                        {
                            await actor.DisposeAsync();
                            actors[step.Actor] = new Actor();
                            trace.Cover("tunnel.replace-retired-actor");
                        }
                        else trace.Cover("tunnel.skip-live-replacement");
                        break;
                    case 9:
                        // All three completion operations are published while backend gates remain held.
                        foreach (var peer in actors) { peer.Complete(); peer.Close(); }
                        trace.Cover("tunnel.overlapping-complete-close");
                        break;
                    case 10: trace.Cover("tunnel.inspect"); break;
                    case 11:
                        actor.Close();
                        Require(ReferenceEquals(actor.Closure, actor.Tunnel.DisposeAsync().AsTask()), "async-disposal-identity", "Async disposal created unowned retirement work.");
                        trace.Cover("tunnel.async-repeated-dispose");
                        break;
                }

                foreach (var peer in actors)
                {
                    Require(peer.Callbacks <= 1, "completion-once", "A backend completion callback ran more than once.");
                    Require(peer.Stream.Disposals <= 1, "stream-dispose-once", "An owned stream was disposed more than once.");
                    if (!peer.Closing) Require(peer.Stream.Disposals == 0, "peer-isolation", "An actor's stream was disposed without closing that actor.");
                    if (peer.Stream.Disposals != 0 && peer.Completion != null)
                        Require(peer.Completion.IsCompleted, "pending-completion-ownership", "The stream was disposed before backend completion settled.");
                }
            }
        }
        finally
        {
            foreach (var actor in actors) actor.Gate.TrySetResult(true);
            var cleanup = new Task[actors.Length];
            for (var index = 0; index < actors.Length; index++) cleanup[index] = actors[index].DisposeAsync().AsTask();
            await Task.WhenAll(cleanup);
        }
    }

    private static async Task FinishAsync(Actor actor)
    {
        actor.Close();
        var closure = actor.Closure ?? throw new StateMachineFailure("close-publication", "Missing close task.");
        var outcome = 1;
        try { await closure; }
        catch (IOException) { outcome = 2; }
        catch (OperationCanceledException) { outcome = 3; }
        Require(actor.Callbacks == 0 ? outcome == 3 : outcome == actor.CallbackOutcome,
            "completion-outcome", "Retirement did not preserve the scripted backend completion outcome.");
        // Observe a scripted backend fault even if a pre-canceled completion never entered it.
        if (actor.Gate.Task.IsFaulted) _ = actor.Gate.Task.Exception;
        Require(actor.Stream.Disposals == 1, "retirement-releases-stream", "Retirement did not release its stream exactly once.");
        Require(ReferenceEquals(closure, actor.Tunnel.CloseAsync()), "retirement-stable", "Retired tunnel changed its close task.");
    }

    private static void Require(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }

    private sealed class Actor : IAsyncDisposable
    {
        internal readonly TrackingStream Stream = new();
        internal readonly CancellationTokenSource Cancel = new();
        internal readonly TaskCompletionSource<bool> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly HttpTunnel Tunnel;
        internal Task? Completion;
        internal Task? Closure;
        internal int Callbacks;
        internal int CallbackOutcome;
        internal bool Closing;
        internal Actor()
        {
            Tunnel = new HttpTunnel(Stream, async cancellation =>
            {
                Interlocked.Increment(ref Callbacks);
                try { await Gate.Task.WaitAsync(cancellation); CallbackOutcome = 1; }
                catch (IOException) { CallbackOutcome = 2; throw; }
                catch (OperationCanceledException) { CallbackOutcome = 3; throw; }
            });
        }
        internal void Complete()
        {
            var task = Tunnel.CompleteOutputAsync(Cancel.Token);
            if (Completion != null) Require(ReferenceEquals(Completion, task), "completion-stable", "Completion identity changed.");
            Completion = task;
        }
        internal void Close() { Closing = true; Closure ??= Tunnel.CloseAsync(); }
        public async ValueTask DisposeAsync()
        {
            Gate.TrySetResult(true);
            try { await FinishAsync(this); }
            finally
            {
                // Assertions precede fallback cleanup, so this cannot hide a missed ownership release.
                Tunnel.Dispose();
                if (Stream.Disposals == 0) Stream.Dispose();
                Cancel.Dispose();
            }
        }
    }

    private sealed class TrackingStream : MemoryStream
    {
        internal int Disposals;
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref Disposals);
            base.Dispose(disposing);
        }
    }
}
