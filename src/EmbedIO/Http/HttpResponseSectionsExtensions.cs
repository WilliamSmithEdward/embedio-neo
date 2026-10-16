using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO
{
    /// <summary>Enum conveniences for the optional response field-section capability.</summary>
    public static class HttpResponseSectionsExtensions
    {
        /// <summary>Sends an informational section through the existing integer API.</summary>
        /// <param name="this">The response with field-section support.</param>
        /// <param name="statusCode">The informational status code.</param>
        /// <param name="headers">The section's headers.</param>
        /// <param name="cancellationToken">Write cancellation.</param>
        /// <returns>The original capability's send task.</returns>
        public static Task SendInformationalAsync(this IHttpResponseSections @this, HttpStatusCode statusCode,
            WebHeaderCollection headers, CancellationToken cancellationToken = default)
        {
            if (@this is null) throw new NullReferenceException();
            return @this.SendInformationalAsync((int)statusCode, headers, cancellationToken);
        }
    }
}
