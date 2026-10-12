using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Forwarding.Internal;
using EmbedIO.Security.Internal;

namespace EmbedIO.Forwarding
{
    /// <summary>
    /// <para>Resolves the original client address, scheme and host of requests that reach the
    /// server through trusted reverse proxies, from the <c>Forwarded</c> or <c>X-Forwarded-*</c>
    /// headers those proxies add. The result is a <see cref="ForwardedRequest"/>, read with
    /// <see cref="ForwardedRequestExtensions.GetForwardedRequest"/>; the request's own connection
    /// values are not changed.</para>
    /// <para>Headers are read only from connections whose address is in the trusted proxies, and
    /// only as many hops as <see cref="ForwardLimit"/> allows, each further hop only while the
    /// address it names is itself trusted. A forwarded host is used only if it is in the allowed
    /// hosts. Anything malformed or conflicting leaves the connection's values in place.</para>
    /// <para>The module never rejects a request; it records its <see cref="ForwardedOutcome"/>.
    /// Place it before every module that reads the effective values.</para>
    /// </summary>
    public sealed class ForwardedHeadersModule : WebModuleBase
    {
        private const int MaxHeaderLength = 8192;

        private readonly ForwardedHeaderSources _sources;
        private readonly List<ClientNetwork> _trustedProxies = new();
        private readonly List<(string Host, string? Port)> _allowedHosts = new();
        private int _forwardLimit = 1;

        /// <summary>
        /// Initializes a new instance of the <see cref="ForwardedHeadersModule"/> class.
        /// </summary>
        /// <param name="baseRoute">The base route of the requests to process, usually <c>/</c>.</param>
        /// <param name="sources">The headers your proxies set. Read only headers your proxy always
        /// writes or replaces, or a client can supply them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="baseRoute"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="sources"/> is <see cref="ForwardedHeaderSources.None"/>
        /// or contains undefined values.</exception>
        public ForwardedHeadersModule(string baseRoute, ForwardedHeaderSources sources)
            : base(baseRoute)
        {
            const ForwardedHeaderSources all = ForwardedHeaderSources.Forwarded | ForwardedHeaderSources.XForwardedFor
                | ForwardedHeaderSources.XForwardedProto | ForwardedHeaderSources.XForwardedHost;
            if (sources == ForwardedHeaderSources.None || (sources & ~all) != 0)
                throw new ArgumentException("Choose the forwarding headers your proxies set.", nameof(sources));

            _sources = sources;
        }

        /// <inheritdoc />
        public override bool IsFinalHandler => false;

        /// <summary>Gets the headers this module reads.</summary>
        public ForwardedHeaderSources Sources => _sources;

        /// <summary>
        /// Gets or sets the most proxy hops to apply, from 1 (the default) to 16. Set it to the number
        /// of trusted proxies in front of the server.
        /// </summary>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 16.</exception>
        public int ForwardLimit
        {
            get => _forwardLimit;
            set
            {
                EnsureConfigurationNotLocked();
                if (value < 1 || value > 16)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Between 1 and 16 hops.");

                _forwardLimit = value;
            }
        }

        /// <summary>
        /// Adds the addresses of trusted proxies, as IPv4 or IPv6 literals or CIDR networks such as
        /// <c>10.0.0.0/8</c>. Host names are not accepted. At least one is required.
        /// </summary>
        /// <param name="networks">The addresses or networks.</param>
        /// <returns>This module.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="networks"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">An entry is not an address or network.</exception>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public ForwardedHeadersModule WithTrustedProxies(params string[] networks)
        {
            if (networks == null)
                throw new ArgumentNullException(nameof(networks));

            var parsed = networks.Select(ClientNetwork.Parse).ToArray();
            EnsureConfigurationNotLocked();
            _trustedProxies.AddRange(parsed);
            return this;
        }

        /// <summary>
        /// Adds hosts a trusted proxy may forward, such as <c>example.com</c> or <c>example.com:8443</c>.
        /// An entry without a port allows any port. Forwarded hosts are ignored unless allowed, and
        /// <see cref="ForwardedHeaderSources.XForwardedHost"/> requires at least one.
        /// </summary>
        /// <param name="hosts">The hosts.</param>
        /// <returns>This module.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="hosts"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">An entry is not a host with an optional port.</exception>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        public ForwardedHeadersModule WithAllowedHosts(params string[] hosts)
        {
            if (hosts == null)
                throw new ArgumentNullException(nameof(hosts));

            var parsed = hosts.Select(host =>
            {
                var authority = host == null ? null : ForwardedHeaderParser.Authority(host);
                return authority == null
                    ? throw new ArgumentException($"'{host}' is not a host with an optional port.", nameof(hosts))
                    : SplitAuthority(authority);
            }).ToArray();
            EnsureConfigurationNotLocked();
            _allowedHosts.AddRange(parsed);
            return this;
        }

