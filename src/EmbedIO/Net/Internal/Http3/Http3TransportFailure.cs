#if NET10_0_OR_GREATER
using System;
using System.Runtime.Versioning;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    // Classifies the engine's stream and connection failures for HTTP/3 decisions
    // without converting every task or read into a wrapper allocation.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class Http3TransportFailure
    {
        internal static bool IsTransport(Exception? error) => error is QuicStreamFailure or QuicConnectionFailure;

        internal static bool IsStream(Exception error) => error is QuicStreamFailure;

        internal static bool IsLocalReadAbort(Exception error) => error is QuicStreamFailure { Remote: false };
    }
}
#endif
