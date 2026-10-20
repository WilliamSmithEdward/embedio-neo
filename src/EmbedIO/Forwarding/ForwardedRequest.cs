using System;
using System.Net;

namespace EmbedIO.Forwarding
{
    /// <summary>
    /// The proxy headers <see cref="ForwardedHeadersModule"/> reads. Combine the ones your proxy sets.
    /// </summary>
    [Flags]
    public enum ForwardedHeaderSources
    {
        /// <summary>No headers; the module changes nothing.</summary>
        None = 0,

        /// <summary>The standard <c>Forwarded</c> header (RFC 7239): <c>for</c>, <c>proto</c> and <c>host</c>.</summary>
        Forwarded = 1,

        /// <summary><c>X-Forwarded-For</c>: the client address.</summary>
        XForwardedFor = 2,

        /// <summary><c>X-Forwarded-Proto</c>: the scheme the client used.</summary>
        XForwardedProto = 4,

        /// <summary><c>X-Forwarded-Host</c>: the host the client requested. Requires allowed hosts.</summary>
        XForwardedHost = 8
    }

    /// <summary>
    /// What <see cref="ForwardedHeadersModule"/> did with a request's proxy headers.
    /// </summary>
    public enum ForwardedOutcome
    {
        /// <summary>No forwarded-headers module handled the request; the values are the connection's own.</summary>
        NotProcessed,

        /// <summary>The connection does not come from a trusted proxy, so its headers were ignored.</summary>
        UntrustedPeer,

        /// <summary>The connection comes from a trusted proxy, which sent no forwarding headers.</summary>
        NoHeaders,

        /// <summary>One or more hops were applied.</summary>
        Applied,

        /// <summary>A header value was malformed or over the module's bounds; nothing was applied.</summary>
        Malformed,

        /// <summary>Both <c>Forwarded</c> and <c>X-Forwarded-*</c> headers arrived; nothing was applied.</summary>
        Conflict
    }

    /// <summary>
    /// <para>A request's effective client, scheme and host as resolved from trusted proxy headers,
    /// next to the connection's own values, which are never changed. Get it with
    /// <see cref="ForwardedRequestExtensions.GetForwardedRequest"/>.</para>
    /// <para>Code that needs the client behind the proxy, such as logging, rate limiting or
    /// redirects, reads these values; <see cref="IHttpRequest.RemoteEndPoint"/>,
    /// <see cref="IHttpRequest.IsSecureConnection"/> and <see cref="IHttpRequest.Url"/> stay
    /// the connection's own.</para>
    /// </summary>
    public sealed class ForwardedRequest
    {
        internal ForwardedRequest(
            ForwardedOutcome outcome,
            IPAddress clientAddress,
            string scheme,
            string? host,
            int hopsApplied,
            bool hostRejected,
            IPEndPoint physicalRemoteEndPoint,
            bool physicalIsSecure
        )
        {
            Outcome = outcome;
            ClientAddress = clientAddress;
            Scheme = scheme;
            Host = host;
            HopsApplied = hopsApplied;
            HostRejected = hostRejected;
            PhysicalRemoteEndPoint = physicalRemoteEndPoint;
            PhysicalIsSecure = physicalIsSecure;
        }

        /// <summary>Gets what the module did with the request's proxy headers.</summary>
        public ForwardedOutcome Outcome { get; }

        /// <summary>Gets the effective client address: the original client's when hops were applied,
        /// otherwise the connection's. IPv4-mapped addresses are unmapped.</summary>
        public IPAddress ClientAddress { get; }

        /// <summary>Gets the effective scheme, <c>http</c> or <c>https</c>.</summary>
        public string Scheme { get; }

        /// <summary>Gets a value indicating whether the effective scheme is <c>https</c>.</summary>
        public bool IsSecure => Scheme == "https";

        /// <summary>Gets the host the client requested, as forwarded by a trusted proxy and found in the
        /// module's allowed hosts, or <see langword="null"/>. The request's own <c>Host</c> header is
        /// client input and is never reported here.</summary>
        public string? Host { get; }

        /// <summary>Gets the number of proxy hops applied.</summary>
        public int HopsApplied { get; }

        /// <summary>Gets a value indicating whether a trusted proxy forwarded a host that is not allowed,
        /// so it was ignored.</summary>
        public bool HostRejected { get; }

        /// <summary>Gets the connection's remote end point, the nearest proxy when there is one.</summary>
        public IPEndPoint PhysicalRemoteEndPoint { get; }

        /// <summary>Gets a value indicating whether the connection itself uses TLS.</summary>
        public bool PhysicalIsSecure { get; }

        internal static ForwardedRequest Physical(IHttpContext context, ForwardedOutcome outcome)
        {
            var remote = context.RemoteEndPoint;
            var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
            var secure = context.Request.IsSecureConnection;
            return new ForwardedRequest(outcome, address, secure ? "https" : "http", null, 0, false, remote, secure);
        }
    }

    /// <summary>
    /// Provides <see cref="GetForwardedRequest"/>.
    /// </summary>
    public static class ForwardedRequestExtensions
    {
        internal static readonly object ItemKey = new();

        /// <summary>
        /// Gets the request's effective client, scheme and host. Without a <see cref="ForwardedHeadersModule"/>
        /// before the caller, the result holds the connection's own values with <see cref="ForwardedOutcome.NotProcessed"/>.
        /// </summary>
        /// <param name="this">The request's context.</param>
        /// <returns>The effective values.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="this"/> is <see langword="null"/>.</exception>
        public static ForwardedRequest GetForwardedRequest(this IHttpContext @this)
        {
            if (@this == null)
                throw new ArgumentNullException(nameof(@this));

            return @this.Items.TryGetValue(ItemKey, out var value) && value is ForwardedRequest forwarded
                ? forwarded
                : ForwardedRequest.Physical(@this, ForwardedOutcome.NotProcessed);
        }
    }
}
