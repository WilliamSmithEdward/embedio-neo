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
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(65536)]
        [TestCase(1048576)]
        public async Task NativeAcceptedBidirectionalStreamSendsExactBytesAndFin(int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeSendCore(length);
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeCommittedBlockedSendRetainsOwnershipUntilCancellationOrDisposal(bool dispose)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeBlockedSendCore(dispose);
        }
        [Test]
        public async Task NativeLocalWriteAbortReleasesACommittedFlowBlockedSend()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeBlockedSendCore(false, true);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeBlockedSendCore(bool dispose, bool localAbort = false)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var stream = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await stream.WriteAsync(new byte[] { 42 }, true, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var input = new byte[8];
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.EqualTo(1));
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.Zero);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var payload = new byte[8 * 1024 * 1024];
                Array.Fill(payload, (byte)93);
                var writing = ((ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)payload, false, cancellation.Token)).AsTask();
                await Task.Delay(50, token);
                Assert.That(writing.IsCompleted, Is.False, "The committed unbuffered write must remain blocked by unread peer flow control.");
                if (localAbort)
                {
                    var closed = NativeDirection(native, "WritesClosed");
                    ListenerCall(native, "Abort", QuicAbortDirection.Write, 0x127L);
                    await Assert.ThatAsync(async () => await writing.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.QuicError)).EqualTo(QuicError.OperationAborted));
                    await Assert.ThatAsync(async () => await closed.WaitAsync(token), Throws.TypeOf<QuicException>());
                    await Assert.ThatAsync(async () => await stream.ReadsClosed.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(0x127));
                    Assert.That(NativeDirection(native, "ReadsClosed").IsCompletedSuccessfully, Is.True);
                }
                else if (dispose)
                {
                    native.Dispose();
                    await Assert.ThatAsync(async () => await writing.WaitAsync(token), Throws.InstanceOf<System.IO.IOException>());
                }
                else
                {
                    cancellation.Cancel();
                    await Assert.ThatAsync(async () => await writing.WaitAsync(token), Throws.InstanceOf<OperationCanceledException>());
                }
                // Cancellation is stream-local: a new stream still transfers both ways.
                await using var sibling = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await sibling.WriteAsync(new byte[] { 71 }, true, token);
                var siblingAccept = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await siblingAccept.WaitAsync(token);
                using var owned = (SafeHandle)(siblingAccept.GetType().GetProperty("Result")?.GetValue(siblingAccept)
                    ?? throw new AssertionException("Missing sibling native stream."));
                Assert.That(await (ValueTask<int>)ListenerCall(owned, "ReadAsync", input.AsMemory(), token), Is.EqualTo(1));
                Assert.That(input[0], Is.EqualTo(71));
                Assert.That(await (ValueTask<int>)ListenerCall(owned, "ReadAsync", input.AsMemory(), token), Is.Zero);
                await (ValueTask)ListenerCall(owned, "WriteAsync", new ReadOnlyMemory<byte>(new byte[] { 72 }), true, token);
                Assert.That(await sibling.ReadAsync(input, token), Is.EqualTo(1));
                Assert.That(input[0], Is.EqualTo(72));
                Assert.That(await sibling.ReadAsync(input, token), Is.Zero);
            }, unbuffered: true);
        }
        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(false, 65536)]
        [TestCase(true, 65536)]
        public async Task NativeOutgoingStreamsReachPeerWithExactBytesAndFin(bool unidirectional, int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeOutgoingCore(unidirectional, length);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeOutgoingCore(bool unidirectional, int length)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                var opening = (Task)ListenerCall(connection, "OpenStreamAsync", unidirectional, token);
                await opening.WaitAsync(token);
                using var native = (SafeHandle)(opening.GetType().GetProperty("Result")?.GetValue(opening)
                    ?? throw new AssertionException("Missing native outgoing stream."));
                await using var incoming = await peer.AcceptInboundStreamAsync(token);
                Assert.That(incoming.Type, Is.EqualTo(unidirectional ? QuicStreamType.Unidirectional : QuicStreamType.Bidirectional));
                Assert.That(incoming.Id & 3, Is.EqualTo(unidirectional ? 3 : 1));
                Assert.That(native.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(native), Is.EqualTo(incoming.Id));
                var payload = new byte[length];
                for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 11);
                var writing = ((ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)payload, true, token)).AsTask();
                var actual = new byte[length]; var chunk = new byte[8191]; var total = 0;
                while (true)
                {
                    var count = await incoming.ReadAsync(chunk, token);
                    if (count == 0) break;
                    Assert.That(total + count, Is.LessThanOrEqualTo(length));
                    chunk.AsSpan(0, count).CopyTo(actual.AsSpan(total)); total += count;
                }
                await writing.WaitAsync(token);
                Assert.That(total, Is.EqualTo(length)); Assert.That(actual, Is.EqualTo(payload));
                if (unidirectional)
                    await Assert.ThatAsync(async () => await (ValueTask<int>)ListenerCall(native, "ReadAsync", chunk.AsMemory(), token), Throws.InstanceOf<NotSupportedException>());
                else
                {
                    await incoming.WriteAsync(new byte[] { 99 }, true, token);
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", chunk.AsMemory(), token), Is.EqualTo(1));
                    Assert.That(chunk[0], Is.EqualTo(99));
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", chunk.AsMemory(), token), Is.Zero);
                }
            });
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task NativeOutgoingStreamWaitsForPeerCreditAndReleasesOnCancellationOrShutdown(bool unidirectional, bool shutdown)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeOutgoingBlockedCore(unidirectional, shutdown);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeOutgoingBlockedCore(bool unidirectional, bool shutdown)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                var opening = (Task)ListenerCall(connection, "OpenStreamAsync", unidirectional, cancellation.Token);
                await Task.Delay(50, token);
                Assert.That(opening.IsCompleted, Is.False, "A local ID must not substitute for actual peer credit.");
                if (shutdown)
                {
                    await ((Task)ListenerCall(connection, "ShutdownAsync", 0x100L)).WaitAsync(token);
                    await Assert.ThatAsync(async () => await opening.WaitAsync(token), Throws.InstanceOf<System.IO.IOException>());
                }
                else
                {
                    cancellation.Cancel();
                    await Assert.ThatAsync(async () => await opening.WaitAsync(token), Throws.InstanceOf<OperationCanceledException>().With.Property(nameof(OperationCanceledException.CancellationToken)).EqualTo(cancellation.Token));
                    await using var sibling = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                    await sibling.WriteAsync(new byte[] { 53 }, true, token);
                    var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                    await accepting.WaitAsync(token);
                    using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                        ?? throw new AssertionException("Missing healthy native sibling."));
                    var bytes = new byte[8];
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                    Assert.That(bytes[0], Is.EqualTo(53));
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.Zero);
                }
            }, peerCredit: 0);
        }
        private static Task NativeDirection(SafeHandle stream, string name)
            => stream.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(stream) as Task
                ?? throw new AssertionException("Missing native direction completion " + name + ".");

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public async Task NativeBidirectionalCompletionSeparatesFinAndPeerAborts(int ending)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeDirectionCore(ending);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeDirectionCore(int ending)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await remote.WriteAsync(new byte[] { 42 }, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var input = new byte[8];
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.EqualTo(1));
                var reads = NativeDirection(native, "ReadsClosed");
                var writes = NativeDirection(native, "WritesClosed");
                Assert.That(reads.IsCompleted, Is.False);
                Assert.That(writes.IsCompleted, Is.False);
                if (ending == 0)
                {
                    remote.CompleteWrites();
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.Zero);
                    await reads.WaitAsync(token);
                    Assert.That(writes.IsCompleted, Is.False, "Peer FIN must not end the independent send direction.");
                    await (ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)new byte[] { 43 }, true, token);
                    Assert.That(await remote.ReadAsync(input, token), Is.EqualTo(1));
                    Assert.That(input[0], Is.EqualTo(43));
                    Assert.That(await remote.ReadAsync(input, token), Is.Zero);
                    await writes.WaitAsync(token);
                }
                else if (ending == 1)
                {
                    remote.Abort(QuicAbortDirection.Write, 0x123);
                    await Assert.ThatAsync(async () => await reads.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(0x123));
                    Assert.That(writes.IsCompleted, Is.False);
                    await (ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)new byte[] { 44 }, true, token);
                    Assert.That(await remote.ReadAsync(input, token), Is.EqualTo(1));
                    Assert.That(input[0], Is.EqualTo(44));
                    Assert.That(await remote.ReadAsync(input, token), Is.Zero);
                    await writes.WaitAsync(token);
                }
                else
                {
                    remote.Abort(QuicAbortDirection.Read, 0x124);
                    await Assert.ThatAsync(async () => await writes.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(0x124));
                    Assert.That(reads.IsCompleted, Is.False);
                    await remote.WriteAsync(new byte[] { 45 }, true, token);
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.EqualTo(1));
                    Assert.That(input[0], Is.EqualTo(45));
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.Zero);
                    await reads.WaitAsync(token);
                }
                Assert.That(NativeDirection(native, "ReadsClosed"), Is.SameAs(reads));
                Assert.That(NativeDirection(native, "WritesClosed"), Is.SameAs(writes));
            });
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task NativeLocalAbortPreservesTheOtherDirectionAndPeerErrorCode(int direction)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeLocalAbortCore(direction);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeLocalAbortCore(int choice)
        {
            var direction = choice == 0 ? QuicAbortDirection.Read : choice == 1 ? QuicAbortDirection.Write : QuicAbortDirection.Both;
            var firstCode = choice == 3 ? 0x3fffffffffffffffL : 0x125L;
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await remote.WriteAsync(new byte[] { 71 }, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var bytes = new byte[8];
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                var reads = NativeDirection(native, "ReadsClosed");
                var writes = NativeDirection(native, "WritesClosed");
                var readAborted = direction != QuicAbortDirection.Write;
                var writeAborted = direction != QuicAbortDirection.Read;
                var pendingRead = ((ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token)).AsTask();
                Assert.That(pendingRead.IsCompleted, Is.False);
                Assert.Throws<ArgumentOutOfRangeException>(() => ListenerCall(native, "Abort", direction, -1L));
                Assert.Throws<ArgumentOutOfRangeException>(() => ListenerCall(native, "Abort", direction, 0x4000000000000000L));
                Assert.Throws<ArgumentOutOfRangeException>(() => ListenerCall(native, "Abort", (QuicAbortDirection)99, 0L));
                Assert.That(reads.IsCompleted, Is.False, "Rejected arguments must not change stream state.");
                Assert.That(writes.IsCompleted, Is.False);
                ListenerCall(native, "Abort", direction, firstCode);
                ListenerCall(native, "Abort", direction, 0x126L);
                if (readAborted)
                {
                    await Assert.ThatAsync(async () => await reads.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.QuicError)).EqualTo(QuicError.OperationAborted));
                    await Assert.ThatAsync(async () => await pendingRead.WaitAsync(token), Throws.TypeOf<QuicException>());
                    await Assert.ThatAsync(async () => await remote.WritesClosed.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(firstCode));
                }
                else
                {
                    Assert.That(reads.IsCompleted, Is.False);
                    await remote.WriteAsync(new byte[] { 72 }, true, token);
                    Assert.That(await pendingRead.WaitAsync(token), Is.EqualTo(1));
                    Assert.That(bytes[0], Is.EqualTo(72));
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.Zero);
                    await reads.WaitAsync(token);
                }
                if (writeAborted)
                {
                    await Assert.ThatAsync(async () => await writes.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.QuicError)).EqualTo(QuicError.OperationAborted));
                    await Assert.ThatAsync(async () => await remote.ReadsClosed.WaitAsync(token),
                        Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.ApplicationErrorCode)).EqualTo(firstCode));
                    await Assert.ThatAsync(async () => await (ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)new byte[] { 73 }, false, token),
                        Throws.TypeOf<QuicException>());
                }
                else
                {
                    Assert.That(writes.IsCompleted, Is.False);
                    await (ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)new byte[] { 74 }, true, token);
                    Assert.That(await remote.ReadAsync(bytes, token), Is.EqualTo(1));
                    Assert.That(bytes[0], Is.EqualTo(74));
                    Assert.That(await remote.ReadAsync(bytes, token), Is.Zero);
                    await writes.WaitAsync(token);
                }
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NativeUnidirectionalCompletionTreatsOnlyTheMissingSideAsClosed(bool local)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeUnidirectionalDirectionCore(local);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeUnidirectionalDirectionCore(bool local)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                if (local)
                {
                    var opening = (Task)ListenerCall(connection, "OpenStreamAsync", true, token);
                    await opening.WaitAsync(token);
                    using var native = (SafeHandle)(opening.GetType().GetProperty("Result")?.GetValue(opening)
                        ?? throw new AssertionException("Missing native stream."));
                    var reads = NativeDirection(native, "ReadsClosed");
                    var writes = NativeDirection(native, "WritesClosed");
                    Assert.That(reads.IsCompletedSuccessfully, Is.True);
                    Assert.That(writes.IsCompleted, Is.False);
                    await (ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)new byte[] { 61 }, true, token);
                    await using var remote = await peer.AcceptInboundStreamAsync(token);
                    var bytes = new byte[8];
                    Assert.That(await remote.ReadAsync(bytes, token), Is.EqualTo(1));
                    Assert.That(bytes[0], Is.EqualTo(61));
                    Assert.That(await remote.ReadAsync(bytes, token), Is.Zero);
                    await writes.WaitAsync(token);
                }
                else
                {
                    await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
                    await remote.WriteAsync(new byte[] { 62 }, token);
                    var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                    await accepting.WaitAsync(token);
                    using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                        ?? throw new AssertionException("Missing native stream."));
                    Assert.That(NativeDirection(native, "WritesClosed").IsCompletedSuccessfully, Is.True);
                    var reads = NativeDirection(native, "ReadsClosed");
                    Assert.That(reads.IsCompleted, Is.False);
                    var bytes = new byte[8];
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                    remote.CompleteWrites();
                    Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.Zero);
                    await reads.WaitAsync(token);
                }
            });
        }
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task NativeCompleteWritesPreservesCommittedBytesAndTheReadDirection(int mode)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeCompleteWritesCore(mode);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeCompleteWritesCore(int mode)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                SafeHandle native;
                QuicStream remote;
                if (mode == 2)
                {
                    var opening = (Task)ListenerCall(connection, "OpenStreamAsync", true, token);
                    await opening.WaitAsync(token);
                    native = (SafeHandle)(opening.GetType().GetProperty("Result")?.GetValue(opening)
                        ?? throw new AssertionException("Missing native outgoing stream."));
                    ListenerCall(native, "CompleteWrites");
                    remote = await peer.AcceptInboundStreamAsync(token);
                }
                else
                {
                    remote = await peer.OpenOutboundStreamAsync(mode == 3 ? QuicStreamType.Unidirectional : QuicStreamType.Bidirectional, token);
                    await remote.WriteAsync(new byte[] { 81 }, token);
                    var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                    await accepting.WaitAsync(token);
                    native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                        ?? throw new AssertionException("Missing native accepted stream."));
                }
                using (native)
                await using (remote)
                {
                    var bytes = new byte[1023];
                    var reads = NativeDirection(native, "ReadsClosed");
                    var writes = NativeDirection(native, "WritesClosed");
                    if (mode != 2)
                    {
                        Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                        Assert.That(bytes[0], Is.EqualTo(81));
                        Assert.That(reads.IsCompleted, Is.False);
                    }
                    if (mode == 3)
                    {
                        Assert.Throws<InvalidOperationException>(() => ListenerCall(native, "CompleteWrites"));
                        Assert.That(writes.IsCompletedSuccessfully, Is.True);
                    }
                    else
                    {
                        var length = mode == 1 ? 8 * 1024 * 1024 : 0;
                        var payload = new byte[length];
                        Array.Fill(payload, (byte)83);
                        var sending = mode == 1
                            ? ((ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)payload, false, token)).AsTask()
                            : Task.CompletedTask;
                        if (mode == 1)
                        {
                            await Task.Delay(50, token);
                            Assert.That(sending.IsCompleted, Is.False, "The unread peer must block this unbuffered send.");
                        }
                        Parallel.For(0, 16, _ => ListenerCall(native, "CompleteWrites"));
                        if (mode == 1) Assert.That(sending.IsCompleted, Is.False, "Queuing FIN must neither wait for nor cancel committed data.");
                        var received = 0;
                        while (true)
                        {
                            var count = await remote.ReadAsync(bytes, token);
                            if (count == 0) break;
                            Assert.That(received + count, Is.LessThanOrEqualTo(length));
                            Assert.That(bytes.AsSpan(0, count).IndexOfAnyExcept((byte)83), Is.EqualTo(-1));
                            received += count;
                        }
                        Assert.That(received, Is.EqualTo(length));
                        await sending.WaitAsync(token);
                        await writes.WaitAsync(token);
                        ListenerCall(native, "CompleteWrites");
                        await Assert.ThatAsync(async () => await (ValueTask)ListenerCall(native, "WriteAsync", ReadOnlyMemory<byte>.Empty, false, token),
                            Throws.InstanceOf<System.IO.IOException>());
                    }
                    if (mode == 2) Assert.That(reads.IsCompletedSuccessfully, Is.True);
                    else
                    {
                        Assert.That(reads.IsCompleted, Is.False);
                        await remote.WriteAsync(new byte[] { 84 }, true, token);
                        Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                        Assert.That(bytes[0], Is.EqualTo(84));
                        Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.Zero);
                        await reads.WaitAsync(token);
                    }
                }
            }, unbuffered: mode == 1);
        }

        [Test]
        public async Task NativeAsyncDisposalPreservesFinBeforePeerDrainsACommittedSend()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeAsyncFinDisposalCore();
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeAsyncFinDisposalCore()
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await remote.WriteAsync(new byte[] { 91 }, true, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var bytes = new byte[8191];
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.EqualTo(1));
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", bytes.AsMemory(), token), Is.Zero);
                var payload = new byte[8 * 1024 * 1024];
                Array.Fill(payload, (byte)93);
                var writing = ((ValueTask)ListenerCall(native, "WriteAsync", (ReadOnlyMemory<byte>)payload, true, token)).AsTask();
                var disposing = ((ValueTask)ListenerCall(native, "DisposeAsync")).AsTask();
                try
                {
                    Assert.That(disposing.IsCompleted, Is.False, "Async disposal must await queued FIN instead of resetting it.");
                    var total = 0;
                    while (true)
                    {
                        var count = await remote.ReadAsync(bytes, token);
                        if (count == 0) break;
                        Assert.That(total + count, Is.LessThanOrEqualTo(payload.Length));
                        Assert.That(bytes.AsSpan(0, count).IndexOfAnyExcept((byte)93), Is.EqualTo(-1));
                        total += count;
                    }
                    Assert.That(total, Is.EqualTo(payload.Length));
                }
                finally { await Task.WhenAll(writing, disposing).WaitAsync(token); }
                Assert.That(native.IsClosed, Is.True);
                Assert.That(NativeDirection(native, "WritesClosed").IsCompletedSuccessfully, Is.True);
            });
        }

        [TestCase(0)]
        [TestCase(1023)]
        [TestCase(1048576)]
        public async Task NativeTransportRunsTheSharedHttp3WorkerAndReturnsExactData(int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeHttp3WorkerCore(length);
        }
        [TestCase(0)]
        [TestCase(1048576)]
        public async Task NativeTransportDrainPreservesAcceptedResponsesAndRefusesLaterRequests(int length)
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeHttp3WorkerCore(length, true);
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeHttp3WorkerCore(int length, bool graceful = false)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                var worker = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection")
                    ?? throw new AssertionException("Missing HTTP/3 worker.");
                var exchange = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange")
                    ?? throw new AssertionException("Missing HTTP/3 exchange.");
                var run = worker.GetMethod(graceful ? "RunNativeForListenerAsync" : "RunNativeAsync", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing native HTTP/3 worker integration.");
                var payload = new byte[length];
                for (var i = 0; i < length; i++) payload[i] = (byte)(i * 37);
                var dispatched = 0;
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Func<object, Task> reply = async value =>
                {
                    Interlocked.Increment(ref dispatched);
                    entered.TrySetResult();
                    if (graceful) await release.Task.WaitAsync(token);
                    await (Task)(exchange.GetMethod("RespondAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(value, new object[] { payload, token })
                        ?? throw new AssertionException("Missing HTTP/3 response writer."));
                };
                var argument = System.Linq.Expressions.Expression.Parameter(exchange);
                var handler = System.Linq.Expressions.Expression.Lambda(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)),
                    System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(reply),
                        System.Linq.Expressions.Expression.Convert(argument, typeof(object))), argument).Compile();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var drain = new CancellationTokenSource();
                var arguments = graceful
                    ? new object[] { connection, handler, stop.Token, drain.Token, TimeSpan.FromSeconds(5) }
                    : new object[] { connection, handler, stop.Token };
                var serving = (Task)(run.Invoke(null, arguments)
                    ?? throw new AssertionException("Missing native HTTP/3 runner."));
                QuicStream? control = null;
                var serverStreams = new System.Collections.Generic.List<QuicStream>();
                try
                {
                    control = await peer.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
                    await control.WriteAsync(new byte[] { 0, 4, 0 }, token);
                    await using var request = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                    // Independent static QPACK request: GET, https, /, localhost.
                    await request.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, token);
                    if (graceful)
                    {
                        await entered.Task.WaitAsync(token);
                        QuicStream? serverControl = null;
                        for (var index = 0; index < 3 && serverControl == null; index++)
                        {
                            var incoming = await peer.AcceptInboundStreamAsync(token);
                            serverStreams.Add(incoming);
                            if (await ReadNativePeerIntegerAsync(incoming, token) == 0) serverControl = incoming;
                        }
                        Assert.That(serverControl, Is.Not.Null);
                        var readable = serverControl ?? throw new AssertionException("Missing native server control stream.");
                        Assert.That(await ReadNativePeerIntegerAsync(readable, token), Is.EqualTo(4));
                        var settingsLength = await ReadNativePeerIntegerAsync(readable, token);
                        Assert.That(settingsLength, Is.LessThanOrEqualTo(65536));
                        await readable.ReadExactlyAsync(new byte[checked((int)settingsLength)], token);
                        drain.Cancel();
                        Assert.That(await ReadNativePeerIntegerAsync(readable, token), Is.EqualTo(7));
                        var goAwayLength = await ReadNativePeerIntegerAsync(readable, token);
                        Assert.That(goAwayLength, Is.InRange(1, 8));
                        var goAway = new byte[checked((int)goAwayLength)];
                        await readable.ReadExactlyAsync(goAway, token);
                        var goAwayOffset = 0;
                        Assert.That(NativePeerInteger(goAway, ref goAwayOffset), Is.EqualTo(4));
                        Assert.That(goAwayOffset, Is.EqualTo(goAway.Length));
                        Assert.That(serving.IsCompleted, Is.False, "Drain must preserve the admitted response.");
                        await using var rejected = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                        var error = await Assert.ThrowsAsync<QuicException>(async () =>
                        {
                            await rejected.WriteAsync(Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374"), true, token);
                            _ = await rejected.ReadAsync(new byte[1], token);
                        });
                        Assert.That(error?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                        Assert.That(error?.ApplicationErrorCode, Is.EqualTo(0x10b));
                        release.TrySetResult();
                    }
                    using var response = new System.IO.MemoryStream();
                    await request.CopyToAsync(response, token);
                    var wire = response.ToArray();
                    var offset = 0;
                    var headers = 0;
                    using var body = new System.IO.MemoryStream();
                    while (offset < wire.Length)
                    {
                        var type = NativePeerInteger(wire, ref offset);
                        var size = NativePeerInteger(wire, ref offset);
                        Assert.That(size, Is.LessThanOrEqualTo(wire.Length - offset));
                        if (type == 1) { headers++; Assert.That(size, Is.GreaterThanOrEqualTo(2)); }
                        else
                        {
                            Assert.That(type, Is.Zero, "Only final HEADERS and DATA are expected.");
                            Assert.That(headers, Is.EqualTo(1), "DATA must follow final headers.");
                            body.Write(wire, offset, checked((int)size));
                        }
                        offset += checked((int)size);
                    }
                    Assert.That(headers, Is.EqualTo(1));
                    Assert.That(body.ToArray(), Is.EqualTo(payload));
                    Assert.That(Volatile.Read(ref dispatched), Is.EqualTo(1));
                    if (graceful)
                    {
                        await peer.CloseAsync(0x100, token);
                        await serving.WaitAsync(token);
                        Assert.That(stop.IsCancellationRequested, Is.False, "Graceful completion must not require the caller's abort token.");
                    }
                }
                finally
                {
                    release.TrySetResult();
                    stop.Cancel();
                    try
                    {
                        await serving.WaitAsync(token);
                        Assert.That(connection.IsClosed, Is.True, "The protocol runner owns and disposes its native connection.");
                    }
                    finally
                    {
                        foreach (var incoming in serverStreams) await incoming.DisposeAsync();
                        if (control != null) await control.DisposeAsync();
                    }
                }
            });
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<long> ReadNativePeerIntegerAsync(QuicStream stream, CancellationToken token)
        {
            var bytes = new byte[8];
            await stream.ReadExactlyAsync(bytes.AsMemory(0, 1), token);
            var width = 1 << (bytes[0] >> 6);
            if (width != 1) await stream.ReadExactlyAsync(bytes.AsMemory(1, width - 1), token);
            var offset = 0;
            return NativePeerInteger(bytes, ref offset);
        }
        private static long NativePeerInteger(byte[] wire, ref int offset)
        {
            Assert.That(offset, Is.LessThan(wire.Length));
            var width = 1 << (wire[offset] >> 6);
            Assert.That(width, Is.LessThanOrEqualTo(wire.Length - offset));
            long result = wire[offset++] & 63;
            for (var i = 1; i < width; i++) result = (result << 8) | wire[offset++];
            return result;
        }

        [Test]
        public async Task NativeDisposalCompletesBothPendingDirectionObservers()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await NativeDisposedDirectionCore();
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeDisposedDirectionCore()
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var remote = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await remote.WriteAsync(new byte[] { 63 }, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var reads = NativeDirection(native, "ReadsClosed");
                var writes = NativeDirection(native, "WritesClosed");
                Assert.That(reads.IsCompleted, Is.False);
                Assert.That(writes.IsCompleted, Is.False);
                native.Dispose();
                await Assert.ThatAsync(async () => await reads.WaitAsync(token), Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.QuicError)).EqualTo(QuicError.OperationAborted));
                await Assert.ThatAsync(async () => await writes.WaitAsync(token), Throws.TypeOf<QuicException>().With.Property(nameof(QuicException.QuicError)).EqualTo(QuicError.OperationAborted));
                Assert.That(native.IsClosed, Is.True);
            });
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task NativeSendCore(int length)
        {
            await ExerciseNativeHandshake(false, streams: async (connection, peer, token) =>
            {
                await using var stream = await peer.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token);
                await stream.WriteAsync(new byte[] { 42 }, true, token);
                var accepting = (Task)ListenerCall(connection, "AcceptStreamAsync", token);
                await accepting.WaitAsync(token);
                using var native = (SafeHandle)(accepting.GetType().GetProperty("Result")?.GetValue(accepting)
                    ?? throw new AssertionException("Missing native stream."));
                var input = new byte[8];
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.EqualTo(1));
                Assert.That(input[0], Is.EqualTo(42));
                Assert.That(await (ValueTask<int>)ListenerCall(native, "ReadAsync", input.AsMemory(), token), Is.Zero);
                var payload = new byte[length + 6];
                for (var i = 0; i < length; i++) payload[i + 3] = (byte)(i * 53);
                var writing = ((ValueTask)ListenerCall(native, "WriteAsync", new ReadOnlyMemory<byte>(payload, 3, length), true, token)).AsTask();
                var readback = new byte[length];
                var chunk = new byte[1023]; var total = 0;
                while (true)
                {
                    var count = await stream.ReadAsync(chunk, token);
                    if (count == 0) break;
                    Assert.That(total + count, Is.LessThanOrEqualTo(length));
                    chunk.AsSpan(0, count).CopyTo(readback.AsSpan(total)); total += count;
                }
                await writing.WaitAsync(token);
                Assert.That(total, Is.EqualTo(length));
                Assert.That(readback, Is.EqualTo(payload.AsSpan(3, length).ToArray()));
                await Assert.ThatAsync(async () => await (ValueTask)ListenerCall(native, "WriteAsync", ReadOnlyMemory<byte>.Empty, false, token),
                    Throws.InstanceOf<System.IO.IOException>());
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
        private static async Task ExerciseNativeHandshake(bool stopListenerFirst, QuicStreamType? streamType = null, bool credit = false, Func<SafeHandle, QuicConnection, CancellationToken, Task>? streams = null, bool unbuffered = false, int peerCredit = 8)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var api = OpenApi();
            using var registration = Register(api);
            using var configuration = streamType.HasValue || streams != null
                ? (SafeHandle)ListenerCall(registration, unbuffered ? "CreateUnbufferedStreamConfiguration" : "CreateStreamConfiguration", new byte[] { (byte)'h', (byte)'3' },
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
                MaxInboundBidirectionalStreams = peerCredit,
                MaxInboundUnidirectionalStreams = peerCredit,
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
            if (!connection.IsClosed)
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
