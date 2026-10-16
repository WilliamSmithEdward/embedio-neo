using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Listener = EmbedIO.Net.HttpListener;

internal static class SharedRoutesFuzz
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type RoutesType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.EndpointRoutes", true)
        ?? throw new InvalidOperationException("Missing endpoint routes.");
    private static readonly Type PrefixType = typeof(EmbedIO.WebServer).Assembly.GetType("EmbedIO.Net.Internal.ListenerPrefix", true)
        ?? throw new InvalidOperationException("Missing listener prefix.");
    private static readonly MethodInfo Add = Method("Add");
    private static readonly MethodInfo Remove = Method("Remove");
    private static readonly MethodInfo Find = Method("Find");
    private static readonly MethodInfo Capture = Method("Capture");

    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        var routes = Activator.CreateInstance(RoutesType, nonPublic: true) ?? throw new InvalidOperationException("Cannot create routes.");
        var owners = new[] { new Listener(), new Listener(), new Listener() };
        var retired = new List<Listener>();
        var ledger = new Dictionary<int, Listener>();
        var snapshots = new List<(object Snapshot, Dictionary<int, Listener> Ledger)>();
        var pending = new List<Task>();
        var releaseGates = new List<TaskCompletionSource<bool>>();
        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var slot = step.Value % 8;
                var owner = owners[step.Actor];
                switch (step.Command)
                {
                    case 0:
                        var inserted = TryAdd(routes, slot, owner);
                        if (ledger.TryGetValue(slot, out var existing))
                            Require(inserted == (existing == owner ? 0 : -1), "registration-conflict", "An existing registration changed ownership or duplicate outcome.");
                        else { Require(inserted == 1, "registration-publication", "A free registration was not published."); ledger.Add(slot, owner); }
                        trace.Cover("routes.add-or-conflict");
                        break;
                    case 1:
                    case 2:
                        var removing = step.Command == 1 ? owner : owners[(step.Actor + 1) % 3];
                        Remove.Invoke(routes, new[] { Prefix(slot), removing });
                        if (ledger.TryGetValue(slot, out existing) && existing == removing) ledger.Remove(slot);
                        trace.Cover("routes.owner-checked-remove");
                        break;
                    case 3:
                        snapshots.Add((Capture.Invoke(routes, null) ?? throw new InvalidOperationException("Missing snapshot."), new Dictionary<int, Listener>(ledger)));
                        // Retain several generations while keeping corpus memory bounded.
                        if (snapshots.Count > 8) snapshots.RemoveAt(0);
                        trace.Cover("routes.capture-generation");
                        break;
                    case 4: trace.Cover("routes.inspect"); break;
                    case 5:
                        for (var i = 0; i < 11; i++)
                        {
                            Remove.Invoke(routes, new[] { Prefix(i), owner });
                            if (ledger.TryGetValue(i, out existing) && existing == owner) ledger.Remove(i);
                        }
                        trace.Cover("routes.retire-owner-registrations");
                        break;
                    case 6:
                        if (!ledger.ContainsValue(owner))
                        {
                            retired.Add(owner);
                            owners[step.Actor] = new Listener();
                            trace.Cover("routes.replace-owner");
                        }
                        else trace.Cover("routes.skip-registered-replacement");
                        break;
                    case 7:
                        if (ledger.ContainsKey(slot)) { trace.Cover("routes.skip-occupied-race"); break; }
                        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        releaseGates.Add(gate);
                        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var waiting = 0;
                        var contenders = new Task<int>[3];
                        for (var i = 0; i < contenders.Length; i++)
                        {
                            var contender = owners[i];
                            contenders[i] = Task.Run(async () =>
                            {
                                if (Interlocked.Increment(ref waiting) == 3) ready.TrySetResult(true);
                                await gate.Task.WaitAsync(token);
                                return TryAdd(routes, slot, contender);
                            }, token);
                            pending.Add(contenders[i]);
                        }
                        await ready.Task.WaitAsync(token);
                        gate.SetResult(true);
                        var results = await Task.WhenAll(contenders).WaitAsync(token);
                        var winner = -1;
                        for (var i = 0; i < results.Length; i++)
                        {
                            if (results[i] == 1) { Require(winner == -1, "single-registration-winner", "Two owners acquired one route."); winner = i; }
                            else Require(results[i] == -1, "conflicting-registration-result", "A losing owner did not receive a conflict.");
                        }
                        Require(winner >= 0, "registration-progress", "No owner acquired a free route.");
                        ledger.Add(slot, owners[winner]);
                        trace.Cover("routes.overlapping-conflicting-publication");
                        break;
                    case 8:
                        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        releaseGates.Add(start);
                        var writersReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var writersWaiting = 0;
                        var writers = new Task<int>[3];
                        for (var i = 0; i < 3; i++)
                        {
                            var independentSlot = 8 + i;
                            foreach (var peer in owners) Remove.Invoke(routes, new[] { Prefix(independentSlot), peer });
                            ledger.Remove(independentSlot);
                            var publishing = owners[i];
                            writers[i] = Task.Run(async () =>
                            {
                                if (Interlocked.Increment(ref writersWaiting) == 3) writersReady.TrySetResult(true);
                                await start.Task.WaitAsync(token);
                                return TryAdd(routes, independentSlot, publishing);
                            }, token);
                            pending.Add(writers[i]);
                        }
                        await writersReady.Task.WaitAsync(token);
                        start.SetResult(true);
                        var published = await Task.WhenAll(writers).WaitAsync(token);
                        for (var i = 0; i < 3; i++) { Require(published[i] == 1, "disjoint-publication", "Independent publication was lost."); ledger.Add(8 + i, owners[i]); }
                        trace.Cover("routes.overlapping-disjoint-publication");
                        break;
                    case 9:
                        foreach (var stale in retired) Remove.Invoke(routes, new[] { Prefix(slot), stale });
                        trace.Cover("routes.stale-owner-removal");
                        break;
                    case 10:
                    case 11: trace.Cover("routes.recheck-generations"); break;
                }
                Verify(routes, Find, ledger);
                foreach (var snapshot in snapshots)
                    Verify(snapshot.Snapshot, snapshot.Snapshot.GetType().GetMethod("Find", Members) ?? throw new InvalidOperationException("Missing snapshot lookup."), snapshot.Ledger);
            }
        }
        finally
        {
            foreach (var gate in releaseGates) gate.TrySetResult(true);
            try
            {
                try { await Task.WhenAll(pending); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            finally
            {
                foreach (var owner in owners) owner.Dispose();
                foreach (var owner in retired) owner.Dispose();
            }
        }
    }

    private static int TryAdd(object routes, int slot, Listener owner)
    {
        try { return (bool)(Add.Invoke(routes, new[] { Prefix(slot), owner }) ?? throw new InvalidOperationException("Missing add result.")) ? 1 : 0; }
        catch (TargetInvocationException error) when (error.InnerException is HttpListenerException) { return -1; }
    }
    private static void Verify(object target, MethodInfo lookup, Dictionary<int, Listener> ledger)
    {
        for (var slot = 0; slot < 12; slot++)
        {
            ledger.TryGetValue(slot, out var expected);
            var actual = lookup.Invoke(target, new object?[] { new Uri($"http://localhost:9999/slot{slot}/payload"), null });
            Require(ReferenceEquals(expected, actual), "route-identity-ledger", $"Slot {slot} resolved to an unexpected owner or generation.");
        }
    }
    private static object Prefix(int slot) => Activator.CreateInstance(PrefixType, $"http://localhost:9999/slot{slot}/") ?? throw new InvalidOperationException("Missing prefix.");
    private static MethodInfo Method(string name) => RoutesType.GetMethod(name, Members) ?? throw new InvalidOperationException("Missing route operation: " + name);
    private static void Require(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }
}
