using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>A bounded, in-memory event channel with atomic replay-to-live subscription.</summary>
    /// <remarks>Own one hub per channel/security scope. No background work is started by construction.
    /// Replay is not durable, acknowledged delivery or exactly-once processing. Discarded cursors require application resynchronization.</remarks>
    public sealed class ServerSentEventHub : IDisposable
    {
        private readonly object _gate = new();
        private readonly string _epoch = Guid
            .NewGuid()
            .ToString("N", CultureInfo.InvariantCulture);
        private readonly Queue<ServerSentEvent> _replay = new();
        private readonly HashSet<ServerSentEventSubscription> _subscribers = [];
        private long _sequence;
        private int _replayBytes;
        private bool _completed;
        private bool _disposed;

        /// <summary>Creates an application-owned channel. Existing SSE writers and listener defaults are unaffected.</summary>
        /// <param name="options">Optional immutable channel policy.</param>
        public ServerSentEventHub(ServerSentEventHubOptions? options = null) =>
            Options = options ?? new ServerSentEventHubOptions();

        /// <summary>Gets this channel's immutable limits.</summary>
        public ServerSentEventHubOptions Options { get; }

        /// <summary>Gets the number of owned subscriptions, including overflowed subscriptions awaiting disposal.</summary>
        public int SubscriberCount
        {
            get
            {
                lock (_gate) return _subscribers.Count;
            }
        }

        /// <summary>Publishes one event in channel order without awaiting network clients.</summary>
        /// <param name="data">Event data. Line breaks are framed safely.</param>
        /// <param name="eventName">Optional event type; CR, LF and NUL are rejected.</param>
        /// <returns>The opaque event ID assigned after validation.</returns>
        public string Publish(string? data, string? eventName = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (eventName != null && eventName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Invalid event name.", nameof(eventName));

            // Reject obviously oversized input before formatting or allocating a frame.
            if (data.Length > Options.MaxEventBytes || (eventName?.Length ?? 0) > Options.MaxEventBytes)
                throw new ArgumentException("The event exceeds MaxEventBytes.", nameof(data));

            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ServerSentEventHub));

                if (_completed) throw new InvalidOperationException("The event channel is complete.");

                var sequence = checked(_sequence + 1);
                var id = _epoch + ":" + sequence.ToString(CultureInfo.InvariantCulture);
                var frame = new StringBuilder()
                    .Append("id: ")
                    .Append(id)
                    .Append('\n');

                if (eventName != null)
                    frame
                        .Append("event: ")
                        .Append(eventName)
                        .Append('\n');

                var start = 0;
                var bytes = Encoding.UTF8.GetByteCount(frame.ToString()) + 1L;

                for (var index = 0; index <= data.Length; index++)
                {
                    if (index < data.Length && data[index] != '\r' && data[index] != '\n') continue;

                    var length = index - start;
                    bytes += 7L + Encoding.UTF8.GetByteCount(data.Substring(start, length));

                    if (bytes > Options.MaxEventBytes)
                        throw new ArgumentException("The framed event exceeds MaxEventBytes.", nameof(data));

                    frame
                        .Append("data: ")
                        .Append(data, start, length)
                        .Append('\n');

                    if (
                        index < data.Length && data[index] == '\r' && index + 1 < data.Length && data[index + 1] == '\n'
                    ) index++;

                    start = index + 1;
                }

                frame.Append('\n');
                var item = new ServerSentEvent(
                    id,
                    data,
                    eventName,
                    Encoding.UTF8.GetBytes(frame.ToString()),
                    Stopwatch.GetTimestamp(),
                    sequence
                );
                _sequence = sequence;
                Expire(item.Timestamp);

                if (Options.MaxReplayEvents > 0 && item.Frame.Length <= Options.MaxReplayBytes)
                {
                    while (
                        _replay.Count >= Options.MaxReplayEvents
                        || item.Frame.Length > Options.MaxReplayBytes - _replayBytes
                    ) Evict();

                    _replay.Enqueue(item);
                    _replayBytes += item.Frame.Length;
                }

                else
                {
                    _replay.Clear();
                    _replayBytes = 0;
                }

                foreach (var subscriber in _subscribers)
                {
                    if (subscriber.Ended) continue;

                    if (
                        subscriber.Queue.Count >= Options.MaxQueuedEvents
                        || item.Frame.Length > Options.MaxQueuedBytes - subscriber.Bytes
                    )
                    {
                        subscriber.Failure = new IOException(
                            "The event subscriber exceeded its queue budget; reconnect or resynchronize."
                        );
                        subscriber.Ended = true;
                        subscriber.Queue.Clear();
                        subscriber.Bytes = 0;
                    }

                    else
                    {
                        subscriber.Queue.Enqueue(item);
                        subscriber.Bytes += item.Frame.Length;
                    }

                    subscriber.Signal.TrySetResult(true);
                }

                return id;
            }
        }

        /// <summary>Subscribes atomically to retained events after a cursor and all subsequent publications.</summary>
        /// <param name="lastEventId">Null or empty starts with future events. Other cursors must belong to retained history in this hub.</param>
        /// <returns>An owned subscription. Dispose it even after overflow/completion.</returns>
        /// <exception cref="HttpException">409 requires application resynchronization; 503 indicates a full, completed or disposed channel.</exception>
        public ServerSentEventSubscription Subscribe(string? lastEventId = null)
        {
            lock (_gate)
            {
                if (_disposed || _completed || _subscribers.Count >= Options.MaxSubscribers)
                    throw new HttpException(HttpStatusCode.ServiceUnavailable, "The event channel is unavailable.");

                Expire(Stopwatch.GetTimestamp());
                var after = _sequence;

                if (lastEventId != null && lastEventId.Length != 0)
                {
                    var prefix = _epoch + ":";

                    if (
                        lastEventId.Length > 64
                        || !lastEventId.StartsWith(prefix, StringComparison.Ordinal)
                        || !long.TryParse(
                            lastEventId.Substring(prefix.Length),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out after
                        )
                        || after < 1
                        || after > _sequence
                        || lastEventId != prefix + after.ToString(CultureInfo.InvariantCulture)
                        || Options.MaxReplayEvents == 0
                        || _replay.Count == 0
                        || after < _replay.Peek().Sequence - 1
                    )
                        throw new HttpException(
                            HttpStatusCode.Conflict,
                            "Event history is unavailable; resynchronize before reconnecting."
                        );
                }

                var subscriber = new ServerSentEventSubscription(this);

                foreach (var item in _replay)
                {
                    if (item.Sequence <= after) continue;

                    if (
                        subscriber.Queue.Count >= Options.MaxQueuedEvents
                        || item.Frame.Length > Options.MaxQueuedBytes - subscriber.Bytes
                    )
                        throw new HttpException(
                            HttpStatusCode.Conflict,
                            "Event replay exceeds the subscriber budget; resynchronize."
                        );

                    subscriber.Queue.Enqueue(item);
                    subscriber.Bytes += item.Frame.Length;
                }

                _subscribers.Add(subscriber);
                return subscriber;
            }
        }

        /// <summary>Creates a controller/direct response that reads Last-Event-ID after normal request authorization.</summary>
        /// <returns>An explicit SSE result. Creating the result does not subscribe or start timers.</returns>
        public ServerSentEventStream Stream() => new(this);

        /// <summary>Stops publication and subscription, drains existing queues, then ends their streams.</summary>
        public void Complete()
        {
            lock (_gate)
            {
                if (_completed) return;

                _completed = true;
                _replay.Clear();
                _replayBytes = 0;

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Ended = true;
                    subscriber.Signal.TrySetResult(true);
                }
            }
        }

        /// <summary>Discards retained/queued events and wakes readers. Owners must still dispose their subscriptions.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;

                _disposed = true;
                _completed = true;
                _replay.Clear();
                _replayBytes = 0;

                foreach (var subscriber in _subscribers)
                {
                    subscriber.Ended = true;
                    subscriber.Queue.Clear();
                    subscriber.Bytes = 0;
                    subscriber.Signal.TrySetResult(true);
                }
            }
        }

        internal void Remove(ServerSentEventSubscription subscriber)
        {
            lock (_gate)
            {
                if (subscriber.Removed) return;

                subscriber.Removed = true;
                subscriber.Ended = true;
                subscriber.Queue.Clear();
                subscriber.Bytes = 0;
                _subscribers.Remove(subscriber);
                subscriber.Signal.TrySetResult(true);
            }
        }

        internal async Task<ServerSentEvent?> ReadAsync(ServerSentEventSubscription subscriber, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task ready;

                lock (_gate)
                {
                    if (subscriber.Failure != null) throw subscriber.Failure;

                    if (subscriber.Queue.Count > 0)
                    {
                        var item = subscriber.Queue.Dequeue();
                        subscriber.Bytes -= item.Frame.Length;
                        return item;
                    }

                    if (subscriber.Ended) return null;

                    if (subscriber.Signal.Task.IsCompleted) subscriber.Signal = ServerSentEventSubscription.NewSignal();

                    ready = subscriber.Signal.Task;
                }

                var canceled = ServerSentEventSubscription.NewSignal();

                using (token.Register(() => canceled.TrySetCanceled(token)))
                {
                    var completed = await Task
                        .WhenAny(ready, canceled.Task)
                        .ConfigureAwait(false);
                    await completed.ConfigureAwait(false);
                }
            }
        }

        private void Expire(long now)
        {
            while (
                _replay.Count > 0
                && (double)(now - _replay.Peek().Timestamp) / Stopwatch.Frequency > Options.ReplayWindow.TotalSeconds
            ) Evict();
        }

        private void Evict() => _replayBytes -= _replay.Dequeue().Frame.Length;
    }
}
