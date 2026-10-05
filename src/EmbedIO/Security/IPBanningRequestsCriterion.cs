using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace EmbedIO.Security
{
    /// <summary>
    /// Represents a maximun requests per second criterion for <see cref="IPBanningModule"/>.
    /// </summary>
    /// <seealso cref="IIPBanningCriterion" />
    public class IPBanningRequestsCriterion : IIPBanningCriterion
    {
        /// <summary>
        /// The default maximum request per second.
        /// </summary>
        public const int DefaultMaxRequestsPerSecond = 50;

        // Histories retain global ownership; each list is also its private synchronization lock.
        private static readonly ConcurrentDictionary<IPAddress, List<long>> Requests = new ConcurrentDictionary<IPAddress, List<long>>();

        private readonly int _maxRequestsPerSecond;

        private bool _disposed;

        internal IPBanningRequestsCriterion(int maxRequestsPerSecond)
        {
            _maxRequestsPerSecond = maxRequestsPerSecond;
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="IPBanningRequestsCriterion"/> class.
        /// </summary>
        ~IPBanningRequestsCriterion()
        {
            Dispose(false);
        }

        /// <inheritdoc />
        public Task<bool> ValidateIPAddress(IPAddress address)
        {
            var attempts = Requests.GetOrAdd(address, _ => new List<long>());
            var requestedAt = DateTime.Now.Ticks;
            while (true)
            {
                lock (attempts)
                {
                    // A purge or explicit reset may remove this history while we wait.
                    if (!Requests.TryGetValue(address, out var current) || !ReferenceEquals(current, attempts))
                    {
                        attempts = Requests.GetOrAdd(address, _ => new List<long>());
                        continue;
                    }

                    attempts.Add(requestedAt);
                    var lastSecond = DateTime.Now.AddSeconds(-1).Ticks;
                    var lastMinute = DateTime.Now.AddMinutes(-1).Ticks;
                    var secondCount = 0;
                    var minuteCount = 0;
                    for (var i = 0; i < attempts.Count; i++)
                    {
                        var time = attempts[i];
                        if (time >= lastSecond) secondCount++;
                        if (time >= lastMinute) minuteCount++;
                    }

                    return Task.FromResult(secondCount >= _maxRequestsPerSecond
                        || minuteCount / 60 >= _maxRequestsPerSecond);
                }
            }
        }

        /// <inheritdoc />
        public void ClearIPAddress(IPAddress address) =>
            Requests.TryRemove(address, out _);

        /// <inheritdoc />
        public void PurgeData()
        {
            var minTime = DateTime.Now.AddMinutes(-1).Ticks;

            foreach (var pair in Requests)
            {
                var attempts = pair.Value;
                lock (attempts)
                {
                    if (!Requests.TryGetValue(pair.Key, out var current) || !ReferenceEquals(current, attempts))
                        continue;

                    attempts.RemoveAll(time => time < minTime);
                    if (attempts.Count == 0)
                        ((ICollection<KeyValuePair<IPAddress, List<long>>>)Requests).Remove(pair);
                    else
                        attempts.TrimExcess();
                }
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                Requests.Clear();
            }

            _disposed = true;
        }
    }
}
