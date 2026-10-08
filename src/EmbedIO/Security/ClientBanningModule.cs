using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security.Internal;

namespace EmbedIO.Security
{
    /// <summary>Opt-in request-context banning with permanent network policies and temporary client-key bans.</summary>
    /// <remarks>
    /// Allow networks override deny networks, temporary bans and criteria. Empty lists impose no network restriction.
    /// Register after any authentication needed to select a trusted key, and before protected handlers.
    /// No forwarded header is trusted automatically. Callbacks are borrowed and may execute concurrently;
    /// they must honor the context cancellation token and must not retain the context after returning.
    /// </remarks>
    public sealed class ClientBanningModule : WebModuleBase, IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<IHttpContext, string> _clientKeySelector;
        private readonly Dictionary<string, ClientBanInfo> _bans = new Dictionary<string, ClientBanInfo>(StringComparer.Ordinal);
        private readonly HashSet<string> _permanentBans = new HashSet<string>(StringComparer.Ordinal);
        private PermanentClientBanStore? _store;
        private readonly List<ClientNetwork> _allowed = new List<ClientNetwork>();
        private readonly List<ClientNetwork> _denied = new List<ClientNetwork>();
        private readonly List<Func<IHttpContext, Task<bool>>> _criteria = new List<Func<IHttpContext, Task<bool>>>();
        private DateTimeOffset _nextExpiry = DateTimeOffset.MaxValue;
        private bool _frozen;
        private bool _disposed;

        /// <summary>Creates an independent banning policy for an explicit route and client-key selector.</summary>
        /// <param name="baseRoute">The protected base route, relative to its container.</param>
        /// <param name="clientKeySelector">Selects a nonblank ordinal key of at most 1024 characters.</param>
        /// <param name="banDuration">The automatic ban duration; null selects thirty minutes.</param>
        /// <param name="maximumBannedClients">The maximum stored keys; defaults to 4096.</param>
        /// <remarks>
        /// At capacity, requests outside allow networks are rejected until expiration or unban frees capacity.
        /// Manual insertion of a new key returns false at capacity. No active ban is silently evicted.
        /// </remarks>
        public ClientBanningModule(string baseRoute, Func<IHttpContext, string> clientKeySelector,
            TimeSpan? banDuration = null, int maximumBannedClients = 4096) : base(baseRoute)
        {
            _clientKeySelector = clientKeySelector ?? throw new ArgumentNullException(nameof(clientKeySelector));
            BanDuration = banDuration ?? TimeSpan.FromMinutes(30);
            ValidateDuration(BanDuration, nameof(banDuration));
            if (maximumBannedClients <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBannedClients));
            MaximumBannedClients = maximumBannedClients;
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <summary>Gets the positive duration used when a criterion requests a ban.</summary>
        public TimeSpan BanDuration { get; }

        /// <summary>Gets the maximum number of stored temporary bans.</summary>
        public int MaximumBannedClients { get; }

        /// <summary>Gets a detached snapshot of unexpired bans; permanent network rules are separate.</summary>
        public IReadOnlyList<ClientBanInfo> BannedClients
        {
            get { lock (_gate) { CheckDisposed(); Prune(DateTimeOffset.UtcNow); return _bans.Values.ToArray(); } }
        }

        /// <summary>Gets a detached snapshot of permanent client keys, separate from temporary bans and network rules.</summary>
        public IReadOnlyList<string> PermanentBannedClients
        {
            get { lock (_gate) { CheckDisposed(); return _permanentBans.ToArray(); } }
        }

        /// <summary>Loads and enables an application-owned permanent-ban file before startup.</summary>
        /// <param name="path">A trusted application-selected path, not request input.</param>
        /// <returns>This module.</returns>
        /// <remarks>
        /// Missing files begin empty; malformed/unavailable stores throw. Existing in-memory permanent keys are merged.
        /// One cooperating .NET writer holds a lifetime lock file. Changes use same-directory atomic replacement.
        /// Temporary bans and policy callbacks/networks are not persisted. Do not edit the store externally while in use.
        /// </remarks>
        public ClientBanningModule WithPermanentBanStore(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A store path is required.", nameof(path));
            lock (_gate)
            {
                CheckConfiguration();
                if (_store != null) throw new InvalidOperationException("A permanent-ban store is already configured.");
                var store = new PermanentClientBanStore(path, MaximumBannedClients);
                try
                {
                    var merged = new HashSet<string>(_permanentBans, StringComparer.Ordinal);
                    merged.UnionWith(store.Load());
                    Prune(DateTimeOffset.UtcNow);
                    if (merged.Count + _bans.Keys.Count(k => !merged.Contains(k)) > MaximumBannedClients)
                        throw new InvalidOperationException("Loaded bans exceed the configured client capacity.");
                    store.Save(merged);
                    _permanentBans.UnionWith(merged);
                    foreach (var key in merged) _bans.Remove(key);
                    _store = store;
                }
                catch { store.Dispose(); throw; }
            }
            return this;
        }

