using System.Security.Cryptography.X509Certificates;

namespace EmbedIO.Net.Internal
{
    // Implemented by requests of EmbedIO's listeners; read by HttpRequestExtensions.GetClientCertificate.
    internal interface IClientCertificateCarrier
    {
        X509Certificate2? ClientCertificate { get; }
    }
}
