using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net.Internal;

namespace EmbedIO.Internal
{
    // Shared lifecycle hooks for separately packaged, owned HTTP backends.
    internal interface IHttpBackendListener
    {
        void Configure(RequestPolicyResolver? policy, ServerTelemetry? telemetry);
    }

    internal interface IHttpBackendContext
    {
        bool ResponseHeadersSent { get; }

        Task CloseAsync();

        Task AbortAsync(Exception cause);
    }

    internal interface IHttpBackendResponse
    {
        ResponseHeaderFields Fields { get; }
        long SentBodyBytes { get; }
        Security.SecurityHeadersState? SecurityHeaders { get; set; }
    }

    internal interface IHttpBackendRequest : IRequestPolicyTarget
    {
        RequestPolicySnapshot? CapturedRequestPolicy { get; set; }
        RawRequestBodyLimitException? KnownBodyRefusal { get; set; }

        void SetBodyPolicy(RequestBodyPolicy policy);

        Task CompleteBodyInputAsync(CancellationToken token);

        bool IsBodyReadTimeoutFailure(Exception error);
    }
}
