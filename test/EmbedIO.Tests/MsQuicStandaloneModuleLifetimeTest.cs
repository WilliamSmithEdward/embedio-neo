using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicApiLifetimeTest
    {
        [Test]
        public void StandaloneLinuxNativeModuleSurvivesRepeatedApiAndChildRetirement()
        {
            if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux module lifetime check.");
            // Never query runtime QUIC support: its own module reference masks
            // the standalone provider unload failure.
            RequireNativeCycle();
            var mappings = NativeMappings();
            Assert.That(mappings, Is.Not.Empty);
            for (var index = 0; index < 512; index++) NativeCycle((index & 1) == 0, index % 16 == 0);
            Assert.That(NativeMappings(), Is.EquivalentTo(mappings));
            TestContext.Out.WriteLine("513 API/configuration/registration lifecycles closed; module mappings unchanged.");
        }

        private static void NativeCycle(bool parentsFirst, bool rejectConfiguration)
        {
            using var api = OpenApi();
            using var registration = Register(api);
            if (rejectConfiguration)
                Assert.Throws<ArgumentOutOfRangeException>(() => Configure(registration, Array.Empty<byte>()));
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            if (parentsFirst) { api.Dispose(); registration.Dispose(); }
            configuration.Dispose();
            registration.Dispose();
            api.Dispose();
            Assert.That(configuration.IsClosed, Is.True);
            Assert.That(registration.IsClosed, Is.True);
            Assert.That(api.IsClosed, Is.True);
        }

        private static void RequireNativeCycle()
        {
            try { NativeCycle(false, false); }
            catch (TargetInvocationException error) when (error.InnerException is DllNotFoundException)
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1") throw;
                Assert.Ignore("The host does not provide the native MsQuic library.");
            }
        }

        private static HashSet<string> NativeMappings()
        {
            var mappings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/self/maps"))
                if (line.Contains("libmsquic.so", StringComparison.Ordinal)) mappings.Add(line);
            return mappings;
        }
    }
}
