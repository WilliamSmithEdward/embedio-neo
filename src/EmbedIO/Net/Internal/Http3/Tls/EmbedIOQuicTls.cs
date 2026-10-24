#if NET10_0_OR_GREATER
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Quic;

namespace EmbedIO.Net.Internal.Http3
{
    internal static class EmbedIOQuicTls
    {
        internal static void EnsureSupported()
        {
            if (!Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("HTTP/3 requires a 64-bit process.");

            if (OperatingSystem.IsWindows())
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                    throw new PlatformNotSupportedException(
                        "HTTP/3 requires Windows 11 or Windows Server 2022 and later."
                    );

                return;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    OpenSslTls.EnsureAvailable();
                }

                catch (Exception exception) when (
                    exception is DllNotFoundException
                        or EntryPointNotFoundException
                        or BadImageFormatException
                )
                {
                    throw new PlatformNotSupportedException(
                        "HTTP/3 requires the EmbedIO.Quic native TLS runtime for this platform.",
                        exception
                    );
                }

                return;
            }

            throw new PlatformNotSupportedException("Recordless TLS requires a desktop platform.");
        }

        // The caller owns the factory and disposes it when its listener stops.
        internal static EmbedIOQuicTlsFactory CreateFactory(
            ServerCertificateSource certificates,
            ServerTlsPolicy? policy
        )
        {
            ArgumentNullException.ThrowIfNull(certificates);
            policy?.EnsureSupported(true);

            EnsureSupported();
            policy?.Lock();
            return new EmbedIOQuicTlsFactory(certificates, policy);
        }

        internal static bool ValidateClientCertificate(
            ServerTlsPolicy policy,
            X509Certificate2? certificate,
            X509Certificate2Collection additional
        )
        {
            if (certificate == null)
                return policy.ValidateClientCertificate(
                    policy,
                    null,
                    null,
                    SslPolicyErrors.RemoteCertificateNotAvailable
                );

            using var chain = new X509Chain();
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            chain.ChainPolicy.RevocationMode = policy.CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck;
            chain.ChainPolicy.ExtraStore.AddRange(additional);
            var errors = chain.Build(certificate) ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors;
            // The callback's certificate must not borrow the native query's short-lived lease.
            var callbackCertificate = policy.ClientCertificateValidator == null
                ? certificate
                : X509CertificateLoader.LoadCertificate(certificate.RawData);
            return policy.ValidateClientCertificate(policy, callbackCertificate, chain, errors);
        }
    }
}
#endif
