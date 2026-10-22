using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Quic;

internal static class QuicCoreFuzz
{
    internal static bool Run(string[] args)
    {
        if (args.Length == 0 || args[0] != "--quic-core") return false;

        if (args.Length != 3) throw new ArgumentException("Usage: --quic-core seed cases");

        var seed = int.Parse(args[1], CultureInfo.InvariantCulture);
        var count = int.Parse(args[2], CultureInfo.InvariantCulture);

        if (count is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(args));

        var random = new Random(seed);

        for (var index = 0; index < count; index++)
        {
            var data = new byte[random.Next(1, 513)];
            random.NextBytes(data);
            data[0] = (byte)(index % QuicFuzzChecks.TargetCount);

            try
            {
                QuicFuzzChecks.Check(data);
            }

            catch (Exception)
            {
                var directory = Path.Combine("TestResults", "fuzz", "quic-core");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"seed-{seed}-case-{index}.bin");
                File.WriteAllBytes(path, data);
                Console.Error.WriteLine("QUIC reproducer: " + path);
                throw;
            }
        }

        Console.WriteLine($"QUIC core passed: seed={seed}, cases={count}, targets={QuicFuzzChecks.TargetCount}.");
        return true;
    }

    internal static Task RecoveryAsync(StateMachineTrace trace, CancellationToken cancellationToken) =>
        Execute(trace, false, cancellationToken);

    internal static Task LifecycleAsync(StateMachineTrace trace, CancellationToken cancellationToken) =>
        Execute(trace, true, cancellationToken);

    private static Task Execute(StateMachineTrace trace, bool lifecycle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var steps = trace.Steps
            .SelectMany(step => new[]
            {
                (byte)step.Actor,
                (byte)step.Command,
                (byte)step.Value
            })
            .ToArray();

        try
        {
            if (lifecycle) QuicFuzzChecks.Lifecycle(steps, trace.Cover);

            else QuicFuzzChecks.StateMachine(steps, trace.Cover);
        }

        catch (QuicFuzzFailure failure)
        {
            throw new StateMachineFailure(failure.Invariant, failure.Message);
        }

        return Task.CompletedTask;
    }
}
