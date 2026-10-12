using System;
using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace EmbedIO.Tests.TestObjects
{
    // Bounded test-only runtime messages; no TLS secrets or payload capture.
    internal sealed class QuicHandshakeTrace : EventListener
    {
        private readonly ConcurrentQueue<string> _events = new();
        private int _ready;
        private int _count;

        internal QuicHandshakeTrace()
        {
            Volatile.Write(ref _ready, 1);
            foreach (var source in EventSource.GetSources()) Enable(source);
        }

        protected override void OnEventSourceCreated(EventSource source)
        {
            // EventListener's base constructor can call this before fields exist.
            if (Volatile.Read(ref _ready) != 0) Enable(source);
        }

        private void Enable(EventSource source)
        {
            if (source.Name is "Private.InternalDiagnostics.System.Net.Quic" or "Private.InternalDiagnostics.System.Net.Security")
                EnableEvents(source, EventLevel.Verbose, EventKeywords.All);
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (Volatile.Read(ref _ready) == 0) return;
            var payload = data.Payload == null ? string.Empty : string.Join(" | ", data.Payload.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)));
            if (!payload.Contains("handshake", StringComparison.OrdinalIgnoreCase)
                && !payload.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                && !payload.Contains("configuration", StringComparison.OrdinalIgnoreCase)
                && !payload.Contains("listener", StringComparison.OrdinalIgnoreCase)
                && data.EventName != "Error") return;
            _events.Enqueue($"{DateTime.UtcNow:O} {data.EventSource.Name}/{data.EventName}: {payload}");
            var count = Interlocked.Increment(ref _count);
            while (count > 256 && _events.TryDequeue(out _)) count = Interlocked.Decrement(ref _count);
        }

        internal string[] Snapshot() => _events.ToArray();
    }
}
