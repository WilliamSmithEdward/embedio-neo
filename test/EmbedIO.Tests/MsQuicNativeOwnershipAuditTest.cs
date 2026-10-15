using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Native MsQuic acceptance and ownership audit. See
    // docs/project/http3-native-provider-audit.md for findings and evidence.
    public sealed class MsQuicNativeOwnershipAuditTest
    {
        private const int AcceptQueueCapacity = 256;
        private static readonly byte[] H3 = { (byte)'h', (byte)'3' };

        // RFC 9000 section 5.2.2: a server refusing a new connection SHOULD send
        // CONNECTION_CLOSE with CONNECTION_REFUSED. The full-queue path must not
        // drop an already admitted handshake silently.
        [Test]
        public async Task OverflowedAcceptQueueRefusesTheConnection()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseQueueOverflow().ConfigureAwait(false);
        }

        // The started-listener case already completes a pending accept on stop.
        // An acceptance-enabled listener that never started must do the same.
        [Test]
        public async Task UnstartedStopReleasesAPendingConnectionAccept()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            using var api = OpenApi();
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var listener = (SafeHandle)Call(registration, "CreateListener");
            Call(listener, "EnableAcceptance");
            var pending = (Task)Call(listener, "AcceptAsync", CancellationToken.None);
            Assert.That(pending.IsCompleted, Is.False);
            await ((Task)Call(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await Assert.ThatAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false),
                Throws.InstanceOf<ChannelClosedException>());
        }

        [Test]
        public async Task ConnectionBeforeAcceptanceIsRefusedPromptly()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseRefusalWithoutAcceptance().ConfigureAwait(false);
        }

        [Test]
        public async Task CanceledAcceptDoesNotConsumeTheNextConnection()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseCanceledAccept().ConfigureAwait(false);
        }

        [Test]
        public async Task AcceptedConnectionRetainsEveryParentUntilItCloses()
        {
            if (!QuicListener.IsSupported) { Assert.Ignore("The host does not provide MsQuic."); return; }
            await ExerciseConnectionOutlivingParents().ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseQueueOverflow()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var api = OpenApi();
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            var listener = (SafeHandle)Call(registration, "CreateListener");
            var queued = new Task<QuicConnection>[AcceptQueueCapacity];
            Task<QuicConnection>? overflow = null;
            try
            {
                Call(listener, "EnableAcceptance");
                Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
                var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
                for (var i = 0; i < queued.Length; i++)
                    queued[i] = QuicConnection.ConnectAsync(ClientOptions(endpoint, certificate, 30), deadline.Token).AsTask();
                while (QueuedCount(listener) < AcceptQueueCapacity)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await Task.Delay(10, deadline.Token).ConfigureAwait(false);
                }

                // A short client handshake timeout bounds the failing case; the
                // assertion is the refusal kind, not elapsed time.
                overflow = QuicConnection.ConnectAsync(ClientOptions(endpoint, certificate, 10), deadline.Token).AsTask();
                await Task.WhenAny(overflow).ConfigureAwait(false);
                Assert.That(overflow.IsFaulted, Is.True, "The overflow handshake must not complete.");
                var error = overflow.Exception?.InnerException as QuicException;
                Assert.That(error?.QuicError, Is.EqualTo(QuicError.ConnectionRefused),
                    "Overflow failure: " + overflow.Exception?.InnerException);
                Assert.That(QueuedCount(listener), Is.EqualTo(AcceptQueueCapacity));
            }
            finally
            {
                await ((Task)Call(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                listener.Dispose();
                foreach (var client in queued)
                {
                    if (client == null) continue;
                    await Task.WhenAny(client).ConfigureAwait(false);
                    if (client.IsCompletedSuccessfully) await client.Result.DisposeAsync().ConfigureAwait(false);
                }
                if (overflow?.IsCompletedSuccessfully == true) await overflow.Result.DisposeAsync().ConfigureAwait(false);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseRefusalWithoutAcceptance()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var api = OpenApi();
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = (SafeHandle)Call(registration, "CreateListener");
            Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
            var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
            var connecting = QuicConnection.ConnectAsync(ClientOptions(endpoint, certificate, 10), deadline.Token).AsTask();
            await Task.WhenAny(connecting).ConfigureAwait(false);
            if (connecting.IsCompletedSuccessfully) await connecting.Result.DisposeAsync().ConfigureAwait(false);
            Assert.That((connecting.Exception?.InnerException as QuicException)?.QuicError, Is.EqualTo(QuicError.ConnectionRefused),
                "Refusal failure: " + connecting.Exception?.InnerException);
            await ((Task)Call(listener, "StopAsync")).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseCanceledAccept()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var api = OpenApi();
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var configuration = (SafeHandle)Call(registration, "CreateConfiguration", H3);
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            Call(configuration, "LoadServerCertificate", certificate);
            using var listener = (SafeHandle)Call(registration, "CreateListener");
            Call(listener, "EnableAcceptance");
            Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
            var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
            using (var cancel = new CancellationTokenSource())
            {
                var canceled = (Task)Call(listener, "AcceptAsync", cancel.Token);
                Assert.That(canceled.IsCompleted, Is.False);
                await cancel.CancelAsync().ConfigureAwait(false);
                await Task.WhenAny(canceled).ConfigureAwait(false);
                Assert.That(canceled.IsCanceled, Is.True);
            }
            var connecting = QuicConnection.ConnectAsync(ClientOptions(endpoint, certificate, 10), deadline.Token).AsTask();
            var accept = (Task)Call(listener, "AcceptAsync", deadline.Token);
            await accept.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var connection = Result(accept);
            Call(connection, "Configure", configuration);
            await using var peer = await connecting.WaitAsync(deadline.Token).ConfigureAwait(false);
            await Connected(connection).WaitAsync(deadline.Token).ConfigureAwait(false);
            await ((Task)Call(connection, "ShutdownAsync", 0x100L)).WaitAsync(deadline.Token).ConfigureAwait(false);
            await ((Task)Call(listener, "StopAsync")).WaitAsync(deadline.Token).ConfigureAwait(false);
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseConnectionOutlivingParents()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var api = OpenApi();
            using var registration = (SafeHandle)Call(api, "CreateRegistration");
            using var configuration = (SafeHandle)Call(registration, "CreateConfiguration", H3);
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            Call(configuration, "LoadServerCertificate", certificate);
            using var listener = (SafeHandle)Call(registration, "CreateListener");
            Call(listener, "EnableAcceptance");
            Call(listener, "Start", new IPEndPoint(IPAddress.Loopback, 0), H3);
            var endpoint = (IPEndPoint)Call(listener, "LocalEndPoint");
            var connecting = QuicConnection.ConnectAsync(ClientOptions(endpoint, certificate, 10), deadline.Token).AsTask();
            var accept = (Task)Call(listener, "AcceptAsync", deadline.Token);
            await accept.WaitAsync(deadline.Token).ConfigureAwait(false);
            using var connection = Result(accept);
            Call(connection, "Configure", configuration);
            await using var peer = await connecting.WaitAsync(deadline.Token).ConfigureAwait(false);
            await Connected(connection).WaitAsync(deadline.Token).ConfigureAwait(false);

            await ((Task)Call(listener, "StopAsync")).WaitAsync(deadline.Token).ConfigureAwait(false);
            listener.Dispose();
            configuration.Dispose();
            registration.Dispose();
            api.Dispose();
            // IsClosed reports native release; disposed owners stay open while
            // the accepted connection still holds its registration lease.
            Assert.That(listener.IsClosed, Is.True);
            Assert.That(registration.IsClosed, Is.False);
            Assert.That(api.IsClosed, Is.False);

            await ((Task)Call(connection, "ShutdownAsync", 0x100L)).WaitAsync(deadline.Token).ConfigureAwait(false);
            connection.Dispose();
            Assert.That(connection.IsClosed, Is.True);
            Assert.That(registration.IsClosed, Is.True);
            Assert.That(api.IsClosed, Is.True);
        }

        private static object Call(object owner, string method, params object[] arguments)
        {
            try
            {
                var member = owner.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertionException("Missing native member " + method + ".");
                var result = member.Invoke(owner, arguments);
                return member.ReturnType == typeof(void) ? typeof(void) : result ?? throw new AssertionException("Missing result from " + method + ".");
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static SafeHandle OpenApi()
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi")
                ?? throw new IgnoreException("The retained asset does not include the native provider.");
            return (SafeHandle)(type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
                ?? throw new AssertionException("Missing native API owner."));
        }

        private static SafeHandle Result(Task accept)
            => (SafeHandle)(accept.GetType().GetProperty("Result")?.GetValue(accept)
                ?? throw new AssertionException("Missing accepted native connection."));

        private static Task Connected(SafeHandle connection)
            => (Task)(connection.GetType().GetProperty("Connected", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(connection)
                ?? throw new AssertionException("Missing handshake completion."));

        private static int QueuedCount(SafeHandle listener)
        {
            var queue = listener.GetType().GetField("_accepted", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener)
                ?? throw new AssertionException("Missing native accept queue.");
            var reader = queue.GetType().GetProperty("Reader")?.GetValue(queue)
                ?? throw new AssertionException("Missing native accept queue reader.");
            return (int)(reader.GetType().GetProperty("Count")?.GetValue(reader)
                ?? throw new AssertionException("Missing native accept queue count."));
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static QuicClientConnectionOptions ClientOptions(IPEndPoint endpoint, X509Certificate2 certificate, int handshakeSeconds) => new()
        {
            RemoteEndPoint = endpoint,
            DefaultCloseErrorCode = 0x100,
            DefaultStreamErrorCode = 0x10c,
            HandshakeTimeout = TimeSpan.FromSeconds(handshakeSeconds),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                    && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                    && peer.GetCertHashString() == certificate.GetCertHashString(),
            },
        };
    }
}
