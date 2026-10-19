using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security.Internal;

namespace EmbedIO.Security
{
    /// <summary>
    /// A module to ban clients by IP address, based on TCP requests-per-second or RegEx matches on log messages.
    /// </summary>
    /// <seealso cref="WebModuleBase" />
    public class IPBanningModule : WebModuleBase, IDisposable
    {
        /// <summary>
        /// The default ban minutes.
        /// </summary>
        public const int DefaultBanMinutes = 30;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="IPBanningModule" /> class.
        /// </summary>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="whitelist">A collection of valid IPs that never will be banned.</param>
        /// <param name="banMinutes">Minutes that an IP will remain banned.</param>
        public IPBanningModule(
            string baseRoute = "/",
            IEnumerable<string>? whitelist = null,
            int banMinutes = DefaultBanMinutes
        ) : base(baseRoute)
        {
            Configuration = new IPBanningConfiguration(banMinutes);
            AddToWhitelist(whitelist);
            IPBanningExecutor.Register(baseRoute, Configuration);
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="IPBanningModule"/> class.
        /// </summary>
        ~IPBanningModule() => Dispose(false);

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        // The most recent requests this module handled, so a message written just after a
        // request ends can still name its client. Retiring oldest first bounds the map; a
        // retired or never-tracked id is charged to nobody rather than to the wrong client.
        private const int MaximumTrackedRequests = 4096;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPAddress> _requestAddresses = new(
        );
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _trackedRequests = new();

        /// <summary>
        /// Gets the client address.
        /// </summary>
        /// <value>
        /// The client address of whichever request most recently passed through this module.
        /// </value>
        /// <remarks>
        /// This is not request-scoped. Concurrent requests overwrite it, so it reports the last
        /// client seen rather than the client of any particular request, and nothing in this
        /// module decides anything by reading it.
        /// </remarks>
        public IPAddress? ClientAddress { get; private set; }
        internal IPBanningConfiguration Configuration { get; }

        /// <summary>Gets a snapshot of this module's banned IP addresses.</summary>
        public IEnumerable<BanInfo> BannedIPs =>
            IPBanningExecutor.WithInstance(Configuration, instance => instance.BlackList);

        /// <summary>Bans a client in this module only.</summary>
        /// <param name="address">The client address.</param>
        /// <param name="banMinutes">The ban duration in minutes.</param>
        /// <param name="isExplicit">Whether this is an explicit ban.</param>
        /// <returns>Whether the ban was recorded.</returns>
        public bool TryBanClient(IPAddress address, int banMinutes, bool isExplicit = true) =>
            TryBanClient(address, DateTime.Now.AddMinutes(banMinutes), isExplicit);

        /// <summary>Bans a client in this module only.</summary>
        /// <param name="address">The client address.</param>
        /// <param name="banDuration">The ban duration.</param>
        /// <param name="isExplicit">Whether this is an explicit ban.</param>
        /// <returns>Whether the ban was recorded.</returns>
        public bool TryBanClient(IPAddress address, TimeSpan banDuration, bool isExplicit = true) =>
            TryBanClient(address, DateTime.Now.Add(banDuration), isExplicit);

        /// <summary>Bans a client in this module only.</summary>
        /// <param name="address">The client address.</param>
        /// <param name="banUntil">The expiration time.</param>
        /// <param name="isExplicit">Whether this is an explicit ban.</param>
        /// <returns>Whether the ban was recorded.</returns>
        public bool TryBanClient(IPAddress address, DateTime banUntil, bool isExplicit = true) =>
            IPBanningExecutor.WithInstance(Configuration, instance => instance.TryBanIP(address, isExplicit, banUntil));

        /// <summary>Unbans a client and clears its criterion data in this module only.</summary>
        /// <param name="address">The client address.</param>
        /// <returns>Whether a ban was removed.</returns>
        public bool TryUnbanClient(IPAddress address) =>
            IPBanningExecutor.WithInstance(Configuration, instance => instance.TryRemoveBlackList(address));

        /// <summary>
        /// Registers the criterion.
        /// </summary>
        /// <param name="criterion">The criterion.</param>
        public void RegisterCriterion(IIPBanningCriterion criterion) => Configuration.RegisterCriterion(criterion);

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Gets the union of banned IPs from all live modules registered under the route.
        /// For duplicate addresses, the entry with the latest expiration is returned.
        /// </summary>
        /// <param name="baseRoute">The base route.</param>
        /// <returns>
        /// A collection of <see cref="BanInfo" /> in the blacklist.
        /// </returns>
        /// <exception cref="ArgumentException">baseRoute</exception>
        public static IEnumerable<BanInfo> GetBannedIPs(string baseRoute = "/") =>
            [.. IPBanningExecutor
                .WithInstances(baseRoute, instance => instance.BlackList)
                .SelectMany(list => list)
                .GroupBy(info => info.IPAddress)
                .Select(group => group
                    .OrderByDescending(info => info.ExpiresAt)
                    .First())
            ];

        /// <summary>
        /// Tries to ban an IP in all live modules registered under the route.
        /// </summary>
        /// <param name="address">The IP address to ban.</param>
        /// <param name="banMinutes">Minutes that the IP will remain banned.</param>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="isExplicit"><c>true</c> if the IP was explicitly banned.</param>
        /// <returns>
        ///   <c>true</c> if the IP was added to the blacklist; otherwise, <c>false</c>.
        /// </returns>
        public static bool TryBanIP(
            IPAddress address,
            int banMinutes,
            string baseRoute = "/",
            bool isExplicit = true
        ) => TryBanIP(address, DateTime.Now.AddMinutes(banMinutes), baseRoute, isExplicit);

        /// <summary>
        /// Tries to ban an IP in all live modules registered under the route.
        /// </summary>
        /// <param name="address">The IP address to ban.</param>
        /// <param name="banDuration">A <see cref="TimeSpan" /> specifying the duration that the IP will remain banned.</param>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="isExplicit"><c>true</c> if the IP was explicitly banned.</param>
        /// <returns>
        ///   <c>true</c> if the IP was added to the blacklist; otherwise, <c>false</c>.
        /// </returns>
        public static bool TryBanIP(
            IPAddress address,
            TimeSpan banDuration,
            string baseRoute = "/",
            bool isExplicit = true
        ) => TryBanIP(address, DateTime.Now.Add(banDuration), baseRoute, isExplicit);

        /// <summary>
        /// Tries to ban an IP in all live modules registered under the route.
        /// </summary>
        /// <param name="address">The IP address to ban.</param>
        /// <param name="banUntil">A <see cref="DateTime" /> specifying the expiration time of the ban.</param>
        /// <param name="baseRoute">The base route.</param>
        /// <param name="isExplicit"><c>true</c> if the IP was explicitly banned.</param>
        /// <returns>
        ///   <c>true</c> if the IP was added to the blacklist; otherwise, <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentException">baseRoute</exception>
        public static bool TryBanIP(
            IPAddress address,
            DateTime banUntil,
            string baseRoute = "/",
            bool isExplicit = true
        ) =>
            IPBanningExecutor
                .WithInstances(baseRoute, instance => instance.TryBanIP(address, isExplicit, banUntil))
                .Any(result => result);

        /// <summary>
        /// Tries to unban an IP in all live modules registered under the route.
        /// </summary>
        /// <param name="address">The IP address.</param>
        /// <param name="baseRoute">The base route.</param>
        /// <returns>
        ///   <c>true</c> if the IP was removed from the blacklist; otherwise, <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentException">baseRoute</exception>
        public static bool TryUnbanIP(IPAddress address, string baseRoute = "/") =>
            IPBanningExecutor
                .WithInstances(baseRoute, instance => instance.TryRemoveBlackList(address))
                .Any(result => result);

        internal void AddToWhitelist(IEnumerable<string>? whitelist) =>
            Configuration
                .AddToWhitelistAsync(whitelist)
                .GetAwaiter()
                .GetResult();

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            Configuration.Lock();

            base.OnStart(cancellationToken);
        }

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new System.NullReferenceException();

