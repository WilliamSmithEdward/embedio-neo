#if NET10_0_OR_GREATER
using System;
using System.Net.Quic;
using System.Runtime.Versioning;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    // Legacy providers and the managed engine preserve the same HTTP/3 failure
    // decisions without converting every task or read into a wrapper allocation.
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal static class Http3TransportFailure
    {
        internal static bool IsTransport(Exception? error) =>
            error is QuicException or QuicStreamFailure or QuicConnectionFailure;

        internal static bool IsStream(Exception error) =>
            error is QuicStreamFailure
                || error is QuicException { QuicError: QuicError.StreamAborted or QuicError.OperationAborted };

        internal static bool IsLocalReadAbort(Exception error) =>
            error is QuicStreamFailure { Remote: false }
                || error is QuicException { QuicError: QuicError.OperationAborted };
    }
}
#endif
