using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

internal sealed record StateMachineStep(int Actor, int Command, int Value);

internal sealed record StateMachineTrace(string Scenario, int Seed, StateMachineStep[] Steps)
{
    private readonly Dictionary<string, long> _transitions = new(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, long> Transitions
    {
        get
        {
            lock (_transitions) return new SortedDictionary<string, long>(_transitions, StringComparer.Ordinal);
        }
    }

    public void Cover(string transition)
    {
        ArgumentException.ThrowIfNullOrEmpty(transition);

        lock (_transitions)
        {
            _transitions.TryGetValue(transition, out var count);
            _transitions[transition] = count + 1;
        }
    }
}

internal sealed class StateMachineFailure(string invariant, string detail) : Exception(detail)
{
    public string Invariant { get; } = invariant;
}

internal sealed record StateMachineSettings(string EvidenceDirectory, TimeSpan CaseTimeout, TimeSpan CleanupTimeout);

internal static class StateMachineCampaign
{
    private const int ShrinkAttempts = 64;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<bool> RunAsync(
        string[] args,
        IReadOnlyDictionary<string, Func<StateMachineTrace, CancellationToken, Task>> scenarios,
        StateMachineSettings? settings = null
    )
    {
        if (args.Length == 0 || (args[0] != "--state-machines" && args[0] != "--state-machines-replay")) return false;

        settings = Settings(settings);

        if (scenarios.Count == 0) throw new ArgumentException("At least one scenario is required.", nameof(scenarios));

        if (args[0] == "--state-machines-replay")
        {
            if (args.Length != 2) throw new ArgumentException("Usage: --state-machines-replay artifact.json");

            using var document = JsonDocument.Parse(
                await File
                    .ReadAllTextAsync(args[1])
                    .ConfigureAwait(false)
            );
            StateMachineTrace trace;

            if (document.RootElement.TryGetProperty("Trace", out _))
            {
                var artifact = document.RootElement.Deserialize<Artifact>(JsonOptions)
                    ?? throw new InvalidDataException("Missing state-machine artifact.");
                trace = artifact.Trace;
                Console.WriteLine(
                    $"Replay recorded runtime={artifact.Runtime}, core={artifact.CoreAssembly}, module={artifact.CoreModuleId}, sha256={artifact.CoreSha256}."
                );
            }

            else
            {
                trace = document.RootElement.Deserialize<StateMachineTrace>(JsonOptions)
                    ?? throw new InvalidDataException("Missing raw state-machine trace.");
                Console.WriteLine(
                    $"Replaying raw corpus trace {trace.Scenario}; no historical runtime provenance supplied."
                );
            }

            Validate(trace, scenarios);
            await RunTraceAsync(trace, scenarios[trace.Scenario], false, settings).ConfigureAwait(false);
            return true;
        }

        if (args.Length is < 3 or > 4) throw new ArgumentException("Usage: --state-machines seed cases [scenario]");

        var seed = int.Parse(args[1], CultureInfo.InvariantCulture);
        var cases = int.Parse(args[2], CultureInfo.InvariantCulture);

        if (cases is < 1 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(args), "Cases must be between 1 and 100000.");

        var names = args.Length == 4
            ? new[] { args[3] }
            : [.. scenarios.Keys.OrderBy(name => name, StringComparer.Ordinal)];

        foreach (var name in names)
            if (!scenarios.ContainsKey(name)) throw new ArgumentException("Unknown scenario: " + name, nameof(args));

        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);

        for (var index = 0; index < cases; index++)
        {
            // Each case is independent of how many random values earlier scenarios consume.
            var caseSeed = unchecked(seed + index * 104729);
            var random = new Random(caseSeed);
            var steps = Enumerable
                .Range(0, 48)
                .Select(_ => new StateMachineStep(random.Next(3), random.Next(12), random.Next(64)))
                .ToArray();
            var trace = new StateMachineTrace(names[index % names.Length], caseSeed, steps);
            await RunTraceAsync(trace, scenarios[trace.Scenario], true, settings).ConfigureAwait(false);

            foreach (var transition in trace.Transitions)
            {
                var key = trace.Scenario + ":" + transition.Key;
                totals.TryGetValue(key, out var count);
                totals[key] = count + transition.Value;
            }
        }

        Console.WriteLine($"State machines passed: seed={seed}, cases={cases}, actors=3, steps=48.");
        Console.WriteLine(JsonSerializer.Serialize(totals, JsonOptions));
        return true;
    }

    internal static async Task RunTraceAsync(
        StateMachineTrace trace,
        Func<StateMachineTrace, CancellationToken, Task> scenario,
        bool shrink,
        StateMachineSettings? settings = null
    )
    {
        settings = Settings(settings);
        var failed = false;

        try
        {
            await ExecuteAsync(trace, scenario, settings).ConfigureAwait(false);
        }

        catch (Exception exception)
        {
            failed = true;
            var path = Save(trace, exception, "original", settings);
            Console.Error.WriteLine("State-machine failure retained: " + path);

            if (shrink && exception is StateMachineFailure failure)
            {
                var minimized = await MinimizeAsync(trace, scenario, failure.Invariant, settings).ConfigureAwait(false);
                Console.Error.WriteLine("Minimized trace: " + Save(minimized, failure, "minimized", settings));
            }

            throw;
        }

        finally
        {
            if (failed || !shrink)
                Console.WriteLine(
                    $"Transitions: {trace.Scenario}, seed={trace.Seed}: " + JsonSerializer.Serialize(trace.Transitions)
                );
        }
    }

