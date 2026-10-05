using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class PlatformDetectionTest
    {
        [Test]
        public void CertificateOptionsRetainPlatformRestrictions()
        {
            var options = new WebServerOptions();
            options.AutoLoadCertificate = false;
            options.AutoRegisterCertificate = false;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.DoesNotThrow(() => options.AutoLoadCertificate = true);
                Assert.DoesNotThrow(() => options.AutoRegisterCertificate = true);
            }
            else
            {
                Assert.Throws<PlatformNotSupportedException>(() => options.AutoLoadCertificate = true);
                Assert.Throws<PlatformNotSupportedException>(() => options.AutoRegisterCertificate = true);
            }
        }
    }
}
