using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

// Multi-connection ownership model. The oracle is a list of live messages, not
// a second implementation of the production counters or its locking scheme.
internal static class SharedBudgetFuzz
{
    private const BindingFlags Hidden = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const int ConnectionMessages = 3, ModuleMessages = 5;
    private const long ConnectionBytes = 24, ModuleBytes = 48;
    private static readonly Assembly Core = typeof(EmbedIO.WebServer).Assembly;
    private static readonly Type BudgetType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocketDeliveryBudget", true)
        ?? throw new InvalidOperationException("Missing WebSocket budget.");
    private static readonly Type LimitsType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocketDeliveryLimits", true)
        ?? throw new InvalidOperationException("Missing WebSocket limits.");

    private enum Phase { Assembling, Queued, Delivering, Released }
    private sealed class Connection(object actual)
    {
        internal object Actual { get; } = actual;
        internal bool Stopped { get; set; }
    }

    private sealed class Message(object actual, Connection owner)
    {
        internal object Actual { get; } = actual;
        internal Connection Owner { get; } = owner;
        internal Phase State { get; set; }
        internal long Bytes { get; set; }
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Delivery { get; set; }
        internal int Outcome { get; set; }
        internal int CallbackCount { get; set; }
    }

    private sealed class CallbackFailure : Exception { }

    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var limits = Activator.CreateInstance(LimitsType, Hidden, null,
            new object[] { ConnectionMessages, ConnectionBytes, ModuleMessages, ModuleBytes }, null)
            ?? throw new InvalidOperationException("Missing limits constructor.");
        var budget = Activator.CreateInstance(BudgetType, Hidden, null, new[] { limits }, null)
            ?? throw new InvalidOperationException("Missing budget constructor.");
        var generations = new List<Connection>();
        var actors = Enumerable.Range(0, 3).Select(_ => CreateConnection(budget, generations)).ToArray();
        var slots = new Message?[12];
        var messages = new List<Message>();
        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = actors[step.Actor];
                var index = step.Actor * 4 + step.Value % 4;
                var message = slots[index];
                switch (step.Command)
                {
                    case 0:
                    case 1:
                        if (message != null && message.State != Phase.Released) { trace.Cover("budget.skip.occupied"); break; }
                        var expected = !actor.Stopped && Count(messages, actor) < ConnectionMessages && Count(messages) < ModuleMessages;
                        var args = new object?[] { null };
                        var accepted = (bool)(Invoke(actor.Actual, "TryReserve", args) ?? throw new InvalidOperationException("Missing reserve result."));
                        Check(accepted == expected, "budget.admission", $"Expected {expected}, got {accepted}.");
                        Check(accepted == (args[0] != null), "budget.lease-result", "Reservation result and lease disagree.");
                        if (args[0] is { } lease)
                        {
                            message = new Message(lease, actor);
                            slots[index] = message;
                            messages.Add(message);
                        }
                        trace.Cover(accepted ? "budget.reserve.accepted" : "budget.reserve.rejected");
                        break;
                    case 2:
                        if (message == null || message.State != Phase.Assembling) { trace.Cover("budget.skip.grow"); break; }
                        var grow = (long)step.Value;
                        expected = Bytes(messages, message.Owner) + grow <= ConnectionBytes && Bytes(messages) + grow <= ModuleBytes;
                        accepted = (bool)(Invoke(message.Actual, "TryGrow", grow) ?? throw new InvalidOperationException("Missing growth result."));
                        Check(accepted == expected, "budget.growth", $"Expected {expected} for {grow} bytes, got {accepted}.");
                        if (accepted) message.Bytes += grow;
                        trace.Cover(accepted ? "budget.grow.accepted" : "budget.grow.rejected");
                        break;
                    case 3:
                        if (message == null || message.State != Phase.Assembling) { trace.Cover("budget.skip.queue"); break; }
                        Invoke(message.Actual, "CompleteMessage");
                        message.State = Phase.Queued;
                        trace.Cover("budget.queued");
                        break;
                    case 4:
                        if (message == null || message.State != Phase.Queued) { trace.Cover("budget.skip.deliver"); break; }
                        Begin(message, step.Value % 3);
                        trace.Cover("budget.delivery.started");
                        break;
                    case 5:
                        if (message == null || message.State != Phase.Delivering) { trace.Cover("budget.skip.release"); break; }
                        await Finish(message).ConfigureAwait(false);
                        trace.Cover("budget.delivery.finished." + message.Outcome);
                        break;
                    case 6:
                        if (message == null) { trace.Cover("budget.skip.dispose"); break; }
                        ((IDisposable)message.Actual).Dispose();
                        if (message.State != Phase.Delivering) message.State = Phase.Released;
                        trace.Cover("budget.dispose." + message.State);
                        break;
                    case 7:
                        Invoke(actor.Actual, "StopAdmission");
                        actor.Stopped = true;
                        trace.Cover("budget.connection.stopped");
                        break;
                    case 8:
                        Invoke(actor.Actual, "StopAdmission");
                        actor.Stopped = true;
                        actors[step.Actor] = CreateConnection(budget, generations);
                        trace.Cover("budget.connection.recreated");
                        break;
                    case 9:
                        if (message == null) { trace.Cover("budget.skip.invalid-grow"); break; }
                        if (message.State == Phase.Assembling)
                            Reject<ArgumentOutOfRangeException>(() => Invoke(message.Actual, "TryGrow", -1L));
                        else Reject<InvalidOperationException>(() => Invoke(message.Actual, "TryGrow", 0L));
                        trace.Cover("budget.invalid-growth.rejected");
                        break;
                    case 10:
                        if (message == null || message.State == Phase.Assembling) { trace.Cover("budget.skip.invalid-queue"); break; }
                        Reject<InvalidOperationException>(() => Invoke(message.Actual, "CompleteMessage"));
                        trace.Cover("budget.invalid-queue.rejected");
                        break;
                    case 11:
                        foreach (var queued in messages.Where(item => item.State == Phase.Queued)) Begin(queued, step.Value % 3);
                        trace.Cover("budget.deliver-all");
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(trace), "Unknown budget command.");
                }
                var active = messages.Where(item => item.State == Phase.Delivering).ToArray();
                foreach (var delivery in active)
                    Check(delivery.CallbackCount == 1 && delivery.Delivery is { IsCompleted: false }, "budget.callback-barrier", "A gated callback was not alive exactly once.");
                if (active.Select(item => item.Owner).Distinct().Count() > 1) trace.Cover("budget.callbacks.overlap");
                Verify(budget, generations, messages);
            }
        }
        finally
        {
            // Release every callback before inspecting the final ledger, including
            // leases on replaced connection generations and after cancellation.
            foreach (var message in messages) message.Release.TrySetResult();
            await Task.WhenAll(messages.Select(async message =>
            {
                try { if (message.Delivery != null) await Finish(message).ConfigureAwait(false); }
                finally
                {
                    ((IDisposable)message.Actual).Dispose();
                    message.State = Phase.Released;
                }
            })).ConfigureAwait(false);
            Verify(budget, generations, messages);
        }
    }

    private static Connection CreateConnection(object budget, List<Connection> generations)
    {
        var connection = new Connection(Invoke(budget, "CreateConnection") ?? throw new InvalidOperationException("Missing connection."));
        generations.Add(connection);
        return connection;
    }

    private static void Begin(Message message, int outcome)
    {
        message.Outcome = outcome;
        message.State = Phase.Delivering;
        message.Delivery = (Task)(Invoke(message.Actual, "DeliverAsync", (Func<Task>)(async () =>
        {
            message.CallbackCount++;
            await message.Release.Task.ConfigureAwait(false);
            if (outcome == 1) throw new CallbackFailure();
            if (outcome == 2) throw new OperationCanceledException(new CancellationToken(true));
        })) ?? throw new InvalidOperationException("Missing delivery task."));
    }

    private static async Task Finish(Message message)
    {
        message.Release.TrySetResult();
        var outcome = 0;
        try { await (message.Delivery ?? throw new InvalidOperationException("Missing pending delivery.")).ConfigureAwait(false); }
        catch (CallbackFailure) { outcome = 1; }
        catch (OperationCanceledException) when (message.Outcome == 2) { outcome = 2; }
        Check(outcome == message.Outcome, "budget.callback-result", $"Expected outcome {message.Outcome}, got {outcome}.");
        Check(message.CallbackCount == 1, "budget.callback-once", "A message callback ran more than once.");
        message.State = Phase.Released;
    }

    private static long Count(IEnumerable<Message> messages, Connection? owner = null)
        => messages.LongCount(item => item.State != Phase.Released && (owner == null || ReferenceEquals(item.Owner, owner)));

    private static long Bytes(IEnumerable<Message> messages, Connection? owner = null)
        => messages.Where(item => item.State != Phase.Released && (owner == null || ReferenceEquals(item.Owner, owner))).Sum(item => item.Bytes);

    private static void Verify(object budget, List<Connection> connections, List<Message> messages)
    {
        VerifySnapshot(budget, Count(messages), Bytes(messages));
        foreach (var connection in connections) VerifySnapshot(connection.Actual, Count(messages, connection), Bytes(messages, connection));
    }

    private static void VerifySnapshot(object owner, long count, long bytes)
    {
        var snapshot = Property(owner, "Outstanding");
        var actualCount = (long)Property(snapshot, "Messages");
        var actualBytes = (long)Property(snapshot, "Bytes");
        Check(actualCount == count && actualBytes == bytes, "budget.ledger", $"Expected ({count},{bytes}), actual ({actualCount},{actualBytes}).");
    }

    private static object Property(object owner, string name)
        => owner.GetType().GetProperty(name, Hidden)?.GetValue(owner) ?? throw new InvalidOperationException("Missing budget property: " + name);

    private static object? Invoke(object owner, string name, params object?[] args)
    {
        var method = owner.GetType().GetMethod(name, Hidden) ?? throw new InvalidOperationException("Missing budget method: " + name);
        try { return method.Invoke(owner, args); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new StateMachineFailure("budget.invalid-transition", "An invalid lease transition was accepted.");
    }

    private static void Check(bool condition, string invariant, string detail)
    {
        if (!condition) throw new StateMachineFailure(invariant, detail);
    }
}
