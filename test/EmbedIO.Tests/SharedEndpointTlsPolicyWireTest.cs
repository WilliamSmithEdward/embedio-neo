using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Net;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public sealed class SharedEndpointTlsPolicyWireTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task IncompatibleOwnerIsRejectedBeforePublishingRoutesRegardlessOfEndpointOpener(bool requiredOpensEndpoint)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var endpoint = HttpsSmoke.GetUrl();
            using var clientCertificate = CreateClientCertificate();
            var defaultCalls = 0;
            var requiredCalls = 0;
            using var defaultOwner = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO)
                .WithCertificate(certificate).WithUrlPrefix(endpoint + "anonymous/"))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    Interlocked.Increment(ref defaultCalls);
                    Assert.That(context.Request.LocalEndPoint.Port, Is.EqualTo(new Uri(endpoint).Port));
                    Assert.That(context.Request.GetClientCertificate(), Is.Null);
                    return context.SendStringAsync("anonymous-owner", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var requiredOwner = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO)
                .WithCertificate(certificate).WithUrlPrefix(endpoint + "require/")
                .WithTlsPolicy(new ServerTlsPolicy
                {
                    ClientCertificateMode = ClientCertificateMode.Require,
                    ClientCertificateValidator = (peer, _, _) => peer.GetCertHashString() == clientCertificate.GetCertHashString(),
                }))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    Interlocked.Increment(ref requiredCalls);
                    Assert.That(context.Request.LocalEndPoint.Port, Is.EqualTo(new Uri(endpoint).Port));
                    return context.SendStringAsync(context.Request.GetClientCertificate() == null
                        ? "required-owner-anonymous" : "required-owner-certificate", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Task? firstRun = null;
            Task? secondRun = null;
            Exception? primaryFailure = null;
            var secondRejectionObserved = false;
            try
            {
                var first = requiredOpensEndpoint ? requiredOwner : defaultOwner;
                var second = requiredOpensEndpoint ? defaultOwner : requiredOwner;
                firstRun = Start(first, stop.Token, out var firstListening);
                await Ready(first, firstRun, firstListening, stop.Token);
                secondRun = Start(second, stop.Token, out var secondListening);
                var refusal = await Assert.ThrowsAsync<System.Net.HttpListenerException>(
                    () => Ready(second, secondRun, secondListening, stop.Token));
                secondRejectionObserved = refusal != null;
                Assert.That(refusal?.Message, Does.Contain("incompatible TLS policies"));
                Assert.That(second.State, Is.Not.EqualTo(WebServerState.Listening));
                // Fresh clients send no client certificate. They offer only TLS1.2/1.3.
                async Task<(HttpStatusCode? Status, string Body, HttpRequestError? Error)> Observe(string path, bool sendCertificate = false)
                {
                    using var handler = new SocketsHttpHandler
                    {
                        UseProxy = false,
                        SslOptions = new SslClientAuthenticationOptions
                        {
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                                && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                                && peer.GetCertHashString() == certificate.GetCertHashString(),
                        },
                    };
                    if (sendCertificate) handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
                    using var client = new HttpClient(handler)
                    {
                        DefaultRequestVersion = HttpVersion.Version11,
                        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                        Timeout = TimeSpan.FromSeconds(10),
                    };
                    try
                    {
                        using var response = await client.GetAsync(endpoint + path, stop.Token);
                        return (response.StatusCode, await response.Content.ReadAsStringAsync(stop.Token), null);
                    }
                    catch (HttpRequestException error)
                    {
                        return (null, error.ToString(), error.HttpRequestError);
                    }
                }
                var required = requiredOpensEndpoint ? await Observe("require/") : (Status: (HttpStatusCode?)null, Body: "not requested", Error: (HttpRequestError?)null);
                var ordinary = requiredOpensEndpoint ? (Status: (HttpStatusCode?)null, Body: "not requested", Error: (HttpRequestError?)null) : await Observe("anonymous/");
                TestContext.Out.WriteLine($"openerRequired={requiredOpensEndpoint}; required={required}; ordinary={ordinary}; requiredCalls={requiredCalls}; ordinaryCalls={defaultCalls}");
                Assert.That(Volatile.Read(ref requiredCalls), Is.Zero,
                    "A no-certificate client must never reach the logical owner configured to Require a client certificate.");
                Assert.That(required.Status, Is.Null);

                if (!requiredOpensEndpoint)
                {
                    Assert.That(ordinary.Status, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(ordinary.Body, Is.EqualTo("anonymous-owner"));
                    Assert.That(Volatile.Read(ref defaultCalls), Is.EqualTo(1));
                }
                else
                {
                    // The original Require owner still rejects an anonymous handshake;
                    // the incompatible second owner never became a route.
                    Assert.That(ordinary.Status, Is.Null);
                    Assert.That(required.Error, Is.Not.Null);
                    Assert.That(Volatile.Read(ref defaultCalls), Is.Zero);
                    var healthy = await Observe("require/", true);
                    Assert.That(healthy.Status, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(healthy.Body, Is.EqualTo("required-owner-certificate"));
                    Assert.That(Volatile.Read(ref requiredCalls), Is.EqualTo(1));
                }
            }
            catch (Exception error)
            {
                primaryFailure = error;
                throw;
            }
            finally
            {
                var cleanupFailures = new List<Exception>();
                try { stop.Cancel(); }
                catch (Exception cleanupError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(cleanupError)) { cleanupFailures.Add(cleanupError); }
                try
                {
                    if (firstRun != null) await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception cleanupError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(cleanupError)) { cleanupFailures.Add(cleanupError); }
                try
                {
                    // Ready already awaited an expected refused startup. On every
                    // other path, join the second task even when the main assertion failed.
                    if (secondRun != null && (!secondRejectionObserved || !secondRun.IsCompleted))
                        await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception cleanupError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(cleanupError)) { cleanupFailures.Add(cleanupError); }
                var joined = (firstRun == null || firstRun.IsCompleted) && (secondRun == null || secondRun.IsCompleted);
                try
                {
                    TestContext.Out.WriteLine($"terminalJoined={joined}; first={firstRun?.Status}; second={secondRun?.Status}; cleanupFailures={cleanupFailures.Count}");
                    foreach (var cleanupError in cleanupFailures) TestContext.Error.WriteLine(cleanupError.ToString());
                }
                catch (Exception diagnosticError) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(diagnosticError))
                {
                    // An existing primary remains primary for recoverable cleanup-reporting errors.
                    cleanupFailures.Add(diagnosticError);
                }
                if (primaryFailure == null)
                {
                    if (cleanupFailures.Count != 0) ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
                    Assert.That(joined, Is.True, "Both actual server Tasks must be terminal, not merely canceled.");
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompatibleDefaultsKeepBothOwnersHealthy(bool explicitDefaultFirst)
        {
            await ExerciseCompatiblePair(explicitDefaultFirst ? new ServerTlsPolicy() : null,
                explicitDefaultFirst ? null : new ServerTlsPolicy(), null);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task EffectiveDefaultProtocolsAcceptAnEquivalentExplicitMask(bool nullDefault, bool explicitFirst)
        {
            var protocols = SslProtocols.Tls12;
            if (Enum.TryParse<SslProtocols>("Tls13", out var tls13)
                && Enum.IsDefined(typeof(SslProtocols), tls13))
                protocols |= tls13;
            var explicitPolicy = new ServerTlsPolicy { EnabledProtocols = protocols };
            var defaultPolicy = nullDefault ? null : new ServerTlsPolicy();
            await ExerciseCompatiblePair(explicitFirst ? explicitPolicy : defaultPolicy,
                explicitFirst ? defaultPolicy : explicitPolicy, null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SharedOrEquivalentRequirePolicyAcceptsThePinnedClient(bool samePolicyObject)
        {
            using var clientCertificate = CreateClientCertificate();
            Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool> validator = (peer, _, _) =>
                peer.GetCertHashString() == clientCertificate.GetCertHashString();
            var first = new ServerTlsPolicy
            {
                ClientCertificateMode = ClientCertificateMode.Require,
                ClientCertificateValidator = validator,
            };
            var second = samePolicyObject ? first : new ServerTlsPolicy
            {
                ClientCertificateMode = ClientCertificateMode.Require,
                ClientCertificateValidator = validator,
            };
            await ExerciseCompatiblePair(first, second, clientCertificate);
        }

        [Test]
        public void RejectedMultiPrefixRegistrationRollsBackOnlyItsOwnAddressesAndCanRestart()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var occupied = HttpsSmoke.GetUrl();
            var fresh = HttpsSmoke.GetUrl();
            Assert.That(new Uri(fresh).Port, Is.Not.EqualTo(new Uri(occupied).Port));
            using var first = new EmbedIO.Net.HttpListener(certificate);
            first.AddPrefix(occupied + "first/");
            first.Start();
            var certificateSource = new ServerCertificateSource(certificate);
            using var rejected = new EmbedIO.Net.HttpListener(certificateSource,
                new ServerTlsPolicy { ClientCertificateMode = ClientCertificateMode.Require });
            rejected.AddPrefix(fresh + "rollback/");
            rejected.AddPrefix(occupied + "rejected/");
            var error = Assert.Throws<System.Net.HttpListenerException>(() => rejected.Start());
            Assert.That(error?.Message, Does.Contain("incompatible TLS policies"));
            Assert.That(rejected.IsListening, Is.False);
            Assert.That(first.IsListening, Is.True);
            // A different owner can bind the rolled-back localhost generation,
            // including both loopback addresses when IPv6 is enabled.
            using (var replacement = new EmbedIO.Net.HttpListener(certificate))
            {
                replacement.AddPrefix(fresh + "rollback/");
                replacement.Start();
                Assert.That(replacement.IsListening, Is.True);
                replacement.Stop();
            }
            // Removing the failed owner never removes the original prefix.
            rejected.Stop();
            using (var compatible = new EmbedIO.Net.HttpListener(certificate))
            {
                compatible.AddPrefix(occupied + "rejected/");
                compatible.Start();
                compatible.Stop();
                Assert.That(first.IsListening, Is.True);
            }
            first.Stop();
            rejected.Start();
            Assert.That(rejected.IsListening, Is.True);
            rejected.Stop();
            rejected.Start();
            Assert.That(rejected.IsListening, Is.True);
            rejected.Stop();
        }

        [TestCase("validator")]
        [TestCase("selector")]
        [TestCase("revocation")]
        [TestCase("protocols")]
        public void DistinctHandshakeSettingsAreRefusedWithoutInvokingDelegates(string setting)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var endpoint = HttpsSmoke.GetUrl();
            var callbacks = 0;
            var firstPolicy = new ServerTlsPolicy();
            var secondPolicy = new ServerTlsPolicy();
            switch (setting)
            {
                case "validator":
                    firstPolicy.ClientCertificateValidator = (_, _, _) => { callbacks++; return true; };
                    secondPolicy.ClientCertificateValidator = (_, _, _) => { callbacks++; return true; };
                    break;
                case "selector":
                    firstPolicy.ServerCertificateSelector = _ => { callbacks++; return certificate; };
                    secondPolicy.ServerCertificateSelector = _ => { callbacks++; return certificate; };
                    break;
                case "revocation": secondPolicy.CheckCertificateRevocation = true; break;
                case "protocols": secondPolicy.EnabledProtocols = SslProtocols.Tls12; break;
                default: throw new AssertionException("Unexpected setting.");
            }
            var certificateSource = new ServerCertificateSource(certificate);
            var framework = typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            Assert.That(framework, Is.Not.Null);
            if (setting == "selector" && framework == ".NETStandard,Version=v2.0")
            {
                // This asset documents SNI selection as unsupported, so refuse
                // both policies before endpoint registration rather than ignore them.
                var firstError = Assert.Throws<PlatformNotSupportedException>(() =>
                {
                    using var rejected = new EmbedIO.Net.HttpListener(certificateSource, firstPolicy);
                });
                var secondError = Assert.Throws<PlatformNotSupportedException>(() =>
                {
                    using var rejected = new EmbedIO.Net.HttpListener(certificateSource, secondPolicy);
                });
                Assert.That(firstError?.Message, Does.Contain("requires the .NET 10 asset"));
                Assert.That(secondError?.Message, Does.Contain("requires the .NET 10 asset"));
                Assert.That(callbacks, Is.Zero, "Unsupported policy rejection must not execute TLS callbacks.");
                return;
            }
            using var first = new EmbedIO.Net.HttpListener(certificateSource, firstPolicy);
            using var second = new EmbedIO.Net.HttpListener(certificateSource, secondPolicy);
            first.AddPrefix(endpoint + "first/");
            second.AddPrefix(endpoint + "second/");
            first.Start();
            Assert.Throws<System.Net.HttpListenerException>(() => second.Start());
            Assert.That(callbacks, Is.Zero, "Registration comparison must not execute TLS callbacks under the registry gate.");
            Assert.That(first.IsListening, Is.True);
            Assert.That(second.IsListening, Is.False);
        }

        private static X509Certificate2 CreateClientCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=shared-policy-client", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") }, false));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        }

        private static async Task ExerciseCompatiblePair(ServerTlsPolicy? firstPolicy, ServerTlsPolicy? secondPolicy,
            X509Certificate2? clientCertificate)
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var endpoint = HttpsSmoke.GetUrl();
            var calls = new int[2];
            WebServer Make(int owner, ServerTlsPolicy? policy) => new WebServer(options => options
                .WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate)
                .WithUrlPrefix(endpoint + "owner" + owner + "/").WithTlsPolicy(policy))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    Interlocked.Increment(ref calls[owner]);
                    Assert.That(context.Request.LocalEndPoint.Port, Is.EqualTo(new Uri(endpoint).Port));
                    if (clientCertificate == null) Assert.That(context.Request.GetClientCertificate(), Is.Null);
                    else Assert.That(context.Request.GetClientCertificate()?.GetCertHashString(), Is.EqualTo(clientCertificate.GetCertHashString()));
                    return context.SendStringAsync("owner" + owner, "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var first = Make(0, firstPolicy);
            using var second = Make(1, secondPolicy);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Task? firstRun = null;
            Task? secondRun = null;
            Exception? primary = null;
            try
            {
                firstRun = Start(first, stop.Token, out var firstListening);
                await Ready(first, firstRun, firstListening, stop.Token);
                secondRun = Start(second, stop.Token, out var secondListening);
                await Ready(second, secondRun, secondListening, stop.Token);
                using var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                            && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                            && peer.GetCertHashString() == certificate.GetCertHashString(),
                    },
                };
                if (clientCertificate != null) handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                foreach (var owner in new[] { 0, 1 })
                {
                    using var response = await client.GetAsync(endpoint + "owner" + owner + "/", stop.Token);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await response.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("owner" + owner));
                }
                // Remove only the second logical owner, then prove the first still serves.
                second.Dispose();
                await secondRun.WaitAsync(TimeSpan.FromSeconds(5));
                using var healthy = await client.GetAsync(endpoint + "owner0/", stop.Token);
                Assert.That(healthy.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await healthy.Content.ReadAsStringAsync(stop.Token), Is.EqualTo("owner0"));
                Assert.That(calls, Is.EqualTo(new[] { 2, 1 }));
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                var cleanup = new List<Exception>();
                try { stop.Cancel(); }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { cleanup.Add(error); }
                try
                {
                    if (firstRun != null && secondRun != null) await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5));
                    else if (firstRun != null) await firstRun.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { cleanup.Add(error); }
                if (primary == null && cleanup.Count != 0) ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
                if (primary == null) Assert.That((firstRun == null || firstRun.IsCompleted) && (secondRun == null || secondRun.IsCompleted), Is.True);
            }
        }

        private static Task Start(WebServer server, CancellationToken token, out Task listeningTask)
        {
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, args) =>
            {
                if (args.NewState == WebServerState.Listening) listening.TrySetResult();
            };
            listeningTask = listening.Task;
            return server.RunAsync(token);
        }

        private static async Task Ready(WebServer server, Task running, Task listening, CancellationToken token)
        {
            await Task.WhenAny(listening, running).WaitAsync(token);
            if (running.IsCompleted) await running;
            Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
        }
    }
}
