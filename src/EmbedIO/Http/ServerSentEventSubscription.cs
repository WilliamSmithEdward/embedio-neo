using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>One bounded subscriber. Dispose it to release its channel slot.</summary>
    public sealed class ServerSentEventSubscription : IDisposable
    {
        private readonly ServerSentEventHub _hub;
        private int _reading;
        internal ServerSentEventSubscription(ServerSentEventHub hub) => _hub = hub;
        internal Queue<ServerSentEvent> Queue { get; } = new();
        internal int Bytes { get; set; }
        internal bool Ended { get; set; }
        internal bool Removed { get; set; }
        internal IOException? Failure { get; set; }
        internal TaskCompletionSource<bool> Signal { get; set; } = NewSignal();
        internal static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Reads the next event, or null after channel completion. Only one read may be active.</summary>
        /// <param name="cancellationToken">Cancels this read without discarding queued events or the subscription.</param>
        /// <returns>The next event. Queue overflow throws IOException and requires disposal/reconnection.</returns>
        public async Task<ServerSentEvent?> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) != 0) throw new InvalidOperationException("Only one subscription read may be active.");
            try { return await _hub.ReadAsync(this, cancellationToken).ConfigureAwait(false); }
            finally { Volatile.Write(ref _reading, 0); }
        }
        /// <summary>Discards queued events, wakes a pending read and releases the subscriber slot once.</summary>
        public void Dispose() => _hub.Remove(this);
    }
}
