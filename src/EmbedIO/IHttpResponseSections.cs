using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Optional response capability for interim and trailing field sections.</summary>
    /// <remarks>
    /// Detect this capability on the response before opting in. Existing IHttpResponse
    /// implementations need not implement it. Supplied collections are copied; finish
    /// section configuration before the handler returns and do not mutate a collection
    /// concurrently with a call. The application selects fields whose definitions allow
    /// trailer use. Ordinary response headers remain separate from these sections.
    /// </remarks>
    public interface IHttpResponseSections : IHttpResponse
    {
        /// <summary>Sends a non-final informational response without changing final response metadata.</summary>
        /// <param name="statusCode">A 1xx status other than 101, which requires an explicit protocol handshake.</param>
        /// <param name="headers">Fields for this interim section, without body framing or connection control.</param>
        /// <param name="cancellationToken">Cancellation for the serialized transport write.</param>
        /// <returns>A task completing after the interim section has been written.</returns>
        /// <remarks>HTTP/1.0, accepted tunnels and responses whose final headers were sent reject the operation.</remarks>
        Task SendInformationalAsync(int statusCode, WebHeaderCollection headers, CancellationToken cancellationToken = default);

        /// <summary>Reserves an ending trailer section before final headers are sent.</summary>
        /// <param name="fieldNames">Names that may appear in trailers; at least one is required.</param>
        /// <remarks>
        /// HTTP/1.1 requires chunked framing and no configured Content-Length. HTTP/2
        /// and HTTP/3 retain exact Content-Length checking. HEAD, bodyless responses and
        /// accepted tunnels cannot carry trailers. Invalid declarations leave the response unchanged.
        /// </remarks>
        void DeclareTrailers(params string[] fieldNames);

        /// <summary>Copies declared trailing fields to send once during response completion.</summary>
        /// <param name="trailers">The snapshot source, containing only declared, trailer-permitted fields.</param>
        /// <remarks>
        /// Configure trailers after awaited body writes and before disposing output or
        /// completing the handler. A failed call does not replace a valid prior snapshot.
        /// Trailers are not merged into response headers. Declared names need not all be sent.
        /// </remarks>
        void SetTrailers(WebHeaderCollection trailers);
    }
}
