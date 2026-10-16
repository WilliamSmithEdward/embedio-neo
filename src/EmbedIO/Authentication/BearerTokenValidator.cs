using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Authentication
{
    /// <summary>Validates a bearer credential and returns an authenticated principal, or null for an invalid credential.</summary>
    /// <param name="token">The credential, without the scheme. Do not log or retain it unnecessarily.</param>
    /// <param name="cancellationToken">Cancellation of request processing.</param>
    /// <returns>An authenticated principal, or null. Unexpected failures propagate to the application's error handling.</returns>
    public delegate Task<ClaimsPrincipal?> BearerTokenValidator(string token, CancellationToken cancellationToken);
}
