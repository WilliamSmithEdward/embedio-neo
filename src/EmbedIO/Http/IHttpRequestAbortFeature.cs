using System.Threading;

namespace EmbedIO
{
    /// <summary>Optional support for observing cancellation of an individual HTTP request.</summary>
    /// <remarks>Successful completion does not cancel this token. Detection is best effort and
    /// does not replace application deadlines. Existing context cancellation behavior is unchanged.</remarks>
    public interface IHttpRequestAbortFeature
    {
        /// <summary>Gets a stable token for this request and the selected HTTP/1 input policy.</summary>
        /// <param name="cancelOnHttp1InputEnd">Also cancel when HTTP/1 input ends, including a valid
        /// TCP half-close. The default preserves half-close clients. Ignored for HTTP/2 and HTTP/3.</param>
        /// <returns>A request-owned token, linked to the context token current at acquisition.</returns>
        CancellationToken GetRequestAbortedToken(bool cancelOnHttp1InputEnd = false);
    }
}
