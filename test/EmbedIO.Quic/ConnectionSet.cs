namespace EmbedIO.Quic
{
    // Development success counters and protocol factories remain outside the core.
    internal sealed class ConnectionSet : QuicConnectionSet
    {
        internal const int Capacity = 16;
        internal const double HandshakeTimeout = 3000;

        internal int Completed { get; private set; }

        internal ConnectionSet(
            string library,
            string certificate,
            string key,
            bool webTransport = false,
            string authorization = "",
            Func<byte[], string, TlsSession>? tlsFactory = null
        )
            : base(
                (packet, now) => new ProbeConnection(
                    packet,
                    library,
                    certificate,
                    key,
                    webTransport,
                    authorization,
                    now,
                    tlsFactory: tlsFactory
                ),
                Capacity,
                HandshakeTimeout
            )
        { }

        protected override void OnRetiring(QuicConnection connection)
        {
            if (connection is ProbeConnection { Complete: true }) Completed++;
        }
    }
}
