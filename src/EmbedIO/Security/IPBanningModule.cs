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
        public IPBanningModule(string baseRoute = "/",
                               IEnumerable<string>? whitelist = null,
                               int banMinutes = DefaultBanMinutes)
            : base(baseRoute)
        {
            Configuration = new IPBanningConfiguration(banMinutes);
            AddToWhitelist(whitelist);
            IPBanningExecutor.Register(baseRoute, Configuration);
        }

        /// <summary>
        /// Finalizes an instance of the <see cref="IPBanningModule"/> class.
        /// </summary>
        ~IPBanningModule()
        {
            Dispose(false);
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <summary>
        /// Gets the client address.
        /// </summary>
        /// <value>
        /// The client address.
        /// </value>
        public IPAddress? ClientAddress { get; private set; }

        internal IPBanningConfiguration Configuration { get; }

        /// <summary>Gets a snapshot of this module's banned IP addresses.</summary>
        public IEnumerable<BanInfo> BannedIPs => IPBanningExecutor.WithInstance(Configuration, instance => instance.BlackList);

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
        public void RegisterCriterion(IIPBanningCriterion criterion) =>
            Configuration.RegisterCriterion(criterion);

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
            IPBanningExecutor.WithInstances(baseRoute, instance => instance.BlackList).SelectMany(list => list)
                .GroupBy(info => info.IPAddress).Select(group => group.OrderByDescending(info => info.ExpiresAt).First()).ToList();

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
        public static bool TryBanIP(IPAddress address, int banMinutes, string baseRoute = "/", bool isExplicit = true) =>
            TryBanIP(address, DateTime.Now.AddMinutes(banMinutes), baseRoute, isExplicit);

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
        public static bool TryBanIP(IPAddress address, TimeSpan banDuration, string baseRoute = "/", bool isExplicit = true) =>
            TryBanIP(address, DateTime.Now.Add(banDuration), baseRoute, isExplicit);

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
        public static bool TryBanIP(IPAddress address, DateTime banUntil, string baseRoute = "/", bool isExplicit = true)
        {
            return IPBanningExecutor.WithInstances(baseRoute, instance => instance.TryBanIP(address, isExplicit, banUntil)).Any(result => result);
        }

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
            IPBanningExecutor.WithInstances(baseRoute, instance => instance.TryRemoveBlackList(address)).Any(result => result);

        internal void AddToWhitelist(IEnumerable<string>? whitelist) =>
            Configuration.AddToWhitelistAsync(whitelist).GetAwaiter().GetResult();

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            Configuration.Lock();

            base.OnStart(cancellationToken);
        }

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            ClientAddress = context.Request.RemoteEndPoint.Address;
            return Configuration.CheckClient(ClientAddress);
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
