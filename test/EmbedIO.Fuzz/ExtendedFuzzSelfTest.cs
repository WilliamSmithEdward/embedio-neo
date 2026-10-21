using System;
using System.Threading;
using System.Threading.Tasks;

internal static class ExtendedFuzzSelfTest
{
    internal static async Task<bool> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--extended-state-selftest") return false;

        if (args.Length != 1) throw new ArgumentException("Usage: --extended-state-selftest");

        await LifecycleCleanupSelfTest
            .RunAsync()
            .ConfigureAwait(false);
        var cases = new (string Name, string Invariant, Func<StateMachineTrace, CancellationToken, bool, Task> Run)[]
        {
            ("request-codings", "coding.body-hash", RequestCodingFuzz.RunCoreAsync),
            ("multipart-lifetime", "multipart.independent-body", MultipartLifetimeFuzz.RunCoreAsync),
            ("controller-parity", "controller.expected-body", ControllerParityFuzz.RunCoreAsync),
            ("permanent-bans", "ban.disk-ledger", PermanentBanFuzz.RunCoreAsync),
            (
                "server-lifecycle",
                "lifecycle.response-isolation",
                (trace, token, fault) => LifecycleFuzz.RunCoreAsync(trace, fault, token)
            ),
            ("file-mutation", "files.current-representation", FileMutationFuzz.RunCoreAsync)
        };

        foreach (var (name, invariant, run) in cases)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var caught = false;

            try
            {
                await run(new StateMachineTrace(name, 1729, []), timeout.Token, true).ConfigureAwait(false);
            }

            catch (StateMachineFailure failure) when (failure.Invariant == invariant)
            {
                caught = true;
            }

            if (!caught) throw new StateMachineFailure("extended.negative-oracle", name);

            Console.WriteLine("Rejected injected fault: " + name + " => " + invariant);
        }

        return true;
    }
}
