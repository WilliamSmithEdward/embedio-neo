using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;

namespace EmbedIO.Caching
{
    /// <summary>Bounded in-process output storage for explicitly opted-in public endpoints.</summary>
    /// <remarks>No timers, background workers or runtime dependencies are required. Upstream
    /// authentication and authorization must execute before this cache. Cookies, credentials,
    /// conditional/range requests and uncacheable responses bypass storage. Applications must
    /// invalidate after committing changes to their underlying data.</remarks>
    public sealed class OutputCache
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0330:Use 'System.Threading.Lock'",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Threading.Lock."
        )]
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new();
        private readonly Dictionary<string, TaskCompletionSource<bool>> _fills = new(StringComparer.Ordinal);
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly int _maxResponseBytes;
        private readonly int _maxConcurrentFills;
        private readonly int _maxKeyCharacters;
        private long _bytes;
        private long _generation;

        /// <summary>Creates a cache with hard bounds on resident entries, accounted bytes,
        /// individual bodies, simultaneous captures and key lengths.</summary>
        /// <param name="maxEntries">Maximum resident entries.</param>
        /// <param name="maxBytes">Maximum accounted body and UTF-16 metadata bytes.</param>
        /// <param name="maxResponseBytes">Maximum body bytes per capture.</param>
        /// <param name="maxConcurrentFills">Maximum simultaneously captured distinct keys; overflow bypasses.</param>
        /// <param name="maxKeyCharacters">Maximum key characters; longer keys bypass.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Maintainability",
            "CA1512:Use ArgumentOutOfRangeException throw helper",
            Justification = "The shared implementation targets netstandard2.0, which has no ArgumentOutOfRangeException throw helpers."
        )]
        public OutputCache(
            int maxEntries = 256,
            long maxBytes = 16 * 1024 * 1024,
            int maxResponseBytes = 256 * 1024,
            int maxConcurrentFills = 16,
            int maxKeyCharacters = 8192
        )
        {
            if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));

            if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            if (maxResponseBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));

            if (maxConcurrentFills < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrentFills));

            if (maxKeyCharacters < 128) throw new ArgumentOutOfRangeException(nameof(maxKeyCharacters));

            _maxEntries = maxEntries;
            _maxBytes = maxBytes;
            _maxResponseBytes = maxResponseBytes;
            _maxConcurrentFills = maxConcurrentFills;
            _maxKeyCharacters = maxKeyCharacters;
        }

        /// <summary>Gets the number of resident entries, including entries awaiting lazy expiry.</summary>
        public int Count
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        /// <summary>Gets resident body and metadata bytes, excluding bounded object/index overhead.</summary>
        public long SizeBytes
        {
            get
            {
                lock (_gate) return _bytes;
            }
        }

        /// <summary>Removes entries with a tag and fences all fills started before this call.</summary>
        /// <param name="tag">The exact, case-sensitive tag.</param>
        public void InvalidateTag(string tag)
        {
            tag = global::EmbedIO.Utilities.Validate.NotNull(nameof(tag), tag);

            lock (_gate)
            {
                _generation++;
                var node = _lru.First;

                while (node != null)
                {
                    var next = node.Next;

                    if (Array.IndexOf(node.Value.Policy.Tags, tag) >= 0) Remove(node);

                    node = next;
                }
            }
        }

        /// <summary>Removes all entries and fences all earlier fills.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _generation++;
                _entries.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        /// <summary>Wraps a fluent route handler. Run request-specific middleware before this wrapper.</summary>
        /// <param name="policy">The immutable policy.</param>
        /// <param name="handler">The complete handler, including awaited writes.</param>
        /// <returns>A handler that reuses eligible completed representations.</returns>
        public RouteHandlerCallback Cache(OutputCachePolicy policy, RouteHandlerCallback handler)
        {
            policy = global::EmbedIO.Utilities.Validate.NotNull(nameof(policy), policy);

            handler = global::EmbedIO.Utilities.Validate.NotNull(nameof(handler), handler);

            return (context, route) => ExecuteAsync(context, policy, capture => handler(capture, route));
        }

        /// <summary>Runs or replays an explicitly cacheable handler. Never retain the supplied context.</summary>
        /// <param name="context">The current request.</param>
        /// <param name="policy">The immutable policy.</param>
        /// <param name="handler">Writes one complete response to its supplied context.</param>
        /// <returns>The complete response task.</returns>
        public async Task ExecuteAsync(IHttpContext context, OutputCachePolicy policy, Func<IHttpContext, Task> handler)
        {
            context = global::EmbedIO.Utilities.Validate.NotNull(nameof(context), context);

            policy = global::EmbedIO.Utilities.Validate.NotNull(nameof(policy), policy);

            handler = global::EmbedIO.Utilities.Validate.NotNull(nameof(handler), handler);

            context.CancellationToken.ThrowIfCancellationRequested();
            var key = Eligible(context)
                && VaryCovered(context.Response, policy)
                && !Internal.ResponseCommitment.IsCommitted(context)
                ? Key(context, policy)
                : null;

            if (key == null)
            {
                await handler(context).ConfigureAwait(false);
                return;
            }

            TaskCompletionSource<bool>? fill;
            long generation;
            long started;

            while (true)
            {
                Entry? entry = null;
                var leader = false;

                lock (_gate)
                {
                    if (_entries.TryGetValue(key, out var node))
                    {
                        if (Expired(node.Value.Started, node.Value.Policy.Duration)) Remove(node);

                        else
                        {
                            entry = node.Value;
                            _lru.Remove(node);
                            _lru.AddFirst(node);
                        }
                    }

                    generation = _generation;
                    started = Stopwatch.GetTimestamp();

                    if (entry != null) fill = null;

                    else if (!_fills.TryGetValue(key, out fill) && _fills.Count < _maxConcurrentFills)
                    {
                        fill = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _fills.Add(key, fill);
                        leader = true;
                    }
                }

                if (entry != null)
                {
                    await entry
                        .ReplayAsync(context)
                        .ConfigureAwait(false);
                    return;
                }

                if (leader) break;

                if (fill == null)
                {
                    await handler(context).ConfigureAwait(false);
                    return;
                }

                // One waiter's cancellation never cancels the producer or another waiter.
                await WaitAsync(fill.Task, context.CancellationToken).ConfigureAwait(false);

                if (
                    !Eligible(context)
                    || !VaryCovered(context.Response, policy)
                    || Internal.ResponseCommitment.IsCommitted(context)
                )
                {
                    await handler(context).ConfigureAwait(false);
                    return;
                }
            }

            try
            {
                using var capture = OutputCacheCapture.Create(context, _maxResponseBytes);
                await handler(capture).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
                var entry = capture.Complete(key, policy, started);

                if (entry == null || entry.Size > _maxBytes || Expired(started, policy.Duration)) return;

                lock (_gate)
                {
                    if (_generation != generation) return;

                    while (_entries.Count >= _maxEntries || _bytes > _maxBytes - entry.Size)
                    {
                        var last = _lru.Last;

                        if (last == null) break;

                        Remove(last);
                    }

                    _entries.Add(key, _lru.AddFirst(entry));
                    _bytes += entry.Size;
                }
            }

            finally
            {
                lock (_gate) _fills.Remove(key);

                fill?.TrySetResult(true);
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _entries.Remove(node.Value.Key);
            _lru.Remove(node);
            _bytes -= node.Value.Size;
        }

        private static bool Expired(long started, TimeSpan duration) =>
            (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency >= duration.TotalSeconds;

        private string? Key(IHttpContext context, OutputCachePolicy policy)
        {
            var builder = new StringBuilder();

            if (
                !Append(policy.Identity)
                || !Append(context.Request.HttpMethod)
                || !Append(context.Request.ProtocolVersion.ToString())
                || !Append(context.Request.Url.GetLeftPart(UriPartial.Authority))
                || !Append(context.Request.RawTarget)
            ) return null;

            foreach (var name in policy.Headers)
            {
                var values = context.Request.Headers.GetValues(name);

                if (
                    !Append(
                        values == null
                            ? "missing"
                            : values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    )
                ) return null;

                if (values != null) foreach (var value in values) if (!Append(value)) return null;
            }

            return builder.ToString();

            bool Append(string value)
            {
                if (value.Length > _maxKeyCharacters - builder.Length - 16) return false;

                builder
                    .Append(value.Length)
                    .Append(':')
                    .Append(value);
                return true;
            }
        }

        internal static bool Eligible(IHttpContext context)
        {
            var request = context.Request;
            var headers = request.Headers;
            return Security.SecurityHeadersState.Find(context)?.PreventStorage != true
                && (request.HttpVerb == HttpVerbs.Get || request.HttpVerb == HttpVerbs.Head)
                && !request.HasEntityBody
                && !request.IsWebSocketRequest
                && !request.IsAuthenticated
                && context.User?.Identity?.IsAuthenticated
                != true
                && headers["Cookie"] == null
                && headers["Authorization"] == null
                && request.Cookies.Count == 0
                && headers["Upgrade"] == null
                && headers["Proxy-Authorization"] == null
                && headers["Range"] == null
                && headers["If-Match"] == null
                && headers["If-None-Match"] == null
                && headers["If-Modified-Since"] == null
                && headers["If-Unmodified-Since"] == null
                && headers["If-Range"] == null
                && headers["Cache-Control"] == null
                && headers["Pragma"] == null
                && ResponseEligible(context.Response);
        }

        internal static bool VaryCovered(IHttpResponse response, OutputCachePolicy policy) =>
            response.Headers["Vary"] is not { } vary
                || vary
                    .Split(',')
                    .All(name => policy.Headers.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase));

        internal static bool ResponseEligible(IHttpResponse response) =>
            response.StatusCode == 200
                && response.Cookies.Count == 0
                && response.Headers["Set-Cookie"] == null
                && response.Headers["Trailer"] == null
                && response.Headers["Content-Range"] == null
                && response.Headers["Cache-Control"] == null
                && response.Headers["Pragma"] == null
                && !string.Equals(
                    response.ContentType
                        ?.Split(';')[0]
                        .Trim(),
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase
                );

        private static async Task WaitAsync(Task task, CancellationToken token)
        {
            if (!token.CanBeCanceled)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using (token.Register(
                state => ((TaskCompletionSource<bool>)(state
                    ?? throw new InvalidOperationException())).TrySetCanceled(),
                canceled
            ))
                await (await Task
                    .WhenAny(task, canceled.Task)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
        }

        internal sealed class Entry
        {
            internal readonly string Key;
            internal readonly OutputCachePolicy Policy;
            internal readonly long Started;
            internal readonly byte[] Body;
            internal readonly KeyValuePair<string, string[]>[] Headers;
            internal readonly string? ContentType;
            internal readonly string? StatusDescription;
            internal readonly Encoding? Encoding;
            internal readonly long Size;
            internal readonly long ContentLength;

            internal Entry(
                string key,
                OutputCachePolicy policy,
                long started,
                byte[] body,
                KeyValuePair<string, string[]>[] headers,
                string? contentType,
                Encoding? encoding,
                long contentLength,
                string? statusDescription
            )
            {
                Key = key;
                Policy = policy;
                Started = started;
                Body = body;
                Headers = headers;
                ContentType = contentType;
                Encoding = encoding;
                ContentLength = contentLength;
                StatusDescription = statusDescription;
                long headerLength = 0;

                foreach (var header in headers)
                {
                    headerLength += header.Key.Length;

                    foreach (var value in header.Value)
                        headerLength += value.Length;
                }

                Size = body.LongLength
                    + 2L
                        * (key.Length
                            + (contentType?.Length ?? 0)
                            + (statusDescription?.Length ?? 0)
                            + headerLength);
            }

            [System.Diagnostics.CodeAnalysis.SuppressMessage(
                "Performance",
                "CA1835:Prefer memory-based stream operations",
                Justification = "The shared implementation targets netstandard2.0, which has no memory-based Stream async overloads."
            )]
            internal async Task ReplayAsync(IHttpContext context)
            {
                var response = context.Response;

                if (ContentType != null) response.ContentType = ContentType;

                response.ContentEncoding = Encoding;

                if (StatusDescription != null) response.StatusDescription = StatusDescription;

                foreach (var header in Headers)
                {
                    // Current hook/middleware requirements remain authoritative for client
                    // caches; a stored representation must not erase a new supported Vary field.
                    var values = header.Key.Equals("Vary", StringComparison.OrdinalIgnoreCase)
                        && response.Headers[header.Key] is { } current
                        ?
                        [
                            .. header.Value
                                .Concat([current])
                                .SelectMany(value => value.Split(','))
                                .Select(value => value.Trim())
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                        ]
                        : header.Value;
                    response.Headers.Remove(header.Key);

                    foreach (var value in values) response.Headers.Add(header.Key, value);
                }

                response.SendChunked = false;
                response.ContentLength64 = ContentLength;
                await response.OutputStream
                    .WriteAsync(Body, 0, Body.Length, context.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}
