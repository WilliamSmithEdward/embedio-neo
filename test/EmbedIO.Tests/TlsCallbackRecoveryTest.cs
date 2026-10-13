using System;
using System.Net.Security;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.Net;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TlsCallbackRecoveryTest
    {
        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(false, 2)]
        [TestCase(false, 3)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        [TestCase(true, 3)]
        public void CallbacksPreserveTheDocumentedRecoveryBoundary(bool selector, int kind)
        {
            // Synthetic exceptions exercise catch filters without exhausting resources
            // or causing an actual stack overflow or native memory fault.
            Exception failure = kind switch
            {
                0 => new InvalidOperationException("application callback failed"),
                1 => new OutOfMemoryException("synthetic"),
                2 => new StackOverflowException("synthetic"),
                _ => new AccessViolationException("synthetic"),
            };
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=callback-test", key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1));
            var policy = new ServerTlsPolicy
            {
                ClientCertificateValidator = (_, _, _) => throw failure,
                ServerCertificateSelector = _ => throw failure,
            };
            var validate = (typeof(ServerTlsPolicy).GetMethod("ValidateClientCertificate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing validation callback."))
                .CreateDelegate<Func<object, X509Certificate?, X509Chain?, SslPolicyErrors, bool>>(policy);
            var select = (typeof(ServerTlsPolicy).GetMethod("SelectServerCertificate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing selection callback."))
                .CreateDelegate<Func<string?, X509Certificate, X509Certificate>>(policy);

            if (kind == 0 && !selector)
            {
                Assert.That(validate(this, certificate, null, SslPolicyErrors.None), Is.False);
                return;
            }

            var caught = Assert.Catch<Exception>(() =>
            {
                if (selector)
                    _ = select("localhost", certificate);
                else
                    _ = validate(this, certificate, null, SslPolicyErrors.None);
            });
            if (kind == 0)
            {
                Assert.That(caught, Is.TypeOf<AuthenticationException>());
                Assert.That(caught?.InnerException, Is.SameAs(failure));
            }
            else
                Assert.That(caught, Is.SameAs(failure));
        }
    }
}
