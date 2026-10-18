using System;
using System.Threading;

namespace EmbedIO
{
    /// <summary>Immutable wire-byte, replay and subscriber limits for one event channel.</summary>
    public sealed class ServerSentEventHubOptions
    {
        /// <summary>Creates an opt-in event policy. Byte budgets count framed UTF-8 bytes, not total managed heap usage.</summary>
        /// <param name="maxEventBytes">Maximum complete event frame, including fields and separators.</param>
        /// <param name="maxReplayEvents">Maximum retained events; zero disables replay.</param>
        /// <param name="maxReplayBytes">Maximum retained framed bytes.</param>
        /// <param name="maxSubscribers">Maximum simultaneously owned subscriptions.</param>
        /// <param name="maxQueuedEvents">Maximum queued events per subscriber, excluding its one in-flight event.</param>
        /// <param name="maxQueuedBytes">Maximum queued framed bytes per subscriber.</param>
        /// <param name="replayWindow">Retention age; null selects five minutes. Expiry is checked on publish and subscribe.</param>
        /// <param name="heartbeatInterval">Idle comment interval; null selects fifteen seconds. Infinite disables heartbeats.</param>
        /// <param name="writeTimeout">Per-frame write/flush cancellation deadline; null selects thirty seconds.</param>
        public ServerSentEventHubOptions(
            int maxEventBytes = 65536,
            int maxReplayEvents = 256,
            int maxReplayBytes = 1048576,
            int maxSubscribers = 128,
            int maxQueuedEvents = 64,
            int maxQueuedBytes = 262144,
            TimeSpan? replayWindow = null,
            TimeSpan? heartbeatInterval = null,
            TimeSpan? writeTimeout = null
        )
        {
            if (maxEventBytes < 1 || maxEventBytes > 64 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxEventBytes));

            if (maxReplayEvents < 0 || maxReplayEvents > 65536)
                throw new ArgumentOutOfRangeException(nameof(maxReplayEvents));

            if (maxReplayBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxReplayBytes));

            if (maxSubscribers < 1 || maxSubscribers > 65536)
                throw new ArgumentOutOfRangeException(nameof(maxSubscribers));

            if (maxQueuedEvents < 1 || maxQueuedEvents > 65536)
                throw new ArgumentOutOfRangeException(nameof(maxQueuedEvents));

            if (maxQueuedBytes < maxEventBytes) throw new ArgumentOutOfRangeException(nameof(maxQueuedBytes));

            MaxEventBytes = maxEventBytes;
            MaxReplayEvents = maxReplayEvents;
            MaxReplayBytes = maxReplayBytes;
            MaxSubscribers = maxSubscribers;
            MaxQueuedEvents = maxQueuedEvents;
            MaxQueuedBytes = maxQueuedBytes;
            ReplayWindow = replayWindow ?? TimeSpan.FromMinutes(5);
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15);
            WriteTimeout = writeTimeout ?? TimeSpan.FromSeconds(30);

            if (ReplayWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(replayWindow));

            if (
                HeartbeatInterval != Timeout.InfiniteTimeSpan
                && (HeartbeatInterval.TotalMilliseconds < 1 || HeartbeatInterval.TotalMilliseconds > int.MaxValue)
            ) throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));

            if (WriteTimeout.TotalMilliseconds < 1 || WriteTimeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(writeTimeout));
        }

        /// <summary>Gets the maximum complete UTF-8 frame size.</summary>
        public int MaxEventBytes { get; }

        /// <summary>Gets the maximum retained event count.</summary>
        public int MaxReplayEvents { get; }

        /// <summary>Gets the maximum retained framed bytes.</summary>
        public int MaxReplayBytes { get; }

        /// <summary>Gets the maximum owned subscriber count.</summary>
        public int MaxSubscribers { get; }

        /// <summary>Gets the maximum queued count per subscriber.</summary>
        public int MaxQueuedEvents { get; }

        /// <summary>Gets the maximum queued framed bytes per subscriber.</summary>
        public int MaxQueuedBytes { get; }

        /// <summary>Gets the replay retention age.</summary>
        public TimeSpan ReplayWindow { get; }

        /// <summary>Gets the idle heartbeat interval, or infinite to disable it.</summary>
        public TimeSpan HeartbeatInterval { get; }

        /// <summary>Gets the per-frame cooperative write cancellation deadline.</summary>
        public TimeSpan WriteTimeout { get; }
    }
}