    private static async Task ExecuteAsync(
        StateMachineTrace trace,
        Func<StateMachineTrace, CancellationToken, Task> scenario,
        StateMachineSettings settings
    )
    {
        using var cancellation = new CancellationTokenSource();
        var operation = Task.Run(() => scenario(trace, cancellation.Token));

        try
        {
            await operation
                .WaitAsync(settings.CaseTimeout)
                .ConfigureAwait(false);
        }

        catch (TimeoutException)
        {
            if (operation.IsCompleted)
            {
                // Observe a fault that raced the timeout, including a timeout
                // thrown by the scenario itself rather than by WaitAsync.
                await operation.ConfigureAwait(false);
                throw;
            }

            var cleanup = Task.WhenAll(operation, cancellation.CancelAsync());

            try
            {
                await cleanup
                    .WaitAsync(settings.CleanupTimeout)
                    .ConfigureAwait(false);
            }

            catch (TimeoutException) when (!cleanup.IsCompleted)
            {
                var failure = new TimeoutException("Scenario did not stop within its deadline and cleanup grace.");
                var path = Save(trace, failure, "unresponsive", settings);
                // An in-process task cannot be forcibly stopped safely. End this test-only
                // harness rather than abandon it and contaminate subsequent scenarios.
                Environment.FailFast("Unresponsive state-machine scenario. Replay: " + path, failure);
                throw;
            }

            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                throw new TimeoutException("Scenario exceeded its deadline; cleanup completed.");
            }

            catch (Exception exception) when (cleanup.IsCompleted)
            {
                throw new TimeoutException("Scenario exceeded its deadline; cleanup faulted.", exception);
            }

            throw new TimeoutException("Scenario exceeded its deadline; cleanup completed.");
        }
    }

    private static async Task<StateMachineTrace> MinimizeAsync(
        StateMachineTrace original,
        Func<StateMachineTrace, CancellationToken, Task> scenario,
        string invariant,
        StateMachineSettings settings
    )
    {
        var current = original;
        var attempts = 0;

        for (var chunk = Math.Max(1, current.Steps.Length / 2); chunk > 0 && attempts < ShrinkAttempts; chunk /= 2)
        {
            for (var start = 0; start < current.Steps.Length && attempts < ShrinkAttempts;)
            {
                var length = Math.Min(chunk, current.Steps.Length - start);
                var steps = current.Steps
                    .Take(start)
                    .Concat(current.Steps.Skip(start + length))
                    .ToArray();
                var candidate = new StateMachineTrace(current.Scenario, current.Seed, steps);
                attempts++;

                try
                {
                    await ExecuteAsync(candidate, scenario, settings).ConfigureAwait(false);
                    start += chunk;
                }

                catch (StateMachineFailure failure) when (failure.Invariant == invariant)
                {
                    current = candidate;
                }

                catch (StateMachineFailure)
                {
                    start += chunk;
                }

                catch (Exception exception)
                {
                    Console.Error.WriteLine(
                        "Unexpected shrink failure retained: "
                            + Save(candidate, exception, "shrink-unexpected", settings)
                    );
                    throw;
                }
            }
        }

        Console.Error.WriteLine(
            $"Shrinking: {attempts} attempts, {original.Steps.Length} -> {current.Steps.Length} steps."
        );
        return current;
    }

    private static void Validate(
        StateMachineTrace trace,
        IReadOnlyDictionary<string, Func<StateMachineTrace, CancellationToken, Task>> scenarios
    )
    {
        if (
            trace == null
            || trace.Scenario == null
            || trace.Steps == null
            || !scenarios.ContainsKey(trace.Scenario)
            || trace.Steps.Length > 48
            || trace.Steps.Any(
                step => step == null
                    || step.Actor is < 0 or > 2 || step.Command is < 0 or > 11 || step.Value is < 0 or > 63
            )
        )
            throw new InvalidDataException("Invalid or unknown state-machine trace.");
    }

    private static StateMachineSettings Settings(StateMachineSettings? settings)
    {
        settings ??= new StateMachineSettings(
            Path.Combine("TestResults", "fuzz", "state-machines"),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(5)
        );

        if (
            string.IsNullOrWhiteSpace(settings.EvidenceDirectory)
            || settings.CaseTimeout <= TimeSpan.Zero
            || settings.CaseTimeout > TimeSpan.FromSeconds(15)
            || settings.CleanupTimeout <= TimeSpan.Zero
            || settings.CleanupTimeout > TimeSpan.FromSeconds(5)
        )
            throw new ArgumentOutOfRangeException(nameof(settings));

        return settings;
    }

    private static string Save(StateMachineTrace trace, Exception exception, string kind, StateMachineSettings settings)
    {
        var directory = Path.GetFullPath(settings.EvidenceDirectory);
        Directory.CreateDirectory(directory);
        var assembly = trace.Scenario.StartsWith("quic-", StringComparison.Ordinal)
            ? typeof(EmbedIO.Quic.QuicFuzzChecks).Assembly
            : typeof(EmbedIO.WebServer).Assembly;
        var artifact = new Artifact(
            trace,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            assembly.FullName ?? string.Empty,
            assembly.ManifestModule.ModuleVersionId,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            exception is StateMachineFailure failure ? failure.Invariant : null,
            exception.ToString(),
            trace.Transitions
        );
        var path = Path.Combine(
            directory,
            kind
                + "-"
                + Guid
                    .NewGuid()
                    .ToString("N")
                + ".json"
        );
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, JsonOptions));
        return path;
    }

    private sealed record Artifact(
        StateMachineTrace Trace,
        string Runtime,
        string OperatingSystem,
        string CoreAssembly,
        Guid CoreModuleId,
        string CoreSha256,
        string? Invariant,
        string Error,
        IReadOnlyDictionary<string, long> Transitions
    );
}
