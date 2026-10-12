using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class Http1RejectionLifetimeTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly byte[] BadHead = Encoding.ASCII.GetBytes("Host: localhost\r\nX: bad\nGET / HTTP/1.1\r\nHost: localhost\r\n\r\n");

        [TestCase(false, "deadline")]
        [TestCase(true, "deadline")]
        [TestCase(false, "stop")]
        [TestCase(true, "stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "force")]
        [TestCase(true, "force")]
        public async Task OutputEofDoesNotReleaseOwnershipBeforePhysicalRetirement(bool tls, string ending)
        {
            await using var host = new Host(tls, null);
            using var peer = await host.ConnectAsync();
            var connection = await host.ConnectionAsync(peer);
            var completion = Completion(connection);
            await peer.Stream.WriteAsync(BadHead, host.Token);
            Assert.That(await ReadToEndAsync(peer.Stream, host.Token), Does.StartWith("HTTP/1.1 400 "));
            Assert.That(completion.IsCompleted, Is.False, "The peer's send direction is still open after response EOF.");
            Assert.That(host.Calls, Is.Zero);
            using (var excess = new TcpClient())
            {
                await excess.ConnectAsync(IPAddress.Loopback, host.Port, host.Token);
                Assert.That(await excess.GetStream().ReadAsync(new byte[1], host.Token), Is.Zero,
                    "The rejected connection retains its only admission slot until physical disposal.");
            }
            var watch = Stopwatch.StartNew();
            if (ending == "stop") host.Server.Listener.Stop();
            else if (ending == "dispose") host.Server.Listener.Dispose();
            else if (ending == "force") ((IDisposable)connection).Dispose();
            await completion.WaitAsync(TimeSpan.FromSeconds(5), host.Token);
            TestContext.Out.WriteLine($"REJECTION RETIRED tls={tls} ending={ending} elapsedMs={watch.Elapsed.TotalMilliseconds:F3}");
            if (ending is "deadline" or "force")
                Assert.That(await host.HealthyAsync(), Is.EqualTo("healthy"));
            else
            {
                Assert.That(host.Server.Listener.IsListening, Is.False);
                using var replacement = new Net.HttpListener();
                replacement.AddPrefix(host.Url.Replace("https://", "http://", StringComparison.Ordinal));
                replacement.Start();
                Assert.That(replacement.IsListening, Is.True, "Stop/dispose released the physical endpoint.");
            }
        }

        [Test]
        public async Task RejectionCannotExtendAnEarlierConfiguredHeaderDeadline()
        {
            await using var host = new Host(false, TimeSpan.FromMilliseconds(500));
            using var peer = await host.ConnectAsync();
            var connection = await host.ConnectionAsync(peer);
            var owner = Lifetime(connection);
            var original = Deadline(owner);
            await peer.Stream.WriteAsync(BadHead, host.Token);
            Assert.That(await ReadToEndAsync(peer.Stream, host.Token), Does.StartWith("HTTP/1.1 400 "));
            Assert.That(Deadline(owner), Is.LessThanOrEqualTo(original), "An earlier owned monotonic deadline must not be extended.");
            await Completion(connection).WaitAsync(TimeSpan.FromSeconds(5), host.Token);
            Assert.That(host.Calls, Is.Zero);
            Assert.That(await host.HealthyAsync(), Is.EqualTo("healthy"));
        }

        [Test]
        public async Task ExtraInputCannotRenewRejectionOrReachTheApplication()
        {
            await using var host = new Host(false, null);
            using var peer = await host.ConnectAsync();
            var connection = await host.ConnectionAsync(peer);
            var completion = Completion(connection);
            await peer.Stream.WriteAsync(BadHead, host.Token);
            Assert.That(await ReadToEndAsync(peer.Stream, host.Token), Does.StartWith("HTTP/1.1 400 "));
            var owner = Lifetime(connection);
            var deadline = Deadline(owner);
            var extra = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("GET / HTTP/1.1\r\nHost: localhost\r\n\r\n", 32)));
            long sent = 0;
            try
            {
                while (!completion.IsCompleted && sent < 4 * 1024 * 1024)
                {
                    await peer.Stream.WriteAsync(extra, host.Token);
                    sent += extra.Length;
                    await Task.Yield();
                }
            }
            catch (IOException) { }
            Assert.That(Deadline(owner), Is.LessThanOrEqualTo(deadline));
            await completion.WaitAsync(TimeSpan.FromSeconds(5), host.Token);
            TestContext.Out.WriteLine($"REJECTION DISCARD offeredAfterEof={sent}");
            Assert.That(host.Calls, Is.Zero, "Discarded input is never reparsed or dispatched.");
            Assert.That(await host.HealthyAsync(), Is.EqualTo("healthy"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ErrorWriteFailureOrBackpressureStillRetiresTheOwnedTransport(bool block)
        {
            await using var host = new Host(false, null);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var peer = new TcpClient();
            var accept = listener.AcceptSocketAsync(host.Token);
            await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, host.Token);
            var socket = await accept;
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true)
                ?? throw new AssertionException("Missing connection owner.");
            var connection = Activator.CreateInstance(type, Hidden, null, new object[] { socket, host.Endpoint, 60000 }, null)
                ?? throw new AssertionException("Missing isolated connection.");
            var oldOwner = Lifetime(connection);
            Method(oldOwner, "StopDeadline").CreateDelegate<Action>(oldOwner)();
            ((Timer)(oldOwner.GetType().GetProperty("DeadlineTimer", Hidden)?.GetValue(oldOwner)
                ?? throw new AssertionException("Missing timer."))).Dispose();
            ((Stream)(type.GetProperty("Stream", Hidden)?.GetValue(connection)
                ?? throw new AssertionException("Missing borrowed stream."))).Dispose();
            var observed = new ControlledWriteStream(block);
            var close = Method(connection, "CloseTransport").CreateDelegate<Action<bool, bool>>(connection);
            var owner = Activator.CreateInstance(oldOwner.GetType(), Hidden, null, new object[]
            {
                Field(connection, "_connectionSync"), socket, observed, (Action)(() => close(false, true)),
            }, null) ?? throw new AssertionException("Missing isolated lifetime.");
            SetField(connection, "_lifetime", owner);
            SetField(connection, "<Stream>k__BackingField", observed);
            SetField(connection, "_buffer", new byte[8192]);
            Method(owner, "ArmDeadline").CreateDelegate<Action<int>>(owner)(60000);
            var completion = Completion(connection);
            try
            {
                var reject = Method(connection, "RejectHeadAsync").CreateDelegate<Func<byte[], Task>>(connection);
                var writing = reject(Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                await observed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), host.Token);
                Assert.That(await Assert.ThrowsAsync<IOException>(async () => await writing.WaitAsync(TimeSpan.FromSeconds(5), host.Token)), Is.Not.Null);
                await completion.WaitAsync(TimeSpan.FromSeconds(5), host.Token);
                Assert.That(observed.Disposals, Is.EqualTo(1));
                Assert.That(await peer.GetStream().ReadAsync(new byte[1], host.Token), Is.Zero, "The owned socket was physically retired.");
                Assert.That(await host.HealthyAsync(), Is.EqualTo("healthy"));
            }
            finally { ((IDisposable)connection).Dispose(); }
        }

        private static async Task<string> ReadToEndAsync(Stream stream, CancellationToken token)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output, token);
            return Encoding.ASCII.GetString(output.ToArray());
        }
        private static MethodInfo Method(object owner, string name)
            => owner.GetType().GetMethod(name, Hidden) ?? throw new AssertionException("Missing operation " + name);
        private static object Field(object owner, string name)
            => owner.GetType().GetField(name, Hidden)?.GetValue(owner) ?? throw new AssertionException("Missing field " + name);
        private static void SetField(object owner, string name, object value)
            => (owner.GetType().GetField(name, Hidden) ?? throw new AssertionException("Missing field " + name)).SetValue(owner, value);
        private static object Lifetime(object connection) => Field(connection, "_lifetime");
        private static long Deadline(object lifetime) => (long)Field(lifetime, "_deadline");
        private static Task Completion(object connection)
        {
            var owner = Lifetime(connection);
            return owner.GetType().GetProperty("Completion", Hidden)?.GetValue(owner) as Task
                ?? throw new AssertionException("Missing physical completion.");
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _certificate = HttpsSmoke.CreateCertificate();
            private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
            private readonly Task _running;
            private readonly bool _tls;
            private int _calls;
            internal Host(bool tls, TimeSpan? headerDeadline)
            {
                _tls = tls;
                Url = HttpsSmoke.GetUrl();
                if (!tls) Url = Url.Replace("https://", "http://", StringComparison.Ordinal);
                Port = new Uri(Url).Port;
                Server = new WebServer(o => o.WithUrlPrefix(Url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(_certificate)
                    .WithConnectionLimits(1, headerDeadline)).WithAction("/", HttpVerbs.Any, context =>
                    {
                        Interlocked.Increment(ref _calls);
                        return context.SendStringAsync("healthy", "text/plain", Encoding.UTF8);
                    });
                _running = Server.RunAsync(Token);
                Endpoint = TcpRegistrationInventory.Find(IPAddress.Loopback, Port);
            }
            internal string Url { get; }
            internal int Port { get; }
            internal WebServer Server { get; }
            internal object Endpoint { get; }
            internal CancellationToken Token => _stop.Token;
            internal int Calls => Volatile.Read(ref _calls);
            internal async Task<Peer> ConnectAsync()
            {
                var tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, Port, Token);
                    Stream stream = tcp.GetStream();
                    if (_tls)
                    {
                        var pin = _certificate.GetCertHashString(HashAlgorithmName.SHA256);
                        var ssl = new SslStream(stream, false, (_, peer, _, errors) => peer != null
                            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                            && peer.GetCertHashString(HashAlgorithmName.SHA256) == pin);
                        stream = ssl;
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, Token);
                    }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"), Token);
                    return new Peer(tcp, stream);
                }
                catch { tcp.Dispose(); throw; }
            }
            internal async Task<object> ConnectionAsync(Peer peer)
            {
                var local = (IPEndPoint)(peer.Tcp.Client.LocalEndPoint ?? throw new AssertionException("Missing local endpoint."));
                var snapshot = Method(Endpoint, "PendingConnections");
                while (true)
                {
                    Token.ThrowIfCancellationRequested();
                    foreach (var connection in (Array)(snapshot.Invoke(Endpoint, null) ?? throw new AssertionException("Missing pending inventory.")))
                    {
                        var remote = (IPEndPoint)(connection.GetType().GetProperty("RemoteEndPoint", Hidden)?.GetValue(connection)
                            ?? throw new AssertionException("Missing peer endpoint."));
                        if (remote.Port == local.Port && remote.Address.MapToIPv6().Equals(local.Address.MapToIPv6())) return connection;
                    }
                    await Task.Yield();
                }
            }
            internal async Task<string> HealthyAsync()
            {
                using var client = HttpsSmoke.CreateClient(_certificate);
                return await client.GetStringAsync(Url, Token);
            }
            public async ValueTask DisposeAsync()
            {
                try { _stop.Cancel(); await _running.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { Server.Dispose(); _stop.Dispose(); _certificate.Dispose(); }
            }
        }
        private sealed class Peer(TcpClient tcp, Stream stream) : IDisposable
        {
            internal TcpClient Tcp => tcp;
            internal Stream Stream => stream;
            public void Dispose() { stream.Dispose(); tcp.Dispose(); }
        }
        private sealed class ControlledWriteStream(bool block) : MemoryStream
        {
            private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int Disposals { get; private set; }
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Entered.TrySetResult();
                return block ? _pending.Task : Task.FromException(new IOException("Controlled rejection-write failure."));
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { Disposals++; _pending.TrySetException(new IOException("Owned transport retired during blocked write.")); }
                base.Dispose(disposing);
            }
        }
    }
}
