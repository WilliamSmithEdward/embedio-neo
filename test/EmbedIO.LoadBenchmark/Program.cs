// Entry point: "run" orchestrates a comparison; "server" and "client" are the
// child processes it starts. See README.md for the method and its limits.
var options = new CommandLine(args.Skip(1).ToArray());
return args.FirstOrDefault() switch
{
    "run" => await Orchestrator.RunAsync(options).ConfigureAwait(false),
    "server" => await ServerHost.RunAsync(options).ConfigureAwait(false),
    "client" => await LoadClient.RunAsync(options).ConfigureAwait(false),
    "recovery" => await Recovery.RunAsync(options).ConfigureAwait(false),
    "quic-churn" => await QuicChurn.RunAsync(options).ConfigureAwait(false),
    "burn" => await QuicChurn.BurnAsync(options).ConfigureAwait(false),
    "exception-accounting-check" => await ExceptionAccountingCheck.RunAsync().ConfigureAwait(false),
    _ => Usage(),
};

static int Usage()
{
    Control.Write("Usage: EmbedIO.LoadBenchmark run --output <fresh directory> [--baseline-dir <runner copy with baseline EmbedIO.dll>] [--scenarios all|prefix,...] [--rounds 3] [--warmup 5] [--duration 15] [--server-cpus 0-7] [--client-cpus 8-15] [--profile]");
    return 64;
}
