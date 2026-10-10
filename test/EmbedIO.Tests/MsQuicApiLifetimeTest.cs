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
        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public async Task NativeConfigurationAdvertisesOnlyItsExplicitPeerStreamCredit(bool bidirectional, bool credit)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseNativeHandshake(false, bidirectional ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional, credit);
        }
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 65536)]
        [TestCase(true, 65536)]
        [TestCase(false, 1048576)]
        [TestCase(true, 1048576)]
        public async Task NativeAcceptedStreamReadsExactPeerBytesThroughFin(bool bidirectional, int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeReadCore(bidirectional, length);
        }
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 1)]
        [TestCase(false, 2)]
        [TestCase(true, 2)]
        public async Task NativePendingReadReleasesOnCancellationStreamDisposalOrConnectionShutdown(bool bidirectional, int ending)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativePendingReadCore(bidirectional, ending);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativePendingReadCore(bool bidirectional, int ending)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var sending = await peer.OpenOutboundStreamAsync(bidirectional ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional, token);
                await sending.WriteAsync(new byte[] { 42 }, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var receiving = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var buffer = new byte[16];
                Assert.That(await (ValueTask<int>)ListenerCall(receiving, "ReadAsync", buffer.AsMemory(), token), Is.EqualTo(1));
                Assert.That(buffer[0], Is.EqualTo(42));
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var reading = ((ValueTask<int>)ListenerCall(receiving, "ReadAsync", buffer.AsMemory(), cancellation.Token)).AsTask();
                Assert.That(reading.IsCompleted, Is.False, "The read must really be pending.");
                if (ending == 0)
                {
                    cancellation.Cancel();
                    await Assert.ThatAsync(async () => await reading.WaitAsync(token), Throws.InstanceOf<OperationCanceledException>());
                    await sending.WriteAsync(new byte[] { 43 }, true, token);
                    Assert.That(await (ValueTask<int>)ListenerCall(receiving, "ReadAsync", buffer.AsMemory(), token), Is.EqualTo(1));
                    Assert.That(buffer[0], Is.EqualTo(43));
                    Assert.That(await (ValueTask<int>)ListenerCall(receiving, "ReadAsync", buffer.AsMemory(), token), Is.Zero);
                }
                else if (ending == 1)
                {
                    receiving.Dispose();
                    await Assert.ThatAsync(async () => await reading.WaitAsync(token), Throws.InstanceOf<ObjectDisposedException>());
                    Assert.That(receiving.IsClosed, Is.True);
                    await Assert.ThatAsync(async () => await (ValueTask<int>)ListenerCall(receiving, "ReadAsync", Memory<byte>.Empty, token), Throws.InstanceOf<ObjectDisposedException>());
                }
                else
                {
                    await ((Task)ListenerCall(connection, "ShutdownAsync", 0x100L)).WaitAsync(token);
                    await Assert.ThatAsync(async () => await reading.WaitAsync(token), Throws.InstanceOf<System.IO.IOException>());
                }
            });
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeReadCore(bool bidirectional, int length)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var sending = await peer.OpenOutboundStreamAsync(bidirectional ? QuicStreamType.Bidirectional : QuicStreamType.Unidirectional, token);
                var payload = new byte[length];
                for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 37);
                var write = sending.WriteAsync(payload, true, token).AsTask();
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var receiving = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var readback = new byte[length];
                var chunk = new byte[1023];
                var total = 0;
                while (true)
                {
                    var count = await (ValueTask<int>)ListenerCall(receiving, "ReadAsync", chunk.AsMemory(), token);
                    if (count == 0) break;
                    Assert.That(total + count, Is.LessThanOrEqualTo(length));
                    chunk.AsSpan(0, count).CopyTo(readback.AsSpan(total)); total += count;
                }
                await write.WaitAsync(token);
                Assert.That(total, Is.EqualTo(length));
                Assert.That(readback, Is.EqualTo(payload));
                Assert.That(receiving.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(receiving), Is.EqualTo(sending.Id));
                Assert.That(receiving.GetType().GetProperty("Unidirectional", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(receiving), Is.EqualTo(!bidirectional));
            });
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseNativeHandshake(bool stopListenerFirst, QuicStreamType? streamType = null, bool credit = false, Func<SafeHandle, QuicConnection, CancellationToken, Task>? streams = null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = streamType.HasValue || streams != null
                ? (SafeHandle)ListenerCall(registration, "CreateStreamConfiguration", new byte[] { (byte)'h', (byte)'3' },
                    streams != null || (credit && streamType == QuicStreamType.Bidirectional) ? (ushort)8 : (ushort)0,
                    streams != null || (credit && streamType == QuicStreamType.Unidirectional) ? (ushort)8 : (ushort)0)
                : Configure(registration, new byte[] { (byte)'h', (byte)'3' });
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
            if (streams != null) ListenerCall(connection, "EnableStreamAcceptance");
            ListenerCall(connection, "Configure", configuration);
            await using var peer = await connecting.WaitAsync(deadline.Token);
            var ready = (Task)(connection.GetType().GetProperty("Connected", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(connection)
                ?? throw new AssertionException("Missing handshake completion."));
            await ready.WaitAsync(deadline.Token);
            Assert.That(peer.NegotiatedApplicationProtocol, Is.EqualTo(new SslApplicationProtocol("h3")));
            if (streams != null) await streams(connection, peer, deadline.Token);
            if (streamType.HasValue)
            {
                if (credit)
                {
                    await using var stream = await peer.OpenOutboundStreamAsync(streamType.Value, deadline.Token);
                    Assert.That(stream.Type, Is.EqualTo(streamType.Value));
                    Assert.That(stream.Id & 3, Is.EqualTo(streamType == QuicStreamType.Bidirectional ? 0 : 2));
                    // Sending forces the peer stream to reach the native callback.
                    // The temporary provider rejects it with H3_REQUEST_CANCELLED.
                    try { await stream.WriteAsync(new byte[] { 42 }, deadline.Token); }
                    catch (QuicException error) when (error.QuicError == QuicError.StreamAborted)
                    { Assert.That(error.ApplicationErrorCode, Is.EqualTo(0x10c)); }
                    await Assert.ThatAsync(async () => await stream.WritesClosed.WaitAsync(deadline.Token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(0x10c));
                }
                else
                {
                    using var cancelled = new CancellationTokenSource();
                    var pending = peer.OpenOutboundStreamAsync(streamType.Value, cancelled.Token).AsTask();
                    Assert.That(pending.IsCompleted, Is.False, "Zero credit must leave stream creation pending.");
                    cancelled.Cancel();
                    await Assert.ThatAsync(async () => await pending.WaitAsync(deadline.Token), Throws.InstanceOf<OperationCanceledException>());
                }
            }
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
