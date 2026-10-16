using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class StateMachineSelfTest
{
    internal static async Task<bool> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--state-machine-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --state-machine-selftest");
        var root = Path.GetFullPath(Path.Combine("TestResults", "fuzz", "state-machines", "selftest-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        await DeterminismAsync(Settings(root, "determinism")).ConfigureAwait(false);
        await ShrinkAndReplayAsync(Settings(root, "shrink")).ConfigureAwait(false);
        await UnexpectedAsync(Settings(root, "unexpected")).ConfigureAwait(false);
        await CleanupAsync(Settings(root, "cleanup") with { CaseTimeout = TimeSpan.FromMilliseconds(100) }).ConfigureAwait(false);
        Console.WriteLine("State-machine runner self-test passed. Evidence retained: " + root);
        return true;
    }

    private static async Task DeterminismAsync(StateMachineSettings settings)
    {
        var captured = new List<StateMachineTrace>();
        Task Capture(StateMachineTrace trace, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            captured.Add(trace);
            trace.Cover("observed");
            return Task.CompletedTask;
        }

        var scenarios = new Dictionary<string, Func<StateMachineTrace, CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["z"] = Capture,
            ["a"] = Capture,
        };
        var args = new[] { "--state-machines", "1729", "4" };
        await StateMachineCampaign.RunAsync(args, scenarios, settings).ConfigureAwait(false);
        var first = captured.ToArray();
        captured.Clear();
        await StateMachineCampaign.RunAsync(args, scenarios, settings).ConfigureAwait(false);
        Require(first.Length == 4 && captured.Count == 4, "Generated case count changed.");
        for (var index = 0; index < first.Length; index++)
        {
            Require(first[index].Scenario == (index % 2 == 0 ? "a" : "z"), "Scenario order depends on dictionary insertion.");
            Require(first[index].Seed == unchecked(1729 + index * 104729), "Case seeds are not independent.");
            Require(first[index].Steps.Length == 48 && first[index].Steps.All(step => step.Actor is >= 0 and < 3
                && step.Command is >= 0 and < 12 && step.Value is >= 0 and < 64), "Generated steps exceed bounds.");
            Require(JsonSerializer.Serialize(first[index]) == JsonSerializer.Serialize(captured[index]), "Repeated seed changed its trace.");
        }

        var rawPath = Path.Combine(settings.EvidenceDirectory, "raw.json");
        Directory.CreateDirectory(settings.EvidenceDirectory);
        await File.WriteAllTextAsync(rawPath, JsonSerializer.Serialize(first[0])).ConfigureAwait(false);
        captured.Clear();
        await StateMachineCampaign.RunAsync(new[] { "--state-machines-replay", rawPath }, scenarios, settings).ConfigureAwait(false);
        Require(captured.Count == 1 && JsonSerializer.Serialize(captured[0]) == JsonSerializer.Serialize(first[0]), "Raw replay regenerated or changed commands.");
    }

    private static async Task ShrinkAndReplayAsync(StateMachineSettings settings)
    {
        var trace = new StateMachineTrace("shrink", 17, new[]
        {
            new StateMachineStep(0, 1, 1), new StateMachineStep(1, 11, 7), new StateMachineStep(2, 2, 2),
        });
        static Task Fail(StateMachineTrace candidate, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            candidate.Cover("checked");
            throw new StateMachineFailure(candidate.Steps.Any(step => step.Command == 11) ? "target" : "different", "Injected shrink invariant.");
        }

        try
        {
            await StateMachineCampaign.RunTraceAsync(trace, Fail, true, settings).ConfigureAwait(false);
            throw new InvalidOperationException("Injected invariant did not surface.");
        }
        catch (StateMachineFailure failure)
        {
            Require(failure.Invariant == "target", "Shrinking substituted a different invariant.");
        }

        var originalPath = Directory.GetFiles(settings.EvidenceDirectory, "original-*.json").Single();
        var minimizedPath = Directory.GetFiles(settings.EvidenceDirectory, "minimized-*.json").Single();
        using var original = JsonDocument.Parse(await File.ReadAllTextAsync(originalPath).ConfigureAwait(false));
        using var minimized = JsonDocument.Parse(await File.ReadAllTextAsync(minimizedPath).ConfigureAwait(false));
        Require(original.RootElement.GetProperty("Trace").GetProperty("Steps").GetArrayLength() == 3, "Original evidence was overwritten.");
        Require(original.RootElement.GetProperty("CoreSha256").GetString()?.Length == 64, "Binary provenance is missing.");
        var reduced = minimized.RootElement.GetProperty("Trace").Deserialize<StateMachineTrace>()
            ?? throw new InvalidDataException("Missing minimized trace.");
        Require(reduced.Steps.Length == 1 && reduced.Steps[0] == trace.Steps[1]
            && minimized.RootElement.GetProperty("Invariant").GetString() == "target", "Shrinker lost invariant or kept irrelevant steps.");
        StateMachineTrace? replayed = null;
        var scenarios = new Dictionary<string, Func<StateMachineTrace, CancellationToken, Task>>(StringComparer.Ordinal)
        {
            ["shrink"] = (candidate, _) => { replayed = candidate; return Task.CompletedTask; },
        };
        await StateMachineCampaign.RunAsync(new[] { "--state-machines-replay", minimizedPath }, scenarios, settings).ConfigureAwait(false);
        Require(replayed != null && JsonSerializer.Serialize(replayed) == JsonSerializer.Serialize(reduced), "Artifact replay did not retain exact minimized commands.");
    }

    private static async Task UnexpectedAsync(StateMachineSettings settings)
    {
        var failure = new FormatException("Injected unexpected exception.");
        var calls = 0;
        Task Fail(StateMachineTrace _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls++;
            throw failure;
        }

        try
        {
            await StateMachineCampaign.RunTraceAsync(new StateMachineTrace("unexpected", 1, Array.Empty<StateMachineStep>()), Fail, true, settings).ConfigureAwait(false);
            throw new InvalidOperationException("Unexpected exception did not surface.");
        }
        catch (FormatException exception)
        {
            Require(ReferenceEquals(exception, failure), "Unexpected exception was replaced.");
        }

        Require(calls == 1 && Directory.GetFiles(settings.EvidenceDirectory, "original-*.json").Length == 1
            && Directory.GetFiles(settings.EvidenceDirectory, "minimized-*.json").Length == 0, "Unexpected failure was not retained or was shrunk.");
    }

    private static async Task CleanupAsync(StateMachineSettings settings)
    {
        var cleaned = false;
        var callback = false;
        async Task Wait(StateMachineTrace _, CancellationToken token)
        {
            using var registration = token.Register(() => callback = true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            finally
            {
                await Task.Delay(20).ConfigureAwait(false);
                cleaned = true;
            }
        }

        try
        {
            await StateMachineCampaign.RunTraceAsync(new StateMachineTrace("cleanup", 1, Array.Empty<StateMachineStep>()), Wait, true, settings).ConfigureAwait(false);
            throw new InvalidOperationException("Deadline expiry did not fail the scenario.");
        }
        catch (TimeoutException)
        {
            Require(cleaned && callback, "Deadline abandoned the operation or cancellation callback.");
        }

        Require(Directory.GetFiles(settings.EvidenceDirectory, "original-*.json").Length == 1
            && Directory.GetFiles(settings.EvidenceDirectory, "minimized-*.json").Length == 0, "Timeout evidence was missing or incorrectly shrunk.");
    }

    private static StateMachineSettings Settings(string root, string name)
        => new(Path.Combine(root, name), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5));

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("State-machine runner self-test: " + detail);
    }
}
