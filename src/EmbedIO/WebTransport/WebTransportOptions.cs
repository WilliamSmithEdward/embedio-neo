using System;
using EmbedIO.Configuration;

namespace EmbedIO
{
    /// <summary>
    /// <para>Settings of opt-in WebTransport over HTTP/3. A handler accepts a session through
    /// <see cref="IWebTransportContext"/>.</para>
    /// <para>The options are locked when the server that uses them is created.</para>
    /// </summary>
    /// <remarks>
    /// <para>WebTransport is served only by the HTTP/3 listener of the .NET 10 asset, in
    /// <see cref="HttpListenerMode.EmbedIOHttp3"/> and <see cref="HttpListenerMode.EmbedIOCombined"/> mode.</para>
    /// <para>The server speaks draft-ietf-webtrans-http3-16 (<c>:protocol webtransport-h3</c>) and, when
    /// <see cref="AcceptBrowserDraft02"/> is set, the draft-02 dialect current browsers use
    /// (<c>:protocol webtransport</c>). Neither draft is a published RFC.</para>
    /// </remarks>
    public sealed class WebTransportOptions : ConfiguredObject
    {
        private int _maximumSessionsPerConnection = 1;
        private int _maximumIncomingStreamsPerSession = 100;
        private bool _acceptBrowserDraft02 = true;

        /// <summary>
        /// Gets or sets how many sessions one HTTP/3 connection may hold at once. The default is 1.
        /// </summary>
        /// <remarks>
        /// More than one session needs session flow control, which draft-16 peers negotiate by both
        /// declaring initial limits; without it a connection holds one session. A request beyond the limit
        /// is answered with 429 before it reaches a handler.
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the options are locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or greater than 64.</exception>
        public int MaximumSessionsPerConnection
        {
            get => _maximumSessionsPerConnection;
            set
            {
                EnsureConfigurationNotLocked();

                if (value < 1 || value > 64) throw new ArgumentOutOfRangeException(nameof(value));

                _maximumSessionsPerConnection = value;
            }
        }

        /// <summary>
        /// Gets or sets how many streams the peer may open in one session, in each direction, before it
        /// must wait for earlier streams to finish. The default is 100.
        /// </summary>
        /// <remarks>
        /// With session flow control the server advertises this limit; otherwise it resets streams beyond it.
        /// QUIC stream limits of the connection still apply.
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the options are locked.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than 1 or greater than 1024.</exception>
        public int MaximumIncomingStreamsPerSession
        {
            get => _maximumIncomingStreamsPerSession;
            set
            {
                EnsureConfigurationNotLocked();

                if (value < 1 || value > 1024) throw new ArgumentOutOfRangeException(nameof(value));

                _maximumIncomingStreamsPerSession = value;
            }
        }

        /// <summary>
        /// Gets or sets whether the server also accepts the draft-02 dialect that current browsers send.
        /// The default is <see langword="true"/>.
        /// </summary>
        /// <remarks>
        /// Draft-02 has no session flow control and no reliable stream reset: a reset stream may lose
        /// the bytes that name its session, so the peer can discard it unannounced.
        /// </remarks>
        /// <exception cref="InvalidOperationException">This property is being set, and the options are locked.</exception>
        public bool AcceptBrowserDraft02
        {
            get => _acceptBrowserDraft02;
            set
            {
                EnsureConfigurationNotLocked();
                _acceptBrowserDraft02 = value;
            }
        }

        internal void Lock() => LockConfiguration();
    }
}
