using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Optional context capability for negotiated protocol tunnels.</summary>
    /// <remarks>Existing context interfaces are unchanged. Backends without this capability do not implement it.</remarks>
    public interface IHttpTunnelContext : IHttpContext
    {
        /// <summary>Gets offered Upgrade/extended CONNECT protocols; an ordinary CONNECT has no named protocol.</summary>
        IReadOnlyList<string> RequestedTunnelProtocols { get; }
        /// <summary>Accepts an offered protocol or, with null, an eligible ordinary CONNECT.</summary>
        /// <param name="protocol">The selected offered protocol, or null for ordinary CONNECT.</param>
        /// <param name="useCapsules">Whether the negotiated extension uses reliable capsules.</param>
        /// <param name="cancellationToken">Cancellation for handshake commitment.</param>
        /// <returns>The stream handoff. Keep the handler active until its tunnel work finishes.</returns>
        /// <remarks>Context completion must join tunnel output before finishing the response. This is not an automatic proxy service.</remarks>
        Task<HttpTunnel> AcceptTunnelAsync(string? protocol, bool useCapsules = false, CancellationToken cancellationToken = default);
    }
}