        /// <summary>Adds a permanent client ban live, saving to the optional store before confirming the change.</summary>
        /// <param name="clientKey">The application-selected client key.</param>
        /// <returns>True if recorded or already permanent; false for a new key at capacity.</returns>
        public bool TryBanClientPermanently(string clientKey)
        {
            ValidateKey(clientKey);
            lock (_gate)
            {
                CheckDisposed();
                Prune(DateTimeOffset.UtcNow);
                if (_permanentBans.Contains(clientKey)) return true;
                if (!_bans.ContainsKey(clientKey) && BanCount >= MaximumBannedClients) return false;
                var next = new HashSet<string>(_permanentBans, StringComparer.Ordinal) { clientKey };
                _store?.Save(next);
                _permanentBans.Add(clientKey);
                _bans.Remove(clientKey);
                return true;
            }
        }

        /// <summary>Removes a permanent client ban live, saving before confirmation; network rules remain unchanged.</summary>
        /// <param name="clientKey">The application-selected client key.</param>
        /// <returns>Whether a permanent ban was removed.</returns>
        public bool TryUnbanClientPermanently(string clientKey)
        {
            ValidateKey(clientKey);
            lock (_gate)
            {
                CheckDisposed();
                if (!_permanentBans.Contains(clientKey)) return false;
                var next = new HashSet<string>(_permanentBans, StringComparer.Ordinal);
                next.Remove(clientKey);
                _store?.Save(next);
                return _permanentBans.Remove(clientKey);
            }
        }

        /// <summary>Adds literal/CIDR allow networks before startup. Allow rules take precedence over every ban.</summary>
        /// <param name="networks">IPv4/IPv6 literals or CIDRs, without DNS or scope identifiers.</param>
        /// <returns>This module.</returns>
        public ClientBanningModule WithAllowedNetworks(params string[] networks) => AddNetworks(_allowed, networks);

        /// <summary>Adds permanent literal/CIDR deny networks before startup; they never expire or appear in BannedClients.</summary>
        /// <param name="networks">IPv4/IPv6 literals or CIDRs, without DNS or scope identifiers.</param>
        /// <returns>This module.</returns>
        public ClientBanningModule WithDeniedNetworks(params string[] networks) => AddNetworks(_denied, networks);

        /// <summary>Adds a borrowed callback before startup; true requests a temporary ban of the selected client key.</summary>
        /// <param name="criterion">The callback; exceptions propagate through the existing module error pipeline.</param>
        /// <returns>This module.</returns>
        public ClientBanningModule WithCriterion(Func<IHttpContext, Task<bool>> criterion)
        {
            if (criterion == null) throw new ArgumentNullException(nameof(criterion));
            lock (_gate) { CheckConfiguration(); _criteria.Add(criterion); }
            return this;
        }

        /// <summary>Records or extends a temporary ban for this module only; returns false for a new key at capacity.</summary>
        /// <param name="clientKey">The application-selected key.</param>
        /// <param name="duration">A positive duration; an existing later expiration is preserved.</param>
        /// <returns>Whether the ban was recorded.</returns>
        public bool TryBanClient(string clientKey, TimeSpan duration)
        {
            ValidateKey(clientKey);
            ValidateDuration(duration, nameof(duration));
            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(duration);
            lock (_gate)
            {
                CheckDisposed();
                Prune(now);
                if (_permanentBans.Contains(clientKey)) return false;
                if (_bans.TryGetValue(clientKey, out var previous))
                    expires = expires > previous.ExpiresAt ? expires : previous.ExpiresAt;
                else if (BanCount >= MaximumBannedClients) return false;
                _bans[clientKey] = new ClientBanInfo(clientKey, expires);
                if (expires < _nextExpiry) _nextExpiry = expires;
                return true;
            }
        }

