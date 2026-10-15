using System;

namespace EmbedIO.Internal
{
    // Implemented by engine request types so the server can apply its request policies.
    // Body policies wrap InputStream; transport framing checks keep using the raw stream.
    internal interface IRequestPolicyTarget
    {
        // Received header field lines, excluding the HTTP/1 request line and pseudo-headers.
        int HeaderFieldCount { get; }

        // Sum of the name and value lengths of those field lines.
        long HeaderFieldBytes { get; }

        // Must be applied before InputStream is first read. onExceeded, when given, runs
        // once, on the reading thread, when a read crosses the limit.
        void LimitBody(long maximumBytes, Action? onExceeded);

        // Must be applied before InputStream is first read. onTimeout, when given, runs
        // once, on the reading thread, when a read waits longer than the timeout for data.
        void TimeBodyReads(TimeSpan timeout, Action? onTimeout);

        // Must be applied before InputStream is first read. Counts the body bytes the
        // application reads into the request's telemetry scope.
        void CountBody(RequestTelemetry scope);
    }
}
