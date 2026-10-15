using System.IO;

namespace EmbedIO.Net.Internal.WebTransport
{
    // A session-scoped WebTransport rule violation: the session is terminated
    // and its CONNECT stream must be reset with ErrorCode. Connection-scoped
    // violations use the connection's own Http3ProtocolException so the
    // integrating transport closes the connection through its existing path.
    internal sealed class WebTransportException : IOException
    {
        internal WebTransportException(long errorCode, string message) : base(message) { ErrorCode = errorCode; }
        public long ErrorCode { get; }
    }
}
