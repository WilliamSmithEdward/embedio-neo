using System.Diagnostics.Metrics;
using System.Text.Json;

internal static class TeardownAccountingCheck
{
    internal static async Task<int> RunAsync()
    {
        using var ledger = new ConnectionLedger();
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
