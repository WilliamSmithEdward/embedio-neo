namespace EmbedIO.Net
{
    /// <summary>
    /// Specifies whether HTTPS listeners ask clients for a certificate.
    /// </summary>
    /// <seealso cref="ServerTlsPolicy.ClientCertificateMode"/>
    public enum ClientCertificateMode
    {
        /// <summary>Clients are not asked for a certificate. This is the default.</summary>
        None,

        /// <summary>Clients are asked for a certificate but may connect without one.
        /// A certificate that a client does send must pass validation.</summary>
        Allow,

        /// <summary>Clients must send a certificate that passes validation; the handshake fails otherwise.</summary>
        Require,
    }
}
