using System.Diagnostics.Metrics;

// Opt-in test observer: counts actual server lifetime measurements, not client objects.
internal sealed class ConnectionLedger : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private long _opened;
    private long _closed;
    private long _invalid;
    private bool _seen;

    internal ConnectionLedger()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if ((instrument.Meter.Name == "EmbedIO.Server" && instrument.Name == "embedio.server.active_connections")
                || (instrument.Meter.Name == "Microsoft.AspNetCore.Server.Kestrel" && instrument.Name == "kestrel.active_connections"))
            {
                lock (_gate) _seen = true;
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (_gate)
            {
                if (value == 1) _opened++;
                else if (value == -1) _closed++;
                else _invalid++;
            }
        });
        _listener.Start();
    }

    internal object Snapshot()
    {
        lock (_gate) return new { opened = _opened, closed = _closed, active = _opened - _closed, instrumentSeen = _seen, invalidMeasurements = _invalid };
    }

    public void Dispose() => _listener.Dispose();
}
