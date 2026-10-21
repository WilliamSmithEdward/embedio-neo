using EmbedIO.Net.Internal.Http3;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Quic
{
    // Development protocols and their observations wrap the same packet owner
    // used by the transport core; the wrapper contains no packet processing.
    internal sealed class ProbeConnection : QuicConnection
    {
        private readonly bool _webTransport;
        private readonly bool _multiplexed;

        internal ProbeConnection(
            ReadOnlySpan<byte> firstPacket,
            string library,
            string certificate,
            string key,
            bool webTransport = false,
            string authorization = "",
            double now = 0,
            int keyUpdateInterval = KeyUpdateState.DefaultInterval,
            int encryptionLimit = PacketKeys.MaximumEncryptions,
            ulong authenticationLimit = 1UL << 52,
            bool multiplexed = false,
            bool asyncStreams = false,
            Func<byte[], string, TlsSession>? tlsFactory = null,
            Func<StreamOperations, StreamApplication>? applicationFactory = null
        )
            : base(
                firstPacket,
                parameters => tlsFactory != null
                    ? tlsFactory(parameters, webTransport ? "h3" : OpenSslTls.ApplicationProtocol)
                    : new OpenSslTls(
                        library,
                        certificate,
                        key,
                        parameters,
                        webTransport ? "h3" : OpenSslTls.ApplicationProtocol
                    ),
                (operations, features) => applicationFactory != null
                    ? applicationFactory(operations)
                    : webTransport
                        ? new WebTransportApplication(
                            features.MaximumDatagramFrameSize > 0 && features.ReliableReset,
                            operations,
                            headers => headers.TryGetValue("authorization", out var value)
                                && value == "Bearer " + authorization
                        )
                        : asyncStreams ? new AsyncStreamApplication(operations) : new StreamEchoApplication(operations),
                now,
                keyUpdateInterval,
                encryptionLimit,
                authenticationLimit
            )
        {
            _webTransport = webTransport;
            _multiplexed = multiplexed || asyncStreams;
        }

        internal int RetainedApplicationInputs => Application switch
        {
            WebTransportApplication application => application.RetainedInputs,
            StreamEchoApplication application => application.Retained,
            AsyncStreamApplication application => application.Retained,
            _ => 0
        };
        internal AsyncStreamApplication? AsyncApplication => Application as AsyncStreamApplication;
        internal WebTransportApplication? WebTransport => Application as WebTransportApplication;
        internal int RawStreamsCompleted => (Application as StreamEchoApplication)?.Completed ?? 0;
        internal int EchoBytes => (Application as StreamEchoApplication)?.EchoBytes ?? 0;
        internal bool Complete =>
            _webTransport
                ? WebTransport?.AcceptedSessions > 0
                : _multiplexed
                    ? RawStreamsCompleted > 0 || AsyncApplication?.RetiredStreams > 0
                    : Application is StreamEchoApplication { Echoes: > 0 };
        internal bool NativeTlsStarted =>
            OperatingSystem.IsWindows() && Tls is SchannelTls schannel
                ? schannel.NativeContextCreated
                : Tls is OpenSslTls;
        internal IReadOnlyList<string> TlsDiagnostics =>
            OperatingSystem.IsWindows() && Tls is SchannelTls schannel ? schannel.Diagnostics : [];

        internal override List<byte[]> Receive(ReadOnlySpan<byte> datagram, double now)
        {
            try
            {
                return base.Receive(datagram, now);
            }

            catch (Http3ProtocolException error)
            {
                return CloseApplication((ulong)error.ErrorCode, now);
            }

            catch (WebTransportException error)
            {
                return CloseApplication((ulong)error.ErrorCode, now);
            }
        }
    }
}
