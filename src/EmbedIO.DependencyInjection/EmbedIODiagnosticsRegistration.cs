using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace EmbedIO.DependencyInjection
{
    /// <summary>A disposable registration forwarding EmbedIO trace events to a borrowed logger.</summary>
    public sealed class EmbedIODiagnosticsRegistration : IDisposable
    {
        private readonly TraceSource _source = Diagnostics.Log.Source;
        private readonly ForwardingListener _listener;
        private int _detached;
        private int _detachQueued;

        internal EmbedIODiagnosticsRegistration(ILogger logger)
        {
            _listener = new ForwardingListener(logger);
            _source.Listeners.Add(_listener);
        }

        /// <summary>Gets the number of provider, filter or formatting failures contained by this registration.</summary>
        public long ForwardingFailures => _listener.ForwardingFailures;

        /// <summary>Gets the number of recursive events dropped on the forwarding thread.</summary>
        public long RecursiveEventsDropped => _listener.RecursiveEventsDropped;

        /// <summary>Stops forwarding and unregisters this listener without disposing the borrowed factory.</summary>
        /// <remarks>
        /// Normal disposal waits for the current provider callback. Disposal from a forwarding
        /// callback stops delivery immediately. With the default global trace lock, collection
        /// removal is deferred until the trace iteration can finish. If that lock is disabled,
        /// dispose again outside callbacks after emission stops to remove the listener.
        /// Configure registrations before starting servers and dispose them after shutdown,
        /// especially if the application disables Trace.UseGlobalLock.
        /// </remarks>
        public void Dispose()
        {
            _listener.Stop();
            if (Volatile.Read(ref _detached) != 0) return;
            if (ForwardingListener.IsForwarding)
            {
                if (Trace.UseGlobalLock && Interlocked.Exchange(ref _detachQueued, 1) == 0)
                    ThreadPool.QueueUserWorkItem(_ => Detach());
            }
            else
            {
                _listener.WaitForCallback();
                Detach();
            }
        }

        private void Detach()
        {
            _source.Listeners.Remove(_listener);
            Interlocked.Exchange(ref _detached, 1);
            _listener.Dispose();
        }

        private sealed class ForwardingListener : TraceListener
        {
            private readonly object _gate = new object();
            private ILogger? _logger;
            private volatile bool _closed;
            private long _failures;
            private long _recursive;
            [ThreadStatic] private static bool _forwarding;

            internal ForwardingListener(ILogger logger)
            {
                _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            }

            public override bool IsThreadSafe => true;
            internal static bool IsForwarding => _forwarding;
            internal long ForwardingFailures => Volatile.Read(ref _failures);
            internal long RecursiveEventsDropped => Volatile.Read(ref _recursive);

            public override void Write(string? message) => Forward(null, string.Empty, TraceEventType.Information, 0, message, null);
            public override void WriteLine(string? message) => Write(message);
            public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message)
                => Forward(cache, source, type, id, message, null);
            public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, params object?[]? args)
                => Forward(cache, source, type, id, format, args);

            // Neo emits TraceEvent. The base TraceData formatter would call Write with
            // separate header/data fragments, losing their severity and event ID.
            public override void TraceData(TraceEventCache? cache, string source, TraceEventType type, int id, object? data) { }
            public override void TraceData(TraceEventCache? cache, string source, TraceEventType type, int id, params object?[]? data) { }

            internal void Stop()
            {
                _closed = true;
                Interlocked.Exchange(ref _logger, null);
            }

            internal void WaitForCallback()
            {
                lock (_gate) { }
            }

            private void Forward(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, object?[]? args)
            {
                if (_closed) return;
                if (_forwarding)
                {
                    Interlocked.Increment(ref _recursive);
                    return;
                }

                lock (_gate)
                {
                    var logger = _logger;
                    if (_closed || logger == null) return;
                    _forwarding = true;
                    try
                    {
                        var level = Map(type);
                        if (level == LogLevel.None) return;
                        if (Filter != null && !Filter.ShouldTrace(cache, source, type, id, format, args, null, null)) return;
                        if (!logger.IsEnabled(level)) return;
                        var text = args == null ? format ?? string.Empty
                            : string.Format(CultureInfo.InvariantCulture, format ?? string.Empty, args);
                        text = text.Replace("\r", "\\r").Replace("\n", "\\n");
                        logger.Log(level, new EventId(id), null, "{TraceMessage}", text);
                    }
                    catch (Exception)
                    {
                        // Reporting through the same source would create a feedback loop.
                        Interlocked.Increment(ref _failures);
                    }
                    finally { _forwarding = false; }
                }
            }

            private static LogLevel Map(TraceEventType type)
            {
                switch (type)
                {
                    case TraceEventType.Critical: return LogLevel.Critical;
                    case TraceEventType.Error: return LogLevel.Error;
                    case TraceEventType.Warning: return LogLevel.Warning;
                    case TraceEventType.Information: return LogLevel.Information;
                    case TraceEventType.Verbose: return LogLevel.Debug;
                    default: return LogLevel.None;
                }
            }
        }
    }
}
