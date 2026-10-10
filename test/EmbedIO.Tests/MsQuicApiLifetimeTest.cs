using System;
using System.Net.Quic;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Threading;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using EmbedIO.PlatformTests;
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
        private static void LoadServerCertificate(SafeHandle configuration, X509Certificate2 certificate)
        {
            try
            {
                (configuration.GetType().GetMethod("LoadServerCertificate", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing credential loader.")).Invoke(configuration, new object[] { certificate });
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        [Test]
        public void NativeServerCredentialsLoadAndRejectDuplicateLoading()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            LoadServerCertificate(configuration, certificate);
            Assert.Throws<InvalidOperationException>(() => LoadServerCertificate(configuration, certificate));
            certificate.Dispose();
            Assert.That(configuration.IsClosed, Is.False);
        }

        [Test]
        public void RejectedCertificateWithoutPrivateKeyAllowsSubsequentValidLoading()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
            Assert.Throws<ArgumentException>(() => LoadServerCertificate(configuration, publicOnly));
            LoadServerCertificate(configuration, certificate);
        }

        [Test]
        public void ClosedConfigurationRejectsCredentialLoading()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            configuration.Dispose();
            Assert.Throws<ObjectDisposedException>(() => LoadServerCertificate(configuration, certificate));
        }
        private static object ListenerCall(SafeHandle owner, string method, params object[] arguments)
        {
            try
            {
                var member = owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing native listener method.");
                var result = member.Invoke(owner, arguments);
                return member.ReturnType == typeof(void) ? typeof(void) : result ?? throw new AssertionException("Missing native listener result.");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private static SafeHandle Listen(SafeHandle registration)
            => (SafeHandle)ListenerCall(registration, "CreateListener");

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task NativeListenerBindsAndStopsWithRetainedParents(bool ipv6, bool parentsFirst)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            if (ipv6 && !Socket.OSSupportsIPv6) { Assert.Ignore("IPv6 is unavailable."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            if (parentsFirst) { api.Dispose(); registration.Dispose(); }
            var address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            ListenerCall(listener, "Start", new IPEndPoint(address, 0), new byte[] { (byte)'h', (byte)'3' });
            var actual = (IPEndPoint)ListenerCall(listener, "LocalEndPoint");
            Assert.That(actual.Address, Is.EqualTo(address));
            Assert.That(actual.Port, Is.GreaterThan(0));
            await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5));
            await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5));
            listener.Dispose();
            Assert.That(listener.IsClosed, Is.True);
        }

        [Test]
        public async Task InvalidListenerAlpnLeavesTheNativeListenerStartable()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            Assert.Throws<ArgumentOutOfRangeException>(() => ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), Array.Empty<byte>()));
            ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), new byte[] { 1 });
            await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public void ClosedNativeListenerRejectsStartup()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            listener.Dispose();
            Assert.Throws<ObjectDisposedException>(() => ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), new byte[] { 1 }));
        }

        [Test]
        public async Task UnstartedNativeListenerStopsWithoutWaitingForAnEvent()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task ConcurrentNativeListenerStopsJoinTheSameCompletion()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), new byte[] { 1 });
            var stops = new Task[32];
            Parallel.For(0, stops.Length, i => stops[i] = (Task)ListenerCall(listener, "StopAsync"));
            await Task.WhenAll(stops).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var stopped in stops) Assert.That(stopped, Is.SameAs(stops[0]));
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeAcceptedConnectionCompletesTlsAndShutdown(bool stopListenerFirst)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseNativeHandshake(stopListenerFirst);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseNativeHandshake(bool stopListenerFirst)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = Configure(registration, new byte[] { (byte)'h', (byte)'3' });
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            LoadServerCertificate(configuration, certificate);
            using var listener = Listen(registration);
            ListenerCall(listener, "EnableAcceptance");
            ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), new byte[] { (byte)'h', (byte)'3' });
            var endpoint = (IPEndPoint)ListenerCall(listener, "LocalEndPoint");
            var connecting = QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = endpoint,
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                        && peer.GetCertHashString() == certificate.GetCertHashString(),
                },
            }, deadline.Token).AsTask();
            var accept = (Task)ListenerCall(listener, "AcceptAsync", deadline.Token);
            await accept.WaitAsync(deadline.Token);
            using var connection = (SafeHandle)(accept.GetType().GetProperty("Result")?.GetValue(accept)
                ?? throw new AssertionException("Missing accepted native connection."));
            ListenerCall(connection, "Configure", configuration);
            await using var peer = await connecting.WaitAsync(deadline.Token);
            var ready = (Task)(connection.GetType().GetProperty("Connected", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(connection)
                ?? throw new AssertionException("Missing handshake completion."));
            await ready.WaitAsync(deadline.Token);
            Assert.That(peer.NegotiatedApplicationProtocol, Is.EqualTo(new SslApplicationProtocol("h3")));
            if (stopListenerFirst)
            {
                await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(deadline.Token);
                listener.Dispose();
            }
            await ((Task)ListenerCall(connection, "ShutdownAsync", 0x100L)).WaitAsync(deadline.Token);
            connection.Dispose();
            Assert.That(connection.IsClosed, Is.True);
        }
        [Test]
        public async Task NativeStopReleasesAPendingConnectionAccept()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = Register(api);
            using var listener = Listen(registration);
            ListenerCall(listener, "EnableAcceptance");
            ListenerCall(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), new byte[] { 1 });
            var pending = (Task)ListenerCall(listener, "AcceptAsync", CancellationToken.None);
            Assert.That(pending.IsCompleted, Is.False);
            await ((Task)ListenerCall(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)),
                Throws.InstanceOf<System.Threading.Channels.ChannelClosedException>());
        }

    }
}
