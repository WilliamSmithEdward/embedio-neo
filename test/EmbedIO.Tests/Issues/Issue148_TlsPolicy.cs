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
using System.Security.Authentication;
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
    // ServerTlsPolicy: client certificates, protocol versions and SNI certificate selection.
    [TestFixture]
    public sealed class Issue148_TlsPolicy
    {
        private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";
        private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

        private static X509Certificate2 Create(string name, string usage = ServerAuthentication)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddDnsName(name);
            names.AddIpAddress(IPAddress.Loopback);
            names.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(usage) }, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        private static X509Certificate2 Client(string name) => Create(name, ClientAuthentication);

        private static string Hash(X509Certificate certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

        // Accepts exactly the given client certificates, whatever their chain; records what it was shown.
        private static Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> Pin(params X509Certificate2[] trusted)
        {
            var hashes = trusted.Select(Hash).ToArray();
            return (certificate, _, _) => hashes.Contains(Hash(certificate));
        }

        // Configuration ------------------------------------------------------------------------------------------

        [Test]
        public void DefaultsChangeNothing()
        {
            var policy = new ServerTlsPolicy();
            Assert.Multiple(() =>
            {
                Assert.That(policy.ClientCertificateMode, Is.EqualTo(ClientCertificateMode.None));
                Assert.That(policy.ClientCertificateValidator, Is.Null);
                Assert.That(policy.CheckCertificateRevocation, Is.False);
                Assert.That(policy.EnabledProtocols, Is.EqualTo(SslProtocols.None));
                Assert.That(policy.ServerCertificateSelector, Is.Null);
            });
        }

        [Test]
        public void UndefinedModesAreRejected()
        {
            var policy = new ServerTlsPolicy();
            Assert.Throws<ArgumentOutOfRangeException>(() => policy.ClientCertificateMode = (ClientCertificateMode)3);
            Assert.That(policy.ClientCertificateMode, Is.EqualTo(ClientCertificateMode.None));
        }

        [Test]
        public void ThePolicyIsLockedWithTheServerOptions()
        {
            using var certificate = Create("server");
            var policy = new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Allow };
            using var server = new WebServer(options => options.WithUrlPrefix(HttpsSmoke.GetUrl()).WithCertificate(certificate).WithTlsPolicy(policy));
            Assert.That(server.Options.TlsPolicy, Is.SameAs(policy));
            Assert.That(policy.ConfigurationLocked, Is.True);
            Assert.Throws<InvalidOperationException>(() => policy.ClientCertificateMode = ClientCertificateMode.Require);
            Assert.Throws<InvalidOperationException>(() => policy.ClientCertificateValidator = null);
            Assert.Throws<InvalidOperationException>(() => policy.CheckCertificateRevocation = true);
            Assert.Throws<InvalidOperationException>(() => policy.EnabledProtocols = SslProtocols.Tls12);
            Assert.Throws<InvalidOperationException>(() => policy.ServerCertificateSelector = null);
            Assert.Throws<InvalidOperationException>(() => server.Options.TlsPolicy = null);
        }

        [Test]
        public void TheListenerLocksItsPolicy()
        {
            using var certificate = Create("server");
            var policy = new ServerTlsPolicy();
            using var listener = new Net.HttpListener(new ServerCertificateSource(certificate), policy);
            Assert.That(policy.ConfigurationLocked, Is.True);
        }

        // Real TLS over TCP ---------------------------------------------------------------------------------------

        private sealed class Host : IAsyncDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Task _running;

            internal Host(X509Certificate2 certificate, ServerTlsPolicy policy, HttpListenerMode mode = HttpListenerMode.EmbedIO, string? url = null)
            {
                Url = url ?? HttpsSmoke.GetUrl();
                Port = new Uri(Url).Port;
                Server = new WebServer(options => options.WithUrlPrefix(Url).WithMode(mode).WithCertificate(certificate).WithTlsPolicy(policy));
                Server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    var client = context.Request.GetClientCertificate();
                    return context.SendStringAsync(client == null ? "anonymous" : Hash(client), "text/plain", WebServer.Utf8NoBomEncoding);
                }));
                _running = Server.RunAsync(_stop.Token);
            }

            internal string Url { get; }

            internal int Port { get; }

            internal WebServer Server { get; }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                try { await _running.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false); }
                finally
                {
                    Server.Dispose();
                    _stop.Dispose();
                }
            }
        }

        // One HTTP/1.1 request over a fresh TLS connection; returns the server certificate and the response body.
        private static async Task<(string Server, SslProtocols Protocol, string Body)> RequestAsync(int port, X509Certificate2? client, CancellationToken token,
            string targetHost = "localhost", SslProtocols protocols = SslProtocols.None)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            string? server = null;
            using var tls = new SslStream(tcp.GetStream(), false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = protocols,
                ApplicationProtocols = new() { SslApplicationProtocol.Http11 },
                RemoteCertificateValidationCallback = (_, peer, _, _) =>
                {
                    server = peer == null ? null : Hash(peer);
                    return peer != null;
                },
                ClientCertificates = client == null ? null : new X509CertificateCollection { client },
                // Present the certificate whatever issuers the server lists.
                LocalCertificateSelectionCallback = client is { } chosen ? (_, _, _, _, _) => chosen : null,
            }, token).ConfigureAwait(false);

            var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(request, token).ConfigureAwait(false);
            using var response = new MemoryStream();
            await tls.CopyToAsync(response, token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(response.ToArray());
            // Not an NUnit assertion: NUnit records a failed assertion even when a caller catches it, and a
            // refused client can see a closed connection, an empty response, after a TLS 1.3 handshake.
            if (!text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                throw new IOException("No successful response: " + (text.Length == 0 ? "the connection closed" : text.Split("\r\n")[0]));
            var body = text.Substring(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4);
            if (text.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase))
                body = body.Split("\r\n")[1];
            return (server ?? throw new AssertionException("No server certificate."), tls.SslProtocol, body);
        }

        // The handshake fails, or the server drops the connection right after it, without a response.
        private static async Task AssertRefusedAsync(int port, X509Certificate2? client, CancellationToken token)
        {
            string? accepted = null;
            try
            {
                accepted = (await RequestAsync(port, client, token).ConfigureAwait(false)).Body;
            }
            catch (Exception error) when (error is AuthenticationException or IOException or SocketException)
            {
                // Refused during or right after the handshake.
            }

            Assert.That(accepted, Is.Null, "The server accepted the connection.");
        }

        [Test]
        public async Task WithoutAPolicyNoClientCertificateIsRequested()
        {
            using var server = Create("server");
            using var client = Client("client");
            await using var host = new Host(server, new ServerTlsPolicy());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await RequestAsync(host.Port, client, stop.Token);
            Assert.That(result.Body, Is.EqualTo("anonymous"));
        }

        [Test]
        public async Task RequiredCertificatesReachHandlersAndMissingOnesAreRefused()
        {
            using var server = Create("server");
            using var client = Client("client");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(client) });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Assert.That((await RequestAsync(host.Port, client, stop.Token)).Body, Is.EqualTo(Hash(client)));
            await AssertRefusedAsync(host.Port, null, stop.Token);
            Assert.That((await RequestAsync(host.Port, client, stop.Token)).Body, Is.EqualTo(Hash(client)), "The server keeps serving after a refusal.");
        }

        [Test]
        public async Task AllowedCertificatesAreOptionalButStillValidated()
        {
            using var server = Create("server");
            using var trusted = Client("trusted");
            using var stranger = Client("stranger");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Allow, ClientCertificateValidator = Pin(trusted) });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Assert.That((await RequestAsync(host.Port, null, stop.Token)).Body, Is.EqualTo("anonymous"));
            Assert.That((await RequestAsync(host.Port, trusted, stop.Token)).Body, Is.EqualTo(Hash(trusted)));
            await AssertRefusedAsync(host.Port, stranger, stop.Token);
        }

        // Without a validator, normal trust applies: a self-signed client certificate is not trusted.
        [TestCase(ClientCertificateMode.Allow)]
        [TestCase(ClientCertificateMode.Require)]
        public async Task UntrustedCertificatesAreRefusedByDefault(ClientCertificateMode mode)
        {
            using var server = Create("server");
            using var client = Client("self-signed");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = mode });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await AssertRefusedAsync(host.Port, client, stop.Token);
        }

        [Test]
        public async Task TheValidatorSeesTheChainErrors()
        {
            using var server = Create("server");
            using var client = Client("client");
            var seen = new ConcurrentQueue<(string Hash, SslPolicyErrors Errors, bool Chain)>();
            var policy = new ServerTlsPolicy
            {
                ClientCertificateMode = ClientCertificateMode.Require,
                ClientCertificateValidator = (certificate, chain, errors) =>
                {
                    seen.Enqueue((Hash(certificate), errors, chain != null));
                    return true;
                },
            };
            await using var host = new Host(server, policy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Assert.That((await RequestAsync(host.Port, client, stop.Token)).Body, Is.EqualTo(Hash(client)));
            Assert.That(seen.TryDequeue(out var call), Is.True);
            Assert.That(call.Hash, Is.EqualTo(Hash(client)));
            Assert.That(call.Errors & SslPolicyErrors.RemoteCertificateChainErrors, Is.EqualTo(SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.That(call.Chain, Is.True);
        }

        [Test]
        public async Task AThrowingValidatorRefusesTheHandshakeOnly()
        {
            using var server = Create("server");
            using var client = Client("client");
            var calls = 0;
            var policy = new ServerTlsPolicy
            {
                ClientCertificateMode = ClientCertificateMode.Require,
                ClientCertificateValidator = (_, _, _) => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("validator failure") : true,
            };
            await using var host = new Host(server, policy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await AssertRefusedAsync(host.Port, client, stop.Token);
            Assert.That((await RequestAsync(host.Port, client, stop.Token)).Body, Is.EqualTo(Hash(client)));
        }

        // Each connection carries its own client's certificate, under concurrent handshakes.
        [Test]
        public async Task ConcurrentClientsEachSeeTheirOwnCertificate()
        {
            using var server = Create("server");
            var clients = Enumerable.Range(0, 8).Select(index => Client("client" + index.ToString(CultureInfo.InvariantCulture))).ToArray();
            try
            {
                await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(clients) });
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var requests = Enumerable.Range(0, 64).Select(index => Task.Run(async () =>
                {
                    var client = clients[index % clients.Length];
                    var result = await RequestAsync(host.Port, client, stop.Token).ConfigureAwait(false);
                    Assert.That(result.Body, Is.EqualTo(Hash(client)));
                })).ToArray();
                await Task.WhenAll(requests);
            }
            finally
            {
                foreach (var client in clients)
                    client.Dispose();
            }
        }

        // A client that abandons the handshake leaves the server serving others.
        [Test]
        public async Task AnAbandonedHandshakeLeavesTheServerWorking()
        {
            using var server = Create("server");
            using var client = Client("client");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(client) });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            using (var abandoned = new TcpClient())
            {
                await abandoned.ConnectAsync(IPAddress.Loopback, host.Port, stop.Token);
                using var tls = new SslStream(abandoned.GetStream(), false, (_, _, _, _) => true);
                using var cancel = new CancellationTokenSource();
                var handshake = tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, cancel.Token);
                cancel.Cancel();
                try { await handshake; }
                catch (Exception error) when (error is OperationCanceledException or IOException or AuthenticationException)
                {
                    // Canceled before or during the handshake.
                }
            }

            Assert.That((await RequestAsync(host.Port, client, stop.Token)).Body, Is.EqualTo(Hash(client)));
        }

        [Test]
        public async Task EnabledProtocolsLimitTheNegotiatedVersion()
        {
            using var server = Create("server");
            await using var host = new Host(server, new ServerTlsPolicy { EnabledProtocols = SslProtocols.Tls12 });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await RequestAsync(host.Port, null, stop.Token, protocols: SslProtocols.Tls12 | SslProtocols.Tls13);
            Assert.That(result.Protocol, Is.EqualTo(SslProtocols.Tls12));
        }

        // HTTP/2 over TLS: every stream of the connection sees the connection's client certificate.
        [Test]
        public async Task Http2RequestsCarryTheClientCertificate()
        {
            using var server = Create("server");
            using var client = Client("client");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(client) });
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var http = CreateHttpClient(client);
            for (var i = 0; i < 3; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, host.Url) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await http.SendAsync(request, stop.Token);
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo(Hash(client)));
            }
        }

        private static HttpClient CreateHttpClient(X509Certificate2? client, ConcurrentQueue<string>? servers = null)
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
                        servers?.Enqueue(Hash(peer));
                        return true;
                    },
                    ClientCertificates = client == null ? null : new X509CertificateCollection { client },
                    LocalCertificateSelectionCallback = client is { } chosen ? (_, _, _, _, _) => chosen : null,
                },
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }

        // SNI ----------------------------------------------------------------------------------------------------

        private static async Task<string> HandshakeAsync(int port, string targetHost, CancellationToken token)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            string? seen = null;
            using var tls = new SslStream(tcp.GetStream(), false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                RemoteCertificateValidationCallback = (_, peer, _, _) =>
                {
                    seen = peer == null ? null : Hash(peer);
                    return peer != null;
                },
            }, token).ConfigureAwait(false);
            return seen ?? throw new AssertionException("No server certificate.");
        }

        [Test]
        public async Task TheSelectorChoosesTheCertificateByServerName()
        {
            using var fallback = Create("fallback");
            using var alpha = Create("alpha.test");
            using var beta = Create("beta.test");
            var names = new ConcurrentQueue<string?>();
            var policy = new ServerTlsPolicy
            {
                ServerCertificateSelector = name =>
                {
                    names.Enqueue(name);
                    return name switch
                    {
                        "alpha.test" => alpha,
                        "beta.test" => beta,
                        _ => null,
                    };
                },
            };
            await using var host = new Host(fallback, policy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            Assert.That(await HandshakeAsync(host.Port, "alpha.test", stop.Token), Is.EqualTo(Hash(alpha)));
            Assert.That(await HandshakeAsync(host.Port, "beta.test", stop.Token), Is.EqualTo(Hash(beta)));
            Assert.That(await HandshakeAsync(host.Port, "other.test", stop.Token), Is.EqualTo(Hash(fallback)));
            // A client connecting by IP address sends no server name.
            Assert.That(await HandshakeAsync(host.Port, "127.0.0.1", stop.Token), Is.EqualTo(Hash(fallback)));
            Assert.That(names, Does.Contain(null));
            Assert.That(names, Does.Contain("alpha.test"));
        }

        [Test]
        public async Task AThrowingSelectorRefusesTheHandshakeOnly()
        {
            using var fallback = Create("fallback");
            var policy = new ServerTlsPolicy
            {
                ServerCertificateSelector = name => name == "broken.test" ? throw new InvalidOperationException("selector failure") : null,
            };
            await using var host = new Host(fallback, policy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            try
            {
                _ = await HandshakeAsync(host.Port, "broken.test", stop.Token);
                Assert.Fail("The handshake succeeded.");
            }
            catch (Exception error) when (error is AuthenticationException or IOException)
            {
                // Refused.
            }

            Assert.That(await HandshakeAsync(host.Port, "fine.test", stop.Token), Is.EqualTo(Hash(fallback)));
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

        private static async Task<string> GetAsync(HttpClient client, string url, Version version, CancellationToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = version, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            Assert.That(response.Version, Is.EqualTo(version));
            return await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }

        [Test]
        public async Task Http3RequiresAndCarriesClientCertificates()
        {
            RequireHttp3();
            using var server = Create("server");
            using var client = Client("client");
            await using var host = new Host(server, new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(client) },
                HttpListenerMode.EmbedIOHttp3, LocalhostUrl());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            using (var http = CreateHttpClient(client))
                Assert.That(await GetAsync(http, host.Url, HttpVersion.Version30, stop.Token), Is.EqualTo(Hash(client)));

            using (var anonymous = CreateHttpClient(null))
            {
                await Assert.ThatAsync(async () => await GetAsync(anonymous, host.Url, HttpVersion.Version30, stop.Token),
                    Throws.InstanceOf<HttpRequestException>());
            }
        }

        // Combined hosting applies one policy to TCP and QUIC.
        [Test]
        public async Task CombinedHostingAppliesThePolicyToEveryProtocol()
        {
            RequireHttp3();
            using var fallback = Create("fallback");
            using var named = Create("localhost");
            using var client = Client("client");
            var policy = new ServerTlsPolicy
            {
                ClientCertificateMode = ClientCertificateMode.Require,
                ClientCertificateValidator = Pin(client),
                ServerCertificateSelector = name => name == "localhost" ? named : null,
            };
            await using var host = new Host(fallback, policy, HttpListenerMode.EmbedIOCombined, LocalhostUrl());
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 })
            {
                var servers = new ConcurrentQueue<string>();
                using var http = CreateHttpClient(client, servers);
                Assert.That(await GetAsync(http, host.Url, version, stop.Token), Is.EqualTo(Hash(client)), version.ToString());
                Assert.That(servers, Is.EqualTo(new[] { Hash(named) }), version.ToString());
            }
        }

        [Test]
        public void Http3RefusesProtocolsWithoutTls13()
        {
            RequireHttp3();
            using var server = Create("server");
            var policy = new ServerTlsPolicy { EnabledProtocols = SslProtocols.Tls12 };
            Assert.Throws<NotSupportedException>(() =>
            {
                using var host = new WebServer(options => options.WithUrlPrefix(LocalhostUrl()).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(server).WithTlsPolicy(policy));
            });
        }

        // Rotation and policy together: a published certificate keeps the client-certificate requirement.
        [Test]
        public async Task RotationKeepsThePolicy()
        {
            using var first = Create("first");
            using var second = Create("second");
            using var client = Client("client");
            var source = new ServerCertificateSource(first);
            var url = HttpsSmoke.GetUrl();
            var port = new Uri(url).Port;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithCertificateSource(source)
                .WithTlsPolicy(new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require, ClientCertificateValidator = Pin(client) }));
            server.WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                context.SendStringAsync(Hash(context.Request.GetClientCertificate() ?? throw new InvalidOperationException("No client certificate.")),
                    "text/plain", WebServer.Utf8NoBomEncoding)));
            var running = server.RunAsync(stop.Token);
            try
            {
                source.Publish(second);
                var result = await RequestAsync(port, client, stop.Token);
                Assert.That(result.Server, Is.EqualTo(Hash(second)));
                Assert.That(result.Body, Is.EqualTo(Hash(client)));
                await AssertRefusedAsync(port, null, stop.Token);
            }
            finally
            {
                await stop.CancelAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }
}