        /// <inheritdoc />
        protected override void OnStart(CancellationToken cancellationToken)
        {
            if (_trustedProxies.Count == 0)
                throw new InvalidOperationException("A forwarded-headers module needs at least one trusted proxy.");

            if ((_sources & ForwardedHeaderSources.XForwardedHost) != 0 && _allowedHosts.Count == 0)
                throw new InvalidOperationException("Reading X-Forwarded-Host requires allowed hosts.");

            base.OnStart(cancellationToken);
        }

        /// <inheritdoc />
        protected override Task OnRequestAsync(IHttpContext context)
        {
            if (context is null) throw new ArgumentNullException(nameof(context));

            context.Items[ForwardedRequestExtensions.ItemKey] = Resolve(context);
            return Task.CompletedTask;
        }

        private static (string Host, string? Port) SplitAuthority(string authority)
        {
            var colon = authority.LastIndexOf(':');
            return colon > authority.LastIndexOf(']') ? (authority.Substring(0, colon), authority.Substring(colon + 1)) : (authority, null);
        }

        private bool IsTrusted(IPAddress address) => _trustedProxies.Any(network => network.Contains(address));

        private bool IsAllowedHost(string authority)
        {
            var (host, port) = SplitAuthority(authority);
            return _allowedHosts.Any(allowed => allowed.Host == host && (allowed.Port == null || allowed.Port == port));
        }

        private string? Header(IHttpContext context, ForwardedHeaderSources source, string name)
        {
            if ((_sources & source) == 0)
                return null;

            var values = context.Request.Headers.GetValues(name);
            return values == null || values.Length == 0 ? null : string.Join(",", values);
        }

        private ForwardedRequest Resolve(IHttpContext context)
        {
            var physical = ForwardedRequest.Physical(context, ForwardedOutcome.UntrustedPeer);
            if (!IsTrusted(physical.ClientAddress))
                return physical;

            ForwardedRequest Unchanged(ForwardedOutcome outcome)
                => new(outcome, physical.ClientAddress, physical.Scheme, null, 0, false, physical.PhysicalRemoteEndPoint, physical.PhysicalIsSecure);

            var forwarded = Header(context, ForwardedHeaderSources.Forwarded, "Forwarded");
            var forHeader = Header(context, ForwardedHeaderSources.XForwardedFor, "X-Forwarded-For");
            var protoHeader = Header(context, ForwardedHeaderSources.XForwardedProto, "X-Forwarded-Proto");
            var hostHeader = Header(context, ForwardedHeaderSources.XForwardedHost, "X-Forwarded-Host");
            var hasLegacy = forHeader != null || protoHeader != null || hostHeader != null;
            if (forwarded == null && !hasLegacy)
                return Unchanged(ForwardedOutcome.NoHeaders);

            // Two descriptions of the same chain may disagree, and neither can be preferred safely.
            if (forwarded != null && hasLegacy)
                return Unchanged(ForwardedOutcome.Conflict);

            var length = (forwarded?.Length ?? 0) + (forHeader?.Length ?? 0) + (protoHeader?.Length ?? 0) + (hostHeader?.Length ?? 0);
            var hops = length > MaxHeaderLength
                ? null
                : forwarded != null
                    ? ForwardedHeaderParser.ParseForwarded(forwarded)
                    : ForwardedHeaderParser.ParseXForwarded(forHeader, protoHeader, hostHeader);
            if (hops == null)
                return Unchanged(ForwardedOutcome.Malformed);

            // Walk from the nearest proxy outward. Each hop says who sent the request to the proxy
            // that recorded it, and how; a further hop is trusted only if that sender is a trusted proxy.
            var client = physical.ClientAddress;
            var scheme = physical.Scheme;
            string? host = null;
            var hostRejected = false;
            var applied = 0;
            for (var i = hops.Count - 1; i >= 0 && applied < _forwardLimit; i--)
            {
                var hop = hops[i];
                if (hop.Proto != null)
                    scheme = hop.Proto;

                if (hop.Host != null)
                {
                    if (IsAllowedHost(hop.Host))
                    {
                        host = hop.Host;
                        hostRejected = false;
                    }
                    else
                    {
                        host = null;
                        hostRejected = true;
                    }
                }

                applied++;
                if (hop.Client == null)
                    break;

                client = hop.Client;
                if (!IsTrusted(client))
                    break;
            }

            return new ForwardedRequest(
                applied > 0 ? ForwardedOutcome.Applied : ForwardedOutcome.NoHeaders,
                client,
                scheme,
                host,
                applied,
                hostRejected,
                physical.PhysicalRemoteEndPoint,
                physical.PhysicalIsSecure);
        }
    }
}
