#if NET10_0_OR_GREATER
using System;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Net.Internal.Http3
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal abstract class Http3ListenerBinding : IAsyncDisposable
    {
        internal abstract ValueTask<Http3AcceptedConnection> AcceptAsync(CancellationToken token);

        internal virtual ValueTask StopAcceptingAsync() => DisposeAsync();

        // Lets the HTTP/3 session refuse new connections before any TLS work while it has no
        // room for them. The binding passes how many connections it admitted that the session
        // has not accepted yet. A binding without its own admission ignores it.
        internal virtual void LimitAdmission(Func<int, bool> saturated) { }

        public abstract ValueTask DisposeAsync();

        internal static Http3ListenerBinding Bind(
            IPEndPoint endpoint,
            ServerCertificateSource certificates,
            ServerTlsPolicy? policy
        ) => new EmbedIOQuicListenerBinding(endpoint, EmbedIOQuicTls.CreateFactory(certificates, policy));
    }

    internal abstract class Http3AcceptedConnection : IAsyncDisposable
    {
        internal abstract bool HandshakePending { get; }

        // The client's certificate from the handshake, when the listener's policy asked for one.
        internal virtual X509Certificate2? ClientCertificate => null;

        internal abstract ValueTask WaitReadyAsync(CancellationToken token);

        internal abstract Task RunAsync(
            Func<Http3QuicExchange, IPEndPoint, IPEndPoint, Task> dispatch,
            CancellationToken abort,
            CancellationToken drain,
            WebTransportOptions? webTransport = null,
            TimeSpan? requestHeadersTimeout = null
        );

        internal abstract ValueTask RejectAsync(CancellationToken token);

        public abstract ValueTask DisposeAsync();
    }
}
#endif
