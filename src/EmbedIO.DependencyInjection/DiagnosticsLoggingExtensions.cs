using System;
using Microsoft.Extensions.Logging;

namespace EmbedIO.DependencyInjection
{
    /// <summary>Optional forwarding of process-wide EmbedIO diagnostics to application logging.</summary>
    public static class DiagnosticsLoggingExtensions
    {
        /// <summary>Registers forwarding to the application's logger factory.</summary>
        /// <param name="factory">The borrowed factory. It remains owned by the application.</param>
        /// <returns>A registration to dispose after stopping the application's servers.</returns>
        /// <remarks>
        /// Register once per destination at application startup. The source is process-wide,
        /// not per server. Source and provider filters remain unchanged. Disposing the returned
        /// registration removes only its own listener and does not dispose the factory.
        /// This forwards Neo's TraceEvent output; activity and TraceData events are ignored.
        /// </remarks>
        public static EmbedIODiagnosticsRegistration ForwardEmbedIODiagnostics(this ILoggerFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            return new EmbedIODiagnosticsRegistration(factory.CreateLogger("EmbedIO"));
        }
    }
}
