using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

internal static class EventHubFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--event-hub-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --event-hub-selftest");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("event-hub", 1729, Array.Empty<StateMachineStep>()), timeout.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "events.independent-payloads")
        { Console.WriteLine("Rejected injected SSE payload corruption: " + failure.Invariant); return true; }
        throw new StateMachineFailure("events.negative-oracle", "Injected payload corruption was not detected.");
    }
    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        using var hub = new ServerSentEventHub(new ServerSentEventHubOptions(maxEventBytes: 128, maxReplayEvents: 8,
            maxReplayBytes: 1024, maxSubscribers: 3, maxQueuedEvents: 4, maxQueuedBytes: 512));
        var subscriptions = new ServerSentEventSubscription?[3];
        var queues = Enumerable.Range(0, 3).Select(_ => new Queue<(string Id, string Data)>()).ToArray();
        var overflow = new bool[3];
        var replay = new List<(string Id, string Data)>();
        try
        {
            for (var actor = 0; actor < 3; actor++) subscriptions[actor] = hub.Subscribe();
            // Three overlapping publishers and subscribers: independent known payload set,
            // unique IDs, and identical ordering for every subscriber.
            var reading = Enumerable.Range(0, 3).Select(async actor =>
            {
                var items = new List<ServerSentEvent>();
                for (var i = 0; i < 3; i++) items.Add(await Required(actor));
                return items;
            }).ToArray();
            var initial = await Task.WhenAll(Enumerable.Range(0, 3).Select(actor => Task.Run(() =>
            {
                var data = "initial-" + actor;
                return (Id: hub.Publish(injectFault && actor == 0 ? "corrupted" : data), Data: data);
            }, token)));
            var delivered = await Task.WhenAll(reading);
            string[]? order = null;
            for (var actor = 0; actor < 3; actor++)
            {
                var items = delivered[actor];
                Require(items.Select(item => item.Data).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(initial.Select(item => item.Data).OrderBy(x => x, StringComparer.Ordinal)), "events.independent-payloads");
                Require(items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == 3
                    && items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal).SetEquals(initial.Select(item => item.Id)), "events.unique-ids");
                var ids = items.Select(item => item.Id).ToArray();
                if (order != null) Require(ids.SequenceEqual(order), "events.broadcast-order");
                else { order = ids; foreach (var item in items) replay.Add((item.Id, item.Data)); }
            }
            trace.Cover("events.three-publishers-three-subscribers");
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = step.Actor;
                switch (step.Command % 6)
                {
                    case 0:
                        var data = "value-" + step.Value + "\n\u00e9\ud83d\ude00";
                        var id = hub.Publish(data, "update"); replay.Add((id, data));
                        if (replay.Count > 8) replay.RemoveAt(0);
                        for (var owner = 0; owner < 3; owner++)
                        {
                            if (subscriptions[owner] == null || overflow[owner]) continue;
                            if (queues[owner].Count == 4) { overflow[owner] = true; queues[owner].Clear(); trace.Cover("events.queue-overflow"); }
                            else queues[owner].Enqueue((id, data));
                        }
                        trace.Cover("events.publish"); break;
                    case 1:
                        if (subscriptions[actor] == null) break;
                        if (overflow[actor])
                        {
                            var caught = false;
                            try { await Required(actor); } catch (IOException) { caught = true; }
                            Require(caught, "events.overflow-not-hidden");
                        }
                        else if (queues[actor].Count > 0)
                        {
                            var expected = queues[actor].Dequeue(); var actual = await Required(actor);
                            Require(actual.Id == expected.Id && actual.Data == expected.Data, "events.queue-ledger");
                        }
                        trace.Cover("events.read"); break;
                    case 2:
                        subscriptions[actor]?.Dispose(); queues[actor].Clear(); overflow[actor] = false;
                        var cursor = replay[Math.Max(0, replay.Count - 3)];
                        subscriptions[actor] = hub.Subscribe(cursor.Id);
                        foreach (var item in replay.Skip(replay.IndexOf(cursor) + 1)) queues[actor].Enqueue(item);
                        trace.Cover("events.replay-to-live"); break;
                    case 3:
                        if (subscriptions[actor] != null && !overflow[actor] && queues[actor].Count == 0)
                        {
                            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token); canceled.Cancel();
                            var caught = false;
                            try { await (subscriptions[actor] ?? throw new InvalidOperationException()).ReadAsync(canceled.Token); }
                            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { caught = true; }
                            Require(caught, "events.read-cancellation");
                        }
                        trace.Cover("events.cancel"); break;
                    case 4:
                        subscriptions[actor]?.Dispose(); subscriptions[actor] = null; queues[actor].Clear(); overflow[actor] = false;
                        trace.Cover("events.dispose"); break;
                    default:
                        subscriptions[actor]?.Dispose(); subscriptions[actor] = null; queues[actor].Clear(); overflow[actor] = false;
                        var rejected = false;
                        try { using var invalid = hub.Subscribe("foreign-epoch"); }
                        catch (HttpException error) when (error.StatusCode == 409) { rejected = true; }
                        Require(rejected, "events.replay-gap-not-hidden");
                        trace.Cover("events.replay-gap"); break;
                }
                Require(hub.SubscriberCount == subscriptions.Count(item => item != null), "events.subscription-ledger");
            }
            hub.Complete();
            for (var actor = 0; actor < 3; actor++)
            {
                if (subscriptions[actor] == null || overflow[actor]) continue;
                while (queues[actor].Count > 0)
                {
                    var expected = queues[actor].Dequeue(); var actual = await Required(actor);
                    Require(actual.Id == expected.Id && actual.Data == expected.Data, "events.completion-drain");
                }
                Require(await (subscriptions[actor] ?? throw new InvalidOperationException()).ReadAsync(token) == null, "events.completion-end");
            }
        }
        finally { foreach (var subscription in subscriptions) subscription?.Dispose(); Require(hub.SubscriberCount == 0, "events.cleanup-ledger"); }
        async Task<ServerSentEvent> Required(int actor) => await (subscriptions[actor] ?? throw new InvalidOperationException()).ReadAsync(token) ?? throw new StateMachineFailure("events.unexpected-end", actor.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static void Require(bool condition, string invariant) { if (!condition) throw new StateMachineFailure(invariant, "Independent SSE channel ledger mismatch."); }
}
