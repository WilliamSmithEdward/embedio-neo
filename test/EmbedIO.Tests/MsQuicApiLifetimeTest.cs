using System;
using System.Net.Quic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class MsQuicApiLifetimeTest
    {
        private static SafeHandle OpenApi()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi");
            if (type == null) { Assert.Ignore("The retained asset does not include the native provider."); return null; }
            return (SafeHandle)(type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
                ?? throw new AssertionException("Missing native API owner."));
        }
        private static SafeHandle Register(SafeHandle api)
        {
            try
            {
                return (SafeHandle)(api.GetType().GetMethod("CreateRegistration", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(api, null)
                    ?? throw new AssertionException("Missing registration owner."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeRegistrationKeepsItsApiAliveUntilChildClosure(bool apiFirst)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            for (var i = 0; i < 16; i++)
            {
                using var api = OpenApi();
                Assert.That(api.IsInvalid, Is.False);
                using var registration = Register(api);
                Assert.That(registration.IsInvalid, Is.False);
                if (apiFirst) api.Dispose();
                registration.Dispose();
                registration.Dispose();
                api.Dispose();
                Assert.That(registration.IsClosed, Is.True);
                Assert.That(api.IsClosed, Is.True);
                Assert.Throws<ObjectDisposedException>(() => Register(api));
            }
        }

        [Test]
        public void ConcurrentNativeRegistrationDisposalClosesExactlyOneSafeHandle()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            api.Dispose();
            Parallel.For(0, 32, _ => registration.Dispose());
            Assert.That(registration.IsClosed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => Register(api));
        }
        private static SafeHandle Configure(SafeHandle registration, byte[] alpn)
        {
            try
            {
                return (SafeHandle)(registration.GetType().GetMethod("CreateConfiguration", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(registration, new object[] { alpn })
                    ?? throw new AssertionException("Missing configuration owner."));
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NativeConfigurationRetainsItsParentsUntilClosure(bool parentsFirst)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            Assert.That(configuration.IsInvalid, Is.False);
            if (parentsFirst) { api.Dispose(); registration.Dispose(); }
            configuration.Dispose();
            configuration.Dispose();
            registration.Dispose();
            api.Dispose();
            Assert.That(configuration.IsClosed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => Configure(registration, new byte[] { 1 }));
        }

        [TestCase(0)]
        [TestCase(256)]
        public void InvalidAlpnDoesNotPreventSubsequentConfiguration(int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            Assert.Throws<ArgumentOutOfRangeException>(() => Configure(registration, new byte[length]));
            using var valid = Configure(registration, new byte[] { 1 });
            Assert.That(valid.IsInvalid, Is.False);
        }

        [Test]
        public void ConcurrentConfigurationDisposalPreservesParentLeases()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { 1 });
            api.Dispose();
            registration.Dispose();
            Parallel.For(0, 32, _ => configuration.Dispose());
            Assert.That(configuration.IsClosed, Is.True);
        }
    }
}
