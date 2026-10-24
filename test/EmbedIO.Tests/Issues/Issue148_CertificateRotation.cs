using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Net;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // ServerCertificateSource: a certificate published while listeners run applies to new TLS handshakes,
    // over TCP and QUIC, while established connections keep the certificate they started with.
    [TestFixture]
    public sealed class Issue148_CertificateRotation
    {
        private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
        private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

        private static X509Certificate2 Create(string name, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null,
            string? usage = ServerAuthentication, X509KeyStorageFlags flags = X509KeyStorageFlags.Exportable)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            if (usage != null)
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, false));
            using var generated = request.CreateSelfSigned(notBefore ?? DateTimeOffset.UtcNow.AddMinutes(-5), notAfter ?? DateTimeOffset.UtcNow.AddHours(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, flags);
        }

        private static string Hash(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

        private static ServerCertificateSource NewSource(X509Certificate2? certificate)
            => (ServerCertificateSource)(InvalidInput.Invoke((Func<X509Certificate2, ServerCertificateSource>)(value => new ServerCertificateSource(value)), certificate)
                ?? throw new AssertionException("Missing source."));

        // The source ---------------------------------------------------------------------------------------------

        [Test]
        public void PublishedCertificatesBecomeCurrent()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            Assert.That(source.Current, Is.SameAs(first));
            source.Publish(second);
            Assert.That(source.Current, Is.SameAs(second));
            source.Publish(first);
            Assert.That(source.Current, Is.SameAs(first));
        }

        [Test]
        public void NullCertificatesAreRejected()
        {
            using var first = Create("first");
            var source = new ServerCertificateSource(first);
            Assert.That(Assert.Throws<ArgumentNullException>(() => NewSource(null))?.ParamName, Is.EqualTo("certificate"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => InvalidInput.Invoke((Action<X509Certificate2>)source.Publish, (object?)null))?.ParamName,
                Is.EqualTo("certificate"));
            Assert.That(source.Current, Is.SameAs(first));
        }

        public enum Unusable
        {
            NoPrivateKey,
            Expired,
            NotYetValid,
            ClientOnly,
        }

        private static X509Certificate2 CreateUnusable(Unusable kind)
        {
            switch (kind)
            {
                case Unusable.NoPrivateKey:
                    using (var full = Create("public"))
                        return X509CertificateLoader.LoadCertificate(full.RawData);
                case Unusable.Expired:
                    return Create("expired", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
                case Unusable.NotYetValid:
                    return Create("future", DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(2));
                default:
                    return Create("client", usage: ClientAuthentication);
            }
        }

        // A rejected certificate never becomes current; the source keeps the one it had.
        [TestCase(Unusable.NoPrivateKey)]
        [TestCase(Unusable.Expired)]
        [TestCase(Unusable.NotYetValid)]
        [TestCase(Unusable.ClientOnly)]
        public void UnusableCertificatesAreRejected(Unusable kind)
        {
            using var good = Create("good");
            using var bad = CreateUnusable(kind);
            Assert.That(Assert.Throws<ArgumentException>(() => NewSource(bad))?.ParamName, Is.EqualTo("certificate"));
            var source = new ServerCertificateSource(good);
            Assert.That(Assert.Throws<ArgumentException>(() => source.Publish(bad))?.ParamName, Is.EqualTo("certificate"));
            Assert.That(source.Current, Is.SameAs(good));
        }

        // A certificate without an enhanced key usage extension may be used for anything, including servers.
        [Test]
        public void CertificatesWithoutKeyUsageRestrictionsAreAccepted()
        {
            using var open = Create("open", usage: null);
            Assert.That(new ServerCertificateSource(open).Current, Is.SameAs(open));
        }

        [Test]
        public async Task ConcurrentPublicationsLeaveOneOfTheCertificates()
        {
            var certificates = Enumerable.Range(0, 8).Select(index => Create("concurrent" + index.ToString(CultureInfo.InvariantCulture))).ToArray();
            try
            {
                var source = new ServerCertificateSource(certificates[0]);
                using var start = new ManualResetEventSlim();
                var writers = certificates.Select(certificate => Task.Run(() =>
                {
                    start.Wait();
                    for (var i = 0; i < 200; i++)
                        source.Publish(certificate);
                })).ToArray();
                start.Set();
                await Task.WhenAll(writers);
                Assert.That(certificates, Does.Contain(source.Current));
            }
            finally
            {
                foreach (var certificate in certificates)
                    certificate.Dispose();
            }
        }

        // Configuration ------------------------------------------------------------------------------------------

        [Test]
        public void ASourceAndAFixedCertificateCannotBothBeConfigured()
        {
            using var first = Create("first");
            var source = new ServerCertificateSource(first);
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var server = new WebServer(options => options.WithUrlPrefix(HttpsSmoke.GetUrl()).WithCertificate(first).WithCertificateSource(source));
            });
            Assert.Throws<InvalidOperationException>(() =>
            {
                using var server = new WebServer(options => options.WithUrlPrefix(HttpsSmoke.GetUrl()).WithCertificateSource(source).WithCertificateThumbprint("00"));
            });
        }

        [Test]
        public void TheSourceIsLockedWithTheOptions()
        {
            using var first = Create("first");
            var source = new ServerCertificateSource(first);
            using var server = new WebServer(options => options.WithUrlPrefix(HttpsSmoke.GetUrl()).WithCertificateSource(source));
            Assert.That(server.Options.CertificateSource, Is.SameAs(source));
            Assert.Throws<InvalidOperationException>(() => server.Options.CertificateSource = null);
        }

        [Test]
        public void TheListenerRequiresASource()
            => Assert.That(Assert.Throws<ArgumentNullException>(() =>
                InvalidInput.Invoke((Func<ServerCertificateSource, Net.HttpListener>)Net.HttpListener.CreateWithCertificateSource, (object?)null))?.ParamName,
                Is.EqualTo("certificateSource"));

        [Test]
        public void PublishedNullAndDefaultCertificateCallsRemainAvailable()
        {
            using var positionalNull = new Net.HttpListener(null);
            using var positionalDefault = new Net.HttpListener(default);
            using var namedNull = new Net.HttpListener(certificate: null);
            using var typedNull = new Net.HttpListener((X509Certificate?)null);
            using var parameterless = new Net.HttpListener();
            Assert.Multiple(() =>
            {
                Assert.That(positionalNull.IsListening, Is.False);
                Assert.That(positionalDefault.IsListening, Is.False);
                Assert.That(namedNull.IsListening, Is.False);
                Assert.That(typedNull.IsListening, Is.False);
                Assert.That(parameterless.IsListening, Is.False);
            });
        }

        [Test]
        public async Task TheFactoryBorrowsThePublishedSourceAndCertificates()
        {
            using var first = Create("factory-first");
            using var second = Create("factory-second");
            var source = new ServerCertificateSource(first);
            using var listener = Net.HttpListener.CreateWithCertificateSource(source);
            var prefix = HttpsSmoke.GetUrl();
            var port = new Uri(prefix).Port;
            listener.AddPrefix(prefix);
            listener.Start();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using (var before = await Connection.OpenAsync(port, stop.Token))
            {
                Assert.That(before.Certificate, Is.EqualTo(Hash(first)));
                source.Publish(second);
                using var after = await Connection.OpenAsync(port, stop.Token);
                Assert.That(after.Certificate, Is.EqualTo(Hash(second)));
                Assert.That(before.Tls.IsAuthenticated, Is.True);
            }

            listener.Dispose();
            Assert.That(first.RawData, Is.Not.Empty);
            Assert.That(second.RawData, Is.Not.Empty);
            Assert.That(source.Current, Is.SameAs(second));
            source.Publish(first);
            Assert.That(source.Current, Is.SameAs(first));
        }

        // Real TLS over TCP --------------------------------------------------------------------------------------

        private sealed class Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Task _running;

            internal Host(ServerCertificateSource source, HttpListenerMode mode = HttpListenerMode.EmbedIO, string? url = null)
            {
                Url = url ?? HttpsSmoke.GetUrl();
                Port = new Uri(Url).Port;
                var options = new WebServerOptions().WithUrlPrefix(Url).WithMode(mode).WithCertificateSource(source);
                Server = new WebServer(options);
                Server.WithModule(new ActionModule("/slow", HttpVerbs.Get, async context =>
                {
                    Entered.TrySetResult(true);
                    await Release.Task.ConfigureAwait(false);
                    await context.SendStringAsync("slow", "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
                }));
                Server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                    context.SendStringAsync("ok:" + context.Request.ProtocolVersion, "text/plain", WebServer.Utf8NoBomEncoding)));
                _running = Server.RunAsync(_stop.Token);
            }

            internal string Url { get; }

            internal int Port { get; }

            internal WebServer Server { get; }

            internal TaskCompletionSource<bool> Entered { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> Release { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task Running => _running;

            internal void Cancel() => _stop.Cancel();

            public async ValueTask DisposeAsync()
            {
                Release.TrySetResult(true);
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                finally
                {
                    Server.Dispose();
                    _stop.Dispose();
                }
            }
        }

        // A TLS connection that records the certificate its handshake received.
        private sealed class Connection : IDisposable
        {
            private readonly TcpClient _tcp;

            private readonly int _port;

            private Connection(TcpClient tcp, SslStream tls, string certificate, int port)
            {
                _tcp = tcp;
                _port = port;
                Tls = tls;
                Certificate = certificate;
            }

            internal SslStream Tls { get; }

            internal string Certificate { get; }

            internal static async Task<Connection> OpenAsync(int port, CancellationToken token)
            {
                var tcp = new TcpClient();
                try
                {
                    await tcp.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
                    string? seen = null;
                    var tls = new SslStream(tcp.GetStream(), false, (_, peer, _, _) =>
                    {
                        seen = peer == null ? null : Hash(peer);
                        return peer != null;
                    });
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
                    }, token).ConfigureAwait(false);
                    return new Connection(tcp, tls, seen ?? throw new AssertionException("The handshake presented no certificate."), port);
                }
                catch
                {
                    tcp.Dispose();
                    throw;
                }
            }

            internal async Task<string> GetAsync(string path, CancellationToken token)
            {
                // The Host must match the listener's 127.0.0.1 prefix.
                var request = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
                await Tls.WriteAsync(request, token).ConfigureAwait(false);
                var status = await ReadLineAsync(token).ConfigureAwait(false);
                Assert.That(status, Does.StartWith("HTTP/1.1 200"));
                long? length = null;
                var chunked = false;
                for (var line = await ReadLineAsync(token).ConfigureAwait(false); line.Length > 0; line = await ReadLineAsync(token).ConfigureAwait(false))
                {
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        length = long.Parse(line.Substring(15).Trim(), CultureInfo.InvariantCulture);
                    else if (line.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                        chunked = line.Contains("chunked", StringComparison.OrdinalIgnoreCase);
                }

                using var body = new MemoryStream();
                if (chunked)
                {
                    for (var size = await ReadChunkSizeAsync(token).ConfigureAwait(false); size > 0; size = await ReadChunkSizeAsync(token).ConfigureAwait(false))
                    {
                        var chunk = new byte[size];
                        await Tls.ReadExactlyAsync(chunk, token).ConfigureAwait(false);
                        body.Write(chunk);
                        Assert.That(await ReadLineAsync(token).ConfigureAwait(false), Is.Empty);
                    }

                    // No trailers: the empty line that ends the message.
                    Assert.That(await ReadLineAsync(token).ConfigureAwait(false), Is.Empty);
                }
                else
                {
                    var bytes = new byte[length ?? throw new AssertionException("The response has neither a length nor chunked framing.")];
                    await Tls.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                    body.Write(bytes);
                }

                return Encoding.UTF8.GetString(body.ToArray());
            }

            private async Task<int> ReadChunkSizeAsync(CancellationToken token)
                => int.Parse((await ReadLineAsync(token).ConfigureAwait(false)).Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            private async Task<string> ReadLineAsync(CancellationToken token)
            {
                var line = new StringBuilder();
                var one = new byte[1];
                while (!line.ToString().EndsWith("\r\n", StringComparison.Ordinal))
                {
                    if (await Tls.ReadAsync(one, token).ConfigureAwait(false) == 0)
                        throw new IOException("The server closed the connection in the middle of a response.");
                    line.Append((char)one[0]);
                }

                return line.ToString(0, line.Length - 2);
            }

            public void Dispose()
            {
                Tls.Dispose();
                _tcp.Dispose();
            }
        }

        [Test]
        public async Task NewHandshakesUseThePublishedCertificateAndOpenConnectionsKeepTheirs()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            using var before = await Connection.OpenAsync(host.Port, stop.Token);
            Assert.That(before.Certificate, Is.EqualTo(Hash(first)));
            Assert.That(await before.GetAsync("/", stop.Token), Is.EqualTo("ok:1.1"));

            source.Publish(second);

            using var after = await Connection.OpenAsync(host.Port, stop.Token);
            Assert.That(after.Certificate, Is.EqualTo(Hash(second)));
            Assert.That(await after.GetAsync("/", stop.Token), Is.EqualTo("ok:1.1"));

            // The connection that started before the replacement keeps serving requests.
            Assert.That(await before.GetAsync("/", stop.Token), Is.EqualTo("ok:1.1"));
        }

        [Test]
        public async Task ARequestInFlightCompletesAcrossAReplacement()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            using var live = await Connection.OpenAsync(host.Port, stop.Token);
            var pending = live.GetAsync("/slow", stop.Token);
            await host.Entered.Task.WaitAsync(stop.Token);
            source.Publish(second);
            using var other = await Connection.OpenAsync(host.Port, stop.Token);
            Assert.That(other.Certificate, Is.EqualTo(Hash(second)));
            host.Release.SetResult(true);
            Assert.That(await pending, Is.EqualTo("slow"));
            Assert.That(live.Certificate, Is.EqualTo(Hash(first)));
        }

        [Test]
        public async Task ARejectedReplacementLeavesTheServerOnItsCertificate()
        {
            using var first = Create("first");
            using var expired = CreateUnusable(Unusable.Expired);
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Assert.Throws<ArgumentException>(() => source.Publish(expired));
            using var connection = await Connection.OpenAsync(host.Port, stop.Token);
            Assert.That(connection.Certificate, Is.EqualTo(Hash(first)));
            Assert.That(await connection.GetAsync("/", stop.Token), Is.EqualTo("ok:1.1"));
        }

        // Every handshake gets one of the published certificates, whole, while they change underneath it.
        [Test]
        public async Task ConcurrentHandshakesDuringRotationAllSucceed()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var seen = new ConcurrentBag<string>();
            using var done = new CancellationTokenSource();

            var publisher = Task.Run(async () =>
            {
                var flip = false;
                while (!done.IsCancellationRequested)
                {
                    source.Publish(flip ? first : second);
                    flip = !flip;
                    await Task.Yield();
                }
            });

            var clients = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    using var connection = await Connection.OpenAsync(host.Port, stop.Token).ConfigureAwait(false);
                    Assert.That(await connection.GetAsync("/", stop.Token).ConfigureAwait(false), Is.EqualTo("ok:1.1"));
                    seen.Add(connection.Certificate);
                }
            })).ToArray();

            try { await Task.WhenAll(clients); }
            finally
            {
                done.Cancel();
                await publisher;
            }

            Assert.That(seen.Count, Is.EqualTo(128));
            Assert.That(seen.Distinct(), Is.SubsetOf(new[] { Hash(first), Hash(second) }));
        }

        // One source can serve several servers and endpoints; separate sources stay separate.
        [Test]
        public async Task ServersSharingASourceRotateTogetherAndOthersDoNot()
        {
            using var first = Create("first");
            using var second = Create("second");
            using var other = Create("other");
            var shared = new ServerCertificateSource(first);
            var separate = new ServerCertificateSource(other);
            await using var one = new Host(shared);
            await using var two = new Host(shared);
            await using var three = new Host(separate);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            shared.Publish(second);
            foreach (var host in new[] { one, two })
            {
                using var connection = await Connection.OpenAsync(host.Port, stop.Token);
                Assert.That(connection.Certificate, Is.EqualTo(Hash(second)));
            }

            using var unaffected = await Connection.OpenAsync(three.Port, stop.Token);
            Assert.That(unaffected.Certificate, Is.EqualTo(Hash(other)));
        }

        // Stopping the server, even with a request in flight, leaves the application's certificates alone.
        [Test]
        public async Task ShutdownAfterRotationNeitherHangsNorDisposesCertificates()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            using var live = await Connection.OpenAsync(host.Port, stop.Token);
            var pending = live.GetAsync("/slow", stop.Token);
            await host.Entered.Task.WaitAsync(stop.Token);
            source.Publish(second);
            host.Cancel();
            await host.DisposeAsync();
            try { await pending; }
            catch (Exception error) when (error is IOException or OperationCanceledException or AssertionException)
            {
                // A request the server abandoned while stopping may fail; the server must still stop.
            }

            foreach (var certificate in new[] { first, second })
            {
                Assert.That(certificate.Handle, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(certificate.HasPrivateKey, Is.True);
                using var key = certificate.GetRSAPrivateKey();
                Assert.That(key, Is.Not.Null);
            }
        }

        // HTTP/2 over TLS (ALPN) -------------------------------------------------------------------------------

        private static HttpClient CreateClient(ConcurrentQueue<string> seen)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) =>
                    {
                        if (peer == null)
                            return false;
                        seen.Enqueue(Hash(peer));
                        return true;
                    },
                },
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        private static async Task<string> GetAsync(ConcurrentQueue<string> seen, string url, Version version, CancellationToken token)
        {
            using var client = CreateClient(seen);
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            Assert.That(response.Version, Is.EqualTo(version));
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        [Test]
        public async Task Http2HandshakesUseThePublishedCertificate()
        {
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var seen = new ConcurrentQueue<string>();

            Assert.That(await GetAsync(seen, host.Url, HttpVersion.Version20, stop.Token), Is.EqualTo("ok:2.0"));
            source.Publish(second);
            Assert.That(await GetAsync(seen, host.Url, HttpVersion.Version20, stop.Token), Is.EqualTo("ok:2.0"));
            Assert.That(seen, Is.EqualTo(new[] { Hash(first), Hash(second) }));
        }

        // HTTP/3 -------------------------------------------------------------------------------------------------

        private static void RequireHttp3()
        {
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3Listener") == null)
                Assert.Ignore("HTTP/3 is unavailable in the selected asset.");
            if (!QuicConnection.IsSupported)
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1")
                    Assert.Fail("QUIC is required.");
                Assert.Ignore("QUIC is not supported on this machine.");
            }
        }

        private static string LocalhostUrl() => "https://localhost:" + new Uri(Resources.GetServerAddress()).Port.ToString(CultureInfo.InvariantCulture) + "/";

        [Test]
        public async Task Http3HandshakesUseThePublishedCertificate()
        {
            RequireHttp3();
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source, HttpListenerMode.EmbedIOHttp3, LocalhostUrl());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var seen = new ConcurrentQueue<string>();

            Assert.That(await GetAsync(seen, host.Url, HttpVersion.Version30, stop.Token), Is.EqualTo("ok:3.0"));
            source.Publish(second);
            Assert.That(await GetAsync(seen, host.Url, HttpVersion.Version30, stop.Token), Is.EqualTo("ok:3.0"));
            source.Publish(first);
            Assert.That(await GetAsync(seen, host.Url, HttpVersion.Version30, stop.Token), Is.EqualTo("ok:3.0"));
            Assert.That(seen, Is.EqualTo(new[] { Hash(first), Hash(second), Hash(first) }));
        }

        // The native TLS credentials alive in the process: Schannel credential handles on Windows,
        // prepared OpenSSL credentials elsewhere.
        private static long LiveTlsCredentials()
        {
            var counter = EngineInternals.Type(OperatingSystem.IsWindows() ? "EmbedIO.Quic.SchannelTls" : "EmbedIO.Quic.OpenSslTls")
                .GetProperty("ActiveCredentials", EngineInternals.Members)
                ?? throw new AssertionException("Missing TLS credential counter.");
            return Convert.ToInt64(counter.GetValue(null), CultureInfo.InvariantCulture);
        }

        // Open connections that selected one certificate share its credential, and stopping the
        // server frees it. Without sharing, each connection would hold a credential of its own.
        [Test]
        public async Task Http3ConnectionsShareTheCertificatesCredential()
        {
            RequireHttp3();
            using var first = Create("first");
            var source = new ServerCertificateSource(first);
            var before = LiveTlsCredentials();
            var seen = new ConcurrentQueue<string>();
            var clients = Enumerable.Range(0, 4).Select(_ => CreateClient(seen)).ToArray();

            try
            {
                await using (var host = new Host(source, HttpListenerMode.EmbedIOHttp3, LocalhostUrl()))
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

                    foreach (var client in clients)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, host.Url)
                        {
                            Version = HttpVersion.Version30,
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        };
                        using var response = await client.SendAsync(request, stop.Token);
                        Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("ok:3.0"));
                    }

                    // A credential freed elsewhere in the process can only lower the count.
                    Assert.That(LiveTlsCredentials() - before, Is.LessThanOrEqualTo(1));
                    Assert.That(seen, Has.Count.EqualTo(4));
                    Assert.That(seen, Is.All.EqualTo(Hash(first)));
                }

                Assert.That(LiveTlsCredentials(), Is.LessThanOrEqualTo(before));
            }

            finally
            {
                foreach (var client in clients)
                    client.Dispose();
            }
        }

        // Combined hosting shares one source between TCP and QUIC.
        [Test]
        public async Task CombinedHostingRotatesEveryProtocol()
        {
            RequireHttp3();
            using var first = Create("first");
            using var second = Create("second");
            var source = new ServerCertificateSource(first);
            await using var host = new Host(source, HttpListenerMode.EmbedIOCombined, LocalhostUrl());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            source.Publish(second);
            foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 })
            {
                var seen = new ConcurrentQueue<string>();
                Assert.That(await GetAsync(seen, host.Url, version, stop.Token), Is.EqualTo("ok:" + version.ToString(2)));
                Assert.That(seen, Is.EqualTo(new[] { Hash(second) }), version.ToString());
            }
        }
    }
}
