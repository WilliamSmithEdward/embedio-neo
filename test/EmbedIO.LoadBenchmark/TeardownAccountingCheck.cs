using System.Diagnostics.Metrics;
using System.Text.Json;

internal static class TeardownAccountingCheck
{
    internal static async Task<int> RunAsync()
    {
        using var ledger = new ConnectionLedger();
        ledger.Start();
        using var meter = new Meter("EmbedIO.Server");
        var counter = meter.CreateUpDownCounter<long>("embedio.server.active_connections");
        counter.Add(1);
        counter.Add(1);
        counter.Add(-1);
        using var snapshot = JsonDocument.Parse(JsonSerializer.Serialize(ledger.Snapshot()));
        if (snapshot.RootElement.GetProperty("opened").GetInt64() != 2
            || snapshot.RootElement.GetProperty("closed").GetInt64() != 1
            || snapshot.RootElement.GetProperty("active").GetInt64() != 1) return 1;
        using var other = new Meter("other");
        other.CreateUpDownCounter<long>("embedio.server.active_connections").Add(1);
        counter.Add(-1);
        using var closed = JsonDocument.Parse(JsonSerializer.Serialize(ledger.Snapshot()));
        if (closed.RootElement.GetProperty("active").GetInt64() != 0
            || !closed.RootElement.GetProperty("instrumentSeen").GetBoolean()) return 2;
        counter.Add(2);
        using var invalid = JsonDocument.Parse(JsonSerializer.Serialize(ledger.Snapshot()));
        if (invalid.RootElement.GetProperty("invalidMeasurements").GetInt64() != 1) return 3;
        using var kestrel = new Meter("Microsoft.AspNetCore.Server.Kestrel");
        var kestrelCounter = kestrel.CreateUpDownCounter<long>("kestrel.active_connections");
        kestrelCounter.Add(1);
        kestrelCounter.Add(-1);
        using var both = JsonDocument.Parse(JsonSerializer.Serialize(ledger.Snapshot()));
        if (both.RootElement.GetProperty("opened").GetInt64() != 3
            || both.RootElement.GetProperty("closed").GetInt64() != 3) return 4;
        using var direct = new ConnectionLedger(true);
        direct.Start();
        direct.ConfigureDelegates();
        kestrelCounter.Add(1);
        var lifetime = direct.BeginLifetime();
        lifetime.Dispose();
        lifetime.Dispose();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = completion.Task;
        var observed = direct.TrackAsync(() => original);
        if (!ReferenceEquals(original, observed) || await direct.WaitForCompletionAsync(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false)) return 14;
        completion.SetResult();
        await observed.ConfigureAwait(false);
        if (!await direct.WaitForCompletionAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false)) return 15;
        var failure = Task.FromException(new IOException("delegate control"));
        var forwarded = direct.TrackAsync(() => failure);
        if (!ReferenceEquals(failure, forwarded)) return 16;
        try { await forwarded.ConfigureAwait(false); return 17; }
        catch (IOException error) when (error.Message == "delegate control") { }
        using var directClosed = JsonDocument.Parse(JsonSerializer.Serialize(direct.Snapshot()));
        if (directClosed.RootElement.GetProperty("closed").GetInt64() != 3
            || directClosed.RootElement.GetProperty("active").GetInt64() != 0
            || directClosed.RootElement.GetProperty("reportedMeter").GetProperty("active").GetInt64() != 1) return 18;
        var siteTypes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        var sites = new ExceptionSiteLedger(siteTypes);
        for (var i = 0; i < 600; i++)
            sites.Record("IO", "   at System.IO.Stream.Read()", "   at System.IO.Stream.Read()\n   at EmbedIO.Net.Reader.Run()\n   at Continuation" + i);
        using var grouped = JsonDocument.Parse(JsonSerializer.Serialize(sites.Snapshot()));
        if (grouped.RootElement.GetProperty("counts").EnumerateObject().Count() != 1
            || grouped.RootElement.GetProperty("overflow").GetInt64() != 0
            || siteTypes["IO"] != 600) return 19;
        sites.Record("IO", "   at System.IO.Stream.Read()\n" + new string('x', 20000), "   at System.IO.Stream.OtherRead()");
        using var limited = JsonDocument.Parse(JsonSerializer.Serialize(sites.Snapshot()));
        if (!limited.RootElement.GetProperty("examples").EnumerateObject().Any(item => item.Value.GetProperty("truncated").GetBoolean())) return 20;
        for (var i = 0; i < 600; i++) sites.Record("IO", "   at System.IO.Stream.Read()", "   at DistinctSite" + i);
        using var capped = JsonDocument.Parse(JsonSerializer.Serialize(sites.Snapshot()));
        if (capped.RootElement.GetProperty("overflow").GetInt64() == 0) return 21;
        using var cancellation = new CancellationTokenSource();
        var pacing = new RunState(TimeSpan.FromSeconds(5), cancellation.Token, 1);
        if (!await pacing.WaitForSlotAsync().ConfigureAwait(false)) return 5;
        cancellation.Cancel();
        try { await pacing.WaitForSlotAsync().ConfigureAwait(false); return 6; }
        catch (OperationCanceledException) { }
        var expired = new RunState(TimeSpan.Zero, CancellationToken.None, 1);
        if (await expired.WaitForSlotAsync().ConfigureAwait(false)) return 7;
        var unlimited = new RunState(TimeSpan.FromSeconds(5), CancellationToken.None);
        if (!await unlimited.WaitForSlotAsync(16).ConfigureAwait(false)) return 8;
        var bounded = new RunState(TimeSpan.FromMilliseconds(100), CancellationToken.None, 1);
        if (!await bounded.WaitForSlotAsync().ConfigureAwait(false)) return 9;
        if (await bounded.WaitForSlotAsync().ConfigureAwait(false) || bounded.Running) return 10;
        var concurrent = new RunState(TimeSpan.FromSeconds(2), CancellationToken.None, 1);
        if (!await concurrent.WaitForSlotAsync().ConfigureAwait(false)) return 11;
        var waiting = concurrent.WaitForSlotAsync().AsTask();
        if (await concurrent.WaitForSlotAsync().ConfigureAwait(false)) return 12;
        if (!await waiting.ConfigureAwait(false)) return 13;
        Control.Write("CONNECTION CENSUS AND PACING OK");
        return 0;
    }
}
