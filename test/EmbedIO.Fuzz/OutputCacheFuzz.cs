using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Caching;
using EmbedIO.Testing;

// The expected snapshots live in an independent ledger, never read from cache contents.
internal static class OutputCacheFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task<bool> SelfTestAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--output-cache-selftest") return false;
        if (args.Length != 1) throw new ArgumentException("Usage: --output-cache-selftest");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await RunCoreAsync(new StateMachineTrace("output-cache", 1729, Array.Empty<StateMachineStep>()), timeout.Token, true); }
        catch (StateMachineFailure failure) when (failure.Invariant == "cache.snapshot-isolation")
        { Console.WriteLine("Rejected injected stale/foreign output: " + failure.Invariant); return true; }
        throw new StateMachineFailure("cache.negative-oracle", "Injected response corruption was not detected.");
    }

    private static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var hosts = Enumerable.Range(0, 3).Select(owner => new Host(owner, token, injectFault && owner == 0)).ToArray();
        try
        {
            // Mandatory overlap: invalidate a paused old fill, then verify no stale repopulation.
            var host = hosts[0]; host.Hold();
            var old = host.Read(0, expected: host.Value(0));
            await host.Entered.Task.WaitAsync(token);
            var waiting = host.Read(0, expected: "owner=0;epoch=1;variant=0");
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var canceled = host.Server.Client.GetAsync("/value?variant=0", cancellation.Token);
            cancellation.Cancel();
            try { using var ignored = await canceled; throw new StateMachineFailure("cache.waiter-canceled", "Canceled waiter completed."); }
            catch (OperationCanceledException) { trace.Cover("cache.waiter-cancellation-isolation"); }
            host.Epoch++; host.Cache.InvalidateTag("data"); host.Release();
            await Task.WhenAll(old, waiting); host.Ledger[0] = host.Value(0);
            Require(host.Calls == 2, "cache.invalidated-fill-refilled-once"); trace.Cover("cache.invalidation-during-fill");
            // Three independent owners may have equal paths, policies and variants.
            await hosts[1].Probe(0); await hosts[2].Probe(0); trace.Cover("cache.three-owner-isolation");
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested(); host = hosts[step.Actor]; var variant = step.Value % 3;
                switch (step.Command % 7)
                {
                    case 0: await host.Probe(variant); trace.Cover("cache.lookup"); break;
                    case 1: host.Epoch++; await host.Probe(variant); trace.Cover("cache.snapshot-until-invalidation"); break;
                    case 2: host.Epoch++; host.Cache.InvalidateTag("data"); host.Ledger.Clear(); await host.Probe(variant); trace.Cover("cache.tag-invalidation"); break;
                    case 3: host.Cache.Clear(); host.Ledger.Clear(); await host.Probe(variant); trace.Cover("cache.clear"); break;
                    case 4:
                        var expected = host.Ledger.TryGetValue(variant, out var snapshot) ? snapshot : host.Value(variant);
                        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => host.Read(variant, expected)));
                        host.Ledger[variant] = expected; trace.Cover("cache.concurrent-lookups"); break;
                    case 5: await host.Read(variant, host.Value(variant), credentials: true); trace.Cover("cache.credentials-bypass"); break;
                    default: await host.Read(variant, host.Value(variant), refresh: true); trace.Cover("cache.request-no-cache-bypass"); break;
                }
                Require(host.Cache.Count <= 3 && host.Cache.SizeBytes <= 4096, "cache.storage-bounds");
            }
        }
        finally { foreach (var host in hosts) await host.DisposeAsync(); }
    }

    private static void Require(bool valid, string invariant)
    { if (!valid) throw new StateMachineFailure(invariant, "Output cache violated " + invariant); }

    private sealed class Host : IAsyncDisposable
    {
        private readonly int _owner;
        private readonly CancellationToken _token;
        private readonly CancellationTokenSource _stop;
        private readonly Task _running;
        private TaskCompletionSource? _held;
        internal readonly OutputCache Cache = new(maxEntries: 3, maxBytes: 4096, maxResponseBytes: 128);
        internal readonly TestWebServer Server = new();
        internal readonly Dictionary<int, string> Ledger = new();
        internal TaskCompletionSource Entered = Signal();
        internal int Epoch;
        internal int Calls;
        internal Host(int owner, CancellationToken token, bool corrupt)
        {
            _owner = owner; _token = token; _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var policy = new OutputCachePolicy(TimeSpan.FromMinutes(1), tags: new[] { "data" });
            Server.WithModule(new ActionModule(c => Cache.ExecuteAsync(c, policy, async captured =>
            {
                Interlocked.Increment(ref Calls);
                var variant = int.Parse(captured.Request.QueryString["variant"] ?? "0", CultureInfo.InvariantCulture);
                var value = corrupt ? "foreign-owner" : Value(variant); var held = _held;
                Entered.TrySetResult(); if (held != null) await held.Task.WaitAsync(token);
                var bytes = Encoding.UTF8.GetBytes(value); captured.Response.ContentType = "text/plain";
                captured.Response.ContentLength64 = bytes.Length;
                await captured.Response.OutputStream.WriteAsync(bytes, token);
            })));
            _running = Server.RunAsync(_stop.Token);
        }
        internal string Value(int variant) => FormattableString.Invariant($"owner={_owner};epoch={Epoch};variant={variant}");
        internal void Hold() { Entered = Signal(); _held = Signal(); }
        internal void Release() { _held?.TrySetResult(); _held = null; }
        internal async Task Probe(int variant)
        {
            var expected = Ledger.TryGetValue(variant, out var snapshot) ? snapshot : Value(variant);
            await Read(variant, expected); Ledger[variant] = expected;
        }
        internal async Task Read(int variant, string expected, bool credentials = false, bool refresh = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/value?variant=" + variant.ToString(CultureInfo.InvariantCulture));
            if (credentials) request.Headers.TryAddWithoutValidation("Authorization", "Bearer unused");
            if (refresh) request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            using var response = await Server.Client.SendAsync(request, _token);
            Require(response.StatusCode == HttpStatusCode.OK, "cache.successful-response");
            Require(await response.Content.ReadAsStringAsync(_token) == expected, "cache.snapshot-isolation");
        }
        public async ValueTask DisposeAsync()
        { Release(); _stop.Cancel(); await _running; Server.Dispose(); _stop.Dispose(); }
        private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
