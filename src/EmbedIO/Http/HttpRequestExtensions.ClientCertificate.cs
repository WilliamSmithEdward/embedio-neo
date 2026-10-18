using System;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Net.Internal;

namespace EmbedIO
{
    partial class HttpRequestExtensions
    {
        /// <summary>
        /// Gets the certificate the client presented during the TLS handshake of the connection that carried a request.
        /// </summary>
        /// <param name="this">The <see cref="IHttpRequest"/> on which this method is called.</param>
        /// <returns>The client's certificate, which passed the validation of the server's
        /// <see cref="Net.ServerTlsPolicy"/>; or <see langword="null"/> if the connection is not secure,
        /// the policy did not ask for a certificate, or the client sent none.</returns>
        /// <remarks>
        /// <para>The certificate holds no private key. EmbedIO does not dispose it; it is shared by every request on
        /// the same connection, so a handler must not dispose it either.</para>
        /// <para>Requests created by other <see cref="IHttpRequest"/> implementations, such as test doubles, return
        /// <see langword="null"/>.</para>
        /// </remarks>
        /// <exception cref="NullReferenceException"><paramref name="this"/> is <see langword="null"/>.</exception>
        public static X509Certificate2? GetClientCertificate(this IHttpRequest @this)
        {
            if (@this is null) throw new NullReferenceException();

            return (@this as IClientCertificateCarrier)?.ClientCertificate;
        }
    }
}
