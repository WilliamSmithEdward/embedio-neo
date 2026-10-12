using System.Diagnostics;
using System.Diagnostics.Metrics;

// Profiles observe server processing lifetimes, never client objects or native frees.
// Kestrel delegates provide the primary lifetime; its published meter remains a witness.
internal sealed class ConnectionLedger(bool kestrelDelegates = false) : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Dictionary<long, Lifetime> _active = new();
    private long _nextId;
    private long _opened;
    private long _closed;
    private long _invalid;
    private bool _seen;
    private bool _configured;
    private long _meterOpened;
    private long _meterClosed;
    private long _meterInvalid;
    private bool _meterEnded;
    private long _meterActiveAtEnd;
    private long _delegateActiveAtMeterEnd;
    private double _meterEndMilliseconds;

    internal ConnectionLedger() : this(false) { }

    private bool Matches(Instrument instrument) =>
        (instrument.Meter.Name == "Microsoft.AspNetCore.Server.Kestrel" && instrument.Name == "kestrel.active_connections")
        || (!kestrelDelegates && instrument.Meter.Name == "EmbedIO.Server" && instrument.Name == "embedio.server.active_connections");

    internal void Start()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (Matches(instrument))
            {
                lock (_gate) _seen = true;
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            lock (_gate)
            {
                if (kestrelDelegates)
                {
                    if (value == 1) _meterOpened++;
                    else if (value == -1) _meterClosed++;
                    else _meterInvalid++;
                }
                else
                {
                    if (value == 1) _opened++;
                    else if (value == -1) _closed++;
                    else _invalid++;
                }
            }
        });
        _listener.MeasurementsCompleted = (instrument, _) =>
        {
            if (!Matches(instrument)) return;
            lock (_gate)
            {
                _meterEnded = true;
                _meterActiveAtEnd = kestrelDelegates ? _meterOpened - _meterClosed : _opened - _closed;
                _delegateActiveAtMeterEnd = _opened - _closed;
                _meterEndMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
            }
        };
        _listener.Start();
    }

    internal void ConfigureDelegates()
    {
        if (!kestrelDelegates) throw new InvalidOperationException("Delegate observer requires the Kestrel mode.");
        lock (_gate) _configured = true;
    }

    // Return the original task: observing its completion must not add await rethrows.
    internal Task TrackAsync(Func<Task> next, string? connectionId = null)
    {
        var lifetime = BeginLifetime();
        if (lifetime is Lifetime tracked)
        {
            lock (_gate) tracked.ConnectionId = connectionId;
        }
        var attached = false;
        try
        {
            var task = next();
            if (lifetime is Lifetime current)
            {
                lock (_gate) current.Task = new WeakReference<Task>(task);
            }
            if (task.IsCompleted) lifetime.Dispose();
            else
                _ = task.ContinueWith(static (_, state) =>
                {
                    if (state is Lifetime held) held.Dispose();
                }, lifetime, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            attached = true;
            return task;
        }
        finally
        {
            if (!attached) lifetime.Dispose();
        }
    }

    internal IDisposable BeginLifetime()
    {
        lock (_gate)
        {
            if (!_configured) throw new InvalidOperationException("Connection delegate observer is not configured.");
            var lifetime = new Lifetime(this, ++_nextId);
            _opened++;
            _active.Add(lifetime.Id, lifetime);
            return lifetime;
        }
    }

    internal async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            lock (_gate)
            {
                if ((kestrelDelegates ? _configured : _seen) && _opened == _closed && _invalid == 0) return true;
            }

            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    internal object Snapshot()
    {
        lock (_gate) return new
        {
            source = kestrelDelegates ? "kestrel-connection-delegate" : "server-meter",
            available = kestrelDelegates ? _configured : _seen,
            opened = _opened,
            closed = _closed,
            active = _opened - _closed,
            instrumentSeen = _seen,
            invalidMeasurements = _invalid,
            elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            activeDelegateTasks = _active.Values.Take(64).Select(lifetime => new { lifetime.Id, lifetime.ConnectionId, task = lifetime.TaskSnapshot() }).ToArray(),
            activeDelegateTasksOmitted = Math.Max(0, _active.Count - 64),
            reportedMeter = kestrelDelegates ? new
            {
                opened = _meterOpened,
                closed = _meterClosed,
                active = _meterOpened - _meterClosed,
                invalidMeasurements = _meterInvalid,
                completed = _meterEnded,
                activeAtCompletion = _meterActiveAtEnd,
                delegateActiveAtCompletion = _delegateActiveAtMeterEnd,
                completionElapsedMilliseconds = _meterEndMilliseconds,
            } : null,
        };
    }

    public void Dispose() => _listener.Dispose();

    private sealed class Lifetime(ConnectionLedger owner, long id) : IDisposable
    {
        private int _ended;
        internal long Id { get; } = id;
        internal string? ConnectionId { get; set; }
        internal WeakReference<Task>? Task { get; set; }

        internal object TaskSnapshot()
        {
            if (Task is not null && Task.TryGetTarget(out var task)) return new { available = true, id = task.Id, status = task.Status.ToString() };
            return new { available = false, id = 0, status = "collected or not attached" };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0) return;
            lock (owner._gate)
            {
                owner._closed++;
                owner._active.Remove(Id);
            }
        }
    }
}
