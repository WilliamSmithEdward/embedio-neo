using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Idempotency
{
    /// <summary>Bounded process-local claims. Active claims never expire; terminal claims expire lazily without early eviction.</summary>
    public sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly int _maxEntryBytes;

        /// <summary>Creates bounded storage. Capacity reserves each claim's maximum result bytes until expiry.</summary>
        /// <param name="maxEntries">Maximum retained claims, including active and uncertain operations.</param>
        /// <param name="maxBytes">Maximum reserved result bytes.</param>
        /// <param name="maxEntryBytes">Per-claim response and metadata reservation.</param>
        public MemoryIdempotencyStore(
            int maxEntries = 256,
            long maxBytes = 16 * 1024 * 1024,
            int maxEntryBytes = 80 * 1024
        )
        {
            if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));

            if (maxEntryBytes < 1 || maxEntryBytes > 17 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxEntryBytes));

            if (maxBytes < maxEntryBytes) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _maxEntries = maxEntries;
            _maxBytes = maxBytes;
            _maxEntryBytes = maxEntryBytes;
        }

        /// <summary>Gets resident claims, including terminal claims awaiting lazy expiry.</summary>
        public int Count
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        /// <summary>Gets reserved result bytes; bounded key/index overhead is separate.</summary>
        public long SizeBytes
        {
            get
            {
                lock (_gate) return _entries.Count * (long)_maxEntryBytes;
            }
        }

        /// <inheritdoc />
        public Task<IdempotencyRecord> AcquireAsync(
            string key,
            string fingerprint,
            TimeSpan retention,
            CancellationToken cancellationToken
        )
        {
            ValidateKey(key);
            ValidateKey(fingerprint);
            cancellationToken.ThrowIfCancellationRequested();

            if (retention <= TimeSpan.Zero || retention > TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException(nameof(retention));

            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (!Expired(entry))
                        return Task.FromResult(
                            !string.Equals(fingerprint, entry.Fingerprint, StringComparison.Ordinal)
                                ? new IdempotencyRecord(IdempotencyState.Conflict)
                                : new IdempotencyRecord(entry.State, response: entry.Response)
                        );

                    _entries.Remove(key);
                }

                // Existing-key replays stay O(1). A full pool reclaims only expired terminal records.
                if (Full())
                {
                    var expired = new List<string>();

                    foreach (var pair in _entries) if (Expired(pair.Value)) expired.Add(pair.Key);

                    foreach (var old in expired) _entries.Remove(old);
                }

                if (Full()) return Task.FromResult(new IdempotencyRecord(IdempotencyState.Full));

                var owner = Guid
                    .NewGuid()
                    .ToString("N");
                _entries.Add(key, new Entry(fingerprint, owner, retention));
                return Task.FromResult(new IdempotencyRecord(IdempotencyState.Acquired, owner));
            }
        }

        /// <inheritdoc />
        public Task<bool> CompleteAsync(
            string key,
            string ownerToken,
            IdempotencyResponse response,
            CancellationToken cancellationToken
        )
        {
            ValidateKey(key);
            ValidateKey(ownerToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (response == null) throw new ArgumentNullException(nameof(response));

            lock (_gate)
            {
                if (response.SizeBytes > _maxEntryBytes || !Owns(key, ownerToken, out var entry))
                    return Task.FromResult(false);

                entry.Response = response;
                entry.State = IdempotencyState.Completed;
                entry.Finished = Stopwatch.GetTimestamp();
                return Task.FromResult(true);
            }
        }

        /// <inheritdoc />
        public Task<bool> FailAsync(string key, string ownerToken, CancellationToken cancellationToken)
        {
            ValidateKey(key);
            ValidateKey(ownerToken);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                if (!Owns(key, ownerToken, out var entry)) return Task.FromResult(false);

                entry.State = IdempotencyState.Indeterminate;
                entry.Finished = Stopwatch.GetTimestamp();
                return Task.FromResult(true);
            }
        }

        private bool Full() => _entries.Count >= _maxEntries || _entries.Count >= _maxBytes / _maxEntryBytes;

        private static bool Expired(Entry entry) =>
            entry.Finished != 0
                && (Stopwatch.GetTimestamp() - entry.Finished) / (double)Stopwatch.Frequency
                    >= entry.Retention.TotalSeconds;

        private bool Owns(string key, string owner, [NotNullWhen(true)] out Entry? entry)
        {
            if (
                _entries.TryGetValue(key, out var found)
                && found.State == IdempotencyState.InProgress
                && string.Equals(owner, found.Owner, StringComparison.Ordinal)
            )
            {
                entry = found;
                return true;
            }

            entry = null;
            return false;
        }

        private static void ValidateKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 256)
                throw new ArgumentException("A bounded nonempty store key/token is required.", nameof(value));
        }

        private sealed class Entry
        {
            internal readonly string Fingerprint;
            internal readonly string Owner;
            internal readonly TimeSpan Retention;
            internal IdempotencyState State = IdempotencyState.InProgress;
            internal IdempotencyResponse? Response;
            internal long Finished;

            internal Entry(string fingerprint, string owner, TimeSpan retention)
            {
                Fingerprint = fingerprint;
                Owner = owner;
                Retention = retention;
            }
        }
    }
}
