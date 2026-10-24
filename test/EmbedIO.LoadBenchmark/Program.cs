// Entry point: "run" orchestrates a comparison; "server" and "client" are the
// child processes it starts. See README.md for the method and its limits.
var options = new CommandLine(args.Skip(1).ToArray());
return args.FirstOrDefault() switch
{
    "run" => await Orchestrator.RunAsync(options).ConfigureAwait(false),
    "idle" => await IdleConnections.RunAsync(options).ConfigureAwait(false),
    "server" => await ServerHost.RunAsync(options).ConfigureAwait(false),
    "client" => await LoadClient.RunAsync(options).ConfigureAwait(false),
    "recovery" => await Recovery.RunAsync(options).ConfigureAwait(false),
    "cold" => await ColdStart.RunAsync(options).ConfigureAwait(false),
    "cold-client" => await ColdStart.ClientAsync(options).ConfigureAwait(false),
    "endurance" => await Endurance.RunAsync(options).ConfigureAwait(false),
    "endurance-server" => await EnduranceServer.RunAsync(options).ConfigureAwait(false),
    "endurance-client" => await EnduranceClient.RunAsync(options).ConfigureAwait(false),
    "endurance-verdict-check" => EnduranceVerdictCheck.Run(),
    "endurance-profile-check" => EnduranceProfileCheck.Run(),
    "native-heap-census-check" => NativeHeaps.CheckCounter(),
    "quic-churn" => await QuicChurn.RunAsync(options).ConfigureAwait(false),
    "burn" => await QuicChurn.BurnAsync(options).ConfigureAwait(false),
    "teardown-accounting-check" => await TeardownAccountingCheck.RunAsync().ConfigureAwait(false),
    "load-failure-check" => await LoadFailureCheck.RunAsync().ConfigureAwait(false),
    "exception-accounting-check" => await ExceptionAccountingCheck.RunAsync().ConfigureAwait(false),
    _ => Usage(),
};

static int Usage()
{
    Control.Write("Usage: EmbedIO.LoadBenchmark run --output <fresh directory> [--baseline-dir <runner copy with baseline EmbedIO.dll>] [--scenarios all|prefix,...] [--rounds 3] [--warmup 5] [--duration 15] [--server-cpus 0-7] [--client-cpus 8-15] [--profile]");
    return 64;
}
