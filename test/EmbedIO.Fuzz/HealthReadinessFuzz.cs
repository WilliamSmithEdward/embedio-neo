using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Health;
using EmbedIO.Testing;

// An independent ledger of three hosts and their dependency states. No expected value is computed by the service.
internal static class HealthReadinessFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--health-readiness-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --health-readiness-selftest");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("health-readiness", 1729, Array.Empty<StateMachineStep>()), timeout.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "health.owner-isolation")
        { Console.WriteLine("Rejected injected health owner corruption: " + failure.Invariant); return true; }
        throw new StateMachineFailure("health.negative-oracle", "Injected owner corruption was not detected.");
    }
    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var hosts = Enumerable.Range(0, 3).Select(actor => new Host(actor, 0, token, injectFault && actor == 0)).ToArray();
        var generations = new int[3];
        try
        {
            // Mandatory overlapping dependency evaluations and cancellation of just one waiter.
            foreach (var host in hosts) host.Hold();
            var probes = hosts.Select(host => host.Probe()).ToArray();
            await Task.WhenAll(hosts.Select(host => host.Entered.Task.WaitAsync(token)));
            using (var canceled = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                var waiter = hosts[0].Service.CheckReadinessAsync(canceled.Token); canceled.Cancel();
                await Canceled(waiter); trace.Cover("health.waiter-cancellation-isolation");
            }
            foreach (var host in hosts) host.Release();
            await Task.WhenAll(probes);
            Require(hosts.All(host => host.Calls == 1), "health.overlap-single-execution");
            trace.Cover("health.three-overlapping-hosts");
            // Stopping one owner must not make another owner's readiness unavailable.
            await hosts[0].Stop();
            Require((await hosts[0].Service.CheckReadinessAsync(token)).Status == HealthStatus.Unhealthy, "health.stop-fails-closed");
            await hosts[1].Probe(); await hosts[2].Probe();
            await hosts[0].DisposeAsync(); hosts[0] = new Host(0, ++generations[0], token, false);
            trace.Cover("health.stop-and-replace-owner");
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var host = hosts[step.Actor];
                switch (step.Command % 6)
                {
                    case 0:
                        host.State = step.Value % 4; await host.Probe(); trace.Cover("health.dependency-state-" + host.State); break;
                    case 1:
                        await host.Probe(); trace.Cover("health.no-stale-result"); break;
                    case 2:
                        var before = host.Calls;
                        using (var live = await host.Server.Client.GetAsync("/health/live", token)) Require(live.StatusCode == HttpStatusCode.OK, "health.live-independent-of-dependencies");
                        Require(host.Calls == before, "health.live-does-not-run-checks"); trace.Cover("health.liveness"); break;
                    case 3:
                        host.Hold(); var group = Enumerable.Range(0, 3).Select(_ => host.Probe()).ToArray();
                        await host.Entered.Task.WaitAsync(token); host.Release(); await Task.WhenAll(group);
                        trace.Cover("health.concurrent-probes"); break;
                    case 4:
                        host.Service.Dispose();
                        Require((await host.Service.CheckReadinessAsync(token)).Status == HealthStatus.Unhealthy, "health.disposal-fails-closed");
                        await host.DisposeAsync(); hosts[step.Actor] = new Host(step.Actor, ++generations[step.Actor], token, false);
                        await hosts[step.Actor].Probe(); trace.Cover("health.dispose-and-replace-owner"); break;
                    default:
                        using (var cancellation = new CancellationTokenSource())
                        {
                            cancellation.Cancel(); await Canceled(host.Service.CheckReadinessAsync(cancellation.Token));
                        }
                        await host.Probe(); trace.Cover("health.pre-canceled-probe"); break;
                }
            }
        }
        finally { foreach (var host in hosts) await host.DisposeAsync(); }
    }
    private static async Task Canceled(Task task)
    {
        var canceled = false; try { await task; } catch (OperationCanceledException) { canceled = true; }
        Require(canceled, "health.cancellation-observed");
    }
    private static void Require(bool condition, string invariant)
    { if (!condition) throw new StateMachineFailure(invariant, "Health result differs from the independent host/dependency ledger."); }
    private sealed class Host : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly CancellationToken _token;
        private readonly string _identity;
        private readonly Task _running;
        private bool _disposed;
        private TaskCompletionSource<bool>? _held;
        internal TaskCompletionSource<bool> Entered { get; private set; } = NewSignal();
        internal TestWebServer Server { get; } = new TestWebServer();
        internal HealthCheckService Service { get; }
        internal int State;
        internal int Calls;
        internal Host(int actor, int generation, CancellationToken token, bool injectFault)
        {
            _identity = "owner-" + actor + "-" + generation; _token = token;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            Service = new HealthCheckService(Server, new[] { new HealthCheckRegistration("dependency", async checkToken =>
            {
                Interlocked.Increment(ref Calls); Entered.TrySetResult(true);
                var hold = _held; if (hold != null) await hold.Task.WaitAsync(checkToken);
                var state = Volatile.Read(ref State);
                if (state == 3) throw new InvalidOperationException("private failure");
                return new HealthCheckResult(state == 0 ? HealthStatus.Healthy : state == 1 ? HealthStatus.Unhealthy : HealthStatus.Degraded,
                    injectFault ? "another-owner" : _identity);
            }) });
            Server.WithModule(new HealthCheckModule(Service, includeDetails: true, authorize: _ => Task.FromResult(true)));
            _running = Server.RunAsync(_stop.Token);
        }
        internal void Hold() { Entered = NewSignal(); _held = NewSignal(); }
        internal void Release() { _held?.TrySetResult(true); _held = null; }
        internal async Task Probe()
        {
            using var response = await Server.Client.GetAsync("/health/ready", _token);
            var expected = State == 0 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            Require(response.StatusCode == expected, "health.expected-admission");
            Require(response.Headers.CacheControl?.NoStore == true, "health.no-http-cache");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_token));
            var expectedStatus = State == 0 ? "Healthy" : State == 2 ? "Degraded" : "Unhealthy";
            Require(json.RootElement.GetProperty("status").GetString() == expectedStatus, "health.expected-status");
            var result = json.RootElement.GetProperty("checks").GetProperty("dependency");
            if (State != 3) Require(result.GetProperty("description").GetString() == _identity, "health.owner-isolation");
            else Require(!json.RootElement.ToString().Contains("private failure", StringComparison.Ordinal), "health.exception-redaction");
        }
        internal async Task Stop() { _stop.Cancel(); await _running; }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            Release(); Service.Dispose();
            if (!_stop.IsCancellationRequested) _stop.Cancel();
            await _running; Server.Dispose(); _stop.Dispose();
        }
        private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