            var address = context.Request.RemoteEndPoint.Address;
            ClientAddress = address;
            Track(context, address);
            return Configuration.CheckClient(address);
        }

        // Diagnostic messages name their request as "[id] ...", and that id is the only thing
        // tying a message to the client that caused it. A criterion that reads ClientAddress
        // instead would charge the message to whichever request passed through this module
        // last, from any thread, which is not the same client.
        //
        // An entry outlives its request deliberately. A request's completion message, the one
        // carrying its status, is written after the context has closed, so releasing the entry
        // on close would leave exactly the message the criteria care about unattributable.
        // The oldest entries retire instead, which bounds the map without a timer.
        private void Track(IHttpContext context, IPAddress address)
        {
            if (!_requestAddresses.TryAdd(context.Id, address))
                return;

            _trackedRequests.Enqueue(context.Id);

            while (_trackedRequests.Count > MaximumTrackedRequests && _trackedRequests.TryDequeue(out var oldest))
                _requestAddresses.TryRemove(oldest, out _);
        }

        // The client a diagnostic message belongs to, or null when the message names no request
        // this module handled. Refusing to guess is the point: a message from a route this
        // module does not guard must not be charged to one of its own clients.
        internal IPAddress? ResolveMessageClient(string message)
        {
            if (message == null || message.Length < 3 || message[0] != '[') return null;

            var end = EmbedIO.Internal.StringOperations.IndexOfOrdinal(message, ']');

            if (end < 2) return null;

            return _requestAddresses.TryGetValue(message.Substring(1, end - 1), out var address) ? address : null;
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                IPBanningExecutor.Remove(Configuration);
            }

            _disposed = true;
        }
    }
}