        /// <summary>Removes this module's temporary ban; it does not remove network rules or application-owned criterion data.</summary>
        /// <param name="clientKey">The application-selected key.</param>
        /// <returns>Whether an unexpired ban was removed.</returns>
        public bool TryUnbanClient(string clientKey)
        {
            ValidateKey(clientKey);
            lock (_gate) { CheckDisposed(); Prune(DateTimeOffset.UtcNow); return _bans.Remove(clientKey); }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _store?.Dispose();
                _store = null;
                _permanentBans.Clear();
                _bans.Clear(); _allowed.Clear(); _denied.Clear(); _criteria.Clear();
            }
        }

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            lock (_gate) { CheckDisposed(); }
            base.OnStart(cancellationToken);
        }

        /// <inheritdoc />
        protected override void OnBeforeLockConfiguration()
        {
            lock (_gate) { CheckDisposed(); _frozen = true; }
            base.OnBeforeLockConfiguration();
        }

        /// <inheritdoc />
        protected override async Task OnRequestAsync(IHttpContext context)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            Func<IHttpContext, Task<bool>>[] criteria;
            lock (_gate)
            {
                CheckDisposed();
                var address = context.RemoteEndPoint.Address;
                if (_allowed.Any(n => n.Contains(address))) return;
                if (_denied.Any(n => n.Contains(address))) throw HttpException.Forbidden();
                criteria = _criteria.ToArray();
            }
            var key = _clientKeySelector(context);
            ValidateKey(key);
            context.CancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CheckDisposed();
                Prune(DateTimeOffset.UtcNow);
                if (_bans.ContainsKey(key) || _permanentBans.Contains(key) || BanCount >= MaximumBannedClients) throw HttpException.Forbidden();
            }
            foreach (var criterion in criteria)
            {
                var ban = await criterion(context).ConfigureAwait(false);
                context.CancellationToken.ThrowIfCancellationRequested();
                if (ban)
                {
                    TryBanClient(key, BanDuration);
                    throw HttpException.Forbidden();
                }
            }
            // A ban or disposal may have completed while a callback was running.
            context.CancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CheckDisposed();
                Prune(DateTimeOffset.UtcNow);
                if (_bans.ContainsKey(key) || _permanentBans.Contains(key) || BanCount >= MaximumBannedClients) throw HttpException.Forbidden();
            }
        }

        private ClientBanningModule AddNetworks(List<ClientNetwork> destination, string[] networks)
        {
            if (networks == null) throw new ArgumentNullException(nameof(networks));
            var parsed = networks.Select(ClientNetwork.Parse).ToArray();
            lock (_gate) { CheckConfiguration(); destination.AddRange(parsed); }
            return this;
        }

        private void CheckConfiguration()
        {
            CheckDisposed();
            EnsureConfigurationNotLocked();
            if (_frozen) throw new InvalidOperationException("The configuration is locked.");
        }

        private void CheckDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ClientBanningModule));
        }

        private int BanCount => _bans.Count + _permanentBans.Count;

        internal static void ValidateKey(string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (string.IsNullOrWhiteSpace(key) || key.Length > 1024)
                throw new ArgumentException("A client key must be nonblank and at most 1024 characters.", nameof(key));
            for (var i = 0; i < key.Length; i++)
            {
                if (char.IsHighSurrogate(key[i]))
                {
                    if (++i >= key.Length || !char.IsLowSurrogate(key[i]))
                        throw new ArgumentException("A client key must be well-formed Unicode.", nameof(key));
                }
                else if (char.IsLowSurrogate(key[i]))
                    throw new ArgumentException("A client key must be well-formed Unicode.", nameof(key));
            }
        }

        private static void ValidateDuration(TimeSpan duration, string name)
        {
            if (duration <= TimeSpan.Zero || duration > DateTimeOffset.MaxValue - DateTimeOffset.UtcNow)
                throw new ArgumentOutOfRangeException(name);
        }

        private void Prune(DateTimeOffset now)
        {
            if (now < _nextExpiry) return;
            _nextExpiry = DateTimeOffset.MaxValue;
            foreach (var pair in _bans.ToArray())
            {
                if (pair.Value.ExpiresAt <= now) _bans.Remove(pair.Key);
                else if (pair.Value.ExpiresAt < _nextExpiry) _nextExpiry = pair.Value.ExpiresAt;
            }
        }
    }
}
