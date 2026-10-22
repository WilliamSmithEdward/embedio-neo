using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO.Net;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.Quic
{
    internal static class ProductionClientCertificatePolicy
    {
        internal static ServerTlsPolicy Create(
            string mode,
            X509Certificate2 expected,
            X509Certificate2 root,
            ProductionListenerState state
        )
        {
            if (
                mode is not ("none"
                    or "allow"
                    or "require"
                    or "allow-pin"
                    or "require-pin"
                    or "require-reject"
                    or "require-throw"
                    or "require-online"
                    or "require-custom-root"
                    or "require-custom-online")
            )
                throw new ArgumentException("Unknown client-certificate fixture policy.", nameof(mode));

            var policy = new ServerTlsPolicy
            {
                ClientCertificateMode = mode == "none"
                    ? ClientCertificateMode.None
                    : mode.StartsWith("allow", StringComparison.Ordinal)
                        ? ClientCertificateMode.Allow
                        : ClientCertificateMode.Require,
                CheckCertificateRevocation = mode is "require-online" or "require-custom-online"
            };

            if (mode is "none" or "allow" or "require") return policy;

            policy.ClientCertificateValidator = (certificate, chain, errors) =>
            {
                if (Interlocked.Increment(ref state.CallbackCertificateReservations) <= 32)
                    state.CallbackCertificates.Enqueue(certificate);

                var originalStatus = chain?.ChainStatus
                    .Select(value => value.Status.ToString())
                    .ToArray();
                bool? accepted = null;

                try
                {
                    if (mode == "require-throw")
                        throw new InvalidOperationException("client certificate fixture callback");

                    bool result;

                    if (mode == "require-reject") result = false;

                    else if (mode is "require-custom-root" or "require-custom-online")
                    {
                        if (chain == null) result = false;

                        else
                        {
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(root);
                            result = chain.Build(certificate);
                        }
                    }

                    else
                        result = StringComparer.Ordinal.Equals(
                            certificate.GetCertHashString(HashAlgorithmName.SHA256),
                            expected.GetCertHashString(HashAlgorithmName.SHA256)
                        );

                    accepted = result;
                    return result;
                }

                finally
                {
                    var observation = new
                    {
                        sha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256),
                        errors = errors.ToString(),
                        chain = chain != null,
                        elements = chain?.ChainElements.Count,
                        additional = chain?.ChainPolicy.ExtraStore.Count,
                        revocation = chain?.ChainPolicy.RevocationMode.ToString(),
                        status = originalStatus,
                        finalStatus = chain?.ChainStatus
                            .Select(value => value.Status.ToString())
                            .ToArray(),
                        accepted,
                        applicationPolicy = chain?.ChainPolicy.ApplicationPolicy
                            .Cast<Oid>()
                            .Select(oid => oid.Value)
                            .ToArray()
                    };
                    state.ClientCertificateValidations.Enqueue(JsonSerializer.Serialize(observation));
                }
            };
            return policy;
        }
    }

    internal sealed class ProductionClientCertificateController(ProductionListenerState state) : WebApiController
    {
        [Route(HttpVerbs.Get, "/client")]
        public TextHttpResult Client()
        {
            Interlocked.Increment(ref state.ClientCertificateRequests);
            var certificate = Request.GetClientCertificate();

            if (certificate == null) return HttpResults.Text("none");

            QuicFuzzChecks.Require(!certificate.HasPrivateKey, "public-client-certificate-has-no-private-key");

            if (Interlocked.Increment(ref state.CertificateReservations) <= 32)
                state.Certificates.Enqueue(certificate);

            return HttpResults.Text(certificate.GetCertHashString(HashAlgorithmName.SHA256));
        }
    }
}
