using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Optional context capability for accepting WebTransport sessions over HTTP/3.</summary>
    /// <remarks>
    /// <para>Contexts of HTTP/2 and HTTP/3 requests implement this interface; HTTP/1.1 contexts do not.
    /// <see cref="IsWebTransportRequest"/> is <see langword="true"/> only for a WebTransport request on an HTTP/3
    /// connection that negotiated it, on a server configured with <see cref="WebServerOptions.WebTransport"/>.
    /// Existing context interfaces are unchanged.</para>
    /// <para>Authorize the request before accepting it; to refuse it, throw an <see cref="HttpException"/>
    /// such as 403 instead of calling <see cref="AcceptWebTransportAsync"/>.</para>
    /// </remarks>
    public interface IWebTransportContext : IHttpContext
    {
        /// <summary>Gets whether the request is a WebTransport session request that this connection negotiated.</summary>
        bool IsWebTransportRequest { get; }

        /// <summary>Accepts the session: answers the request with 200 and starts delivering its streams and datagrams.</summary>
        /// <param name="cancellationToken">Cancels the acceptance while it is still sending the response.</param>
        /// <returns>The session. Keep the handler running while the session is in use: when the handler returns,
        /// the server closes a session that is still open, with error code 0.</returns>
        /// <exception cref="System.InvalidOperationException">The request is not a WebTransport request, or a
        /// session or other protocol was already accepted on it.</exception>
        Task<WebTransportSession> AcceptWebTransportAsync(CancellationToken cancellationToken = default);
    }
}
