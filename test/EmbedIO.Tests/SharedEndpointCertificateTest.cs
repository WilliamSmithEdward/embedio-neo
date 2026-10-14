using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Net;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // An HTTPS endpoint presents one server certificate to every connection, so it is
    // shared only by listeners that present the same certificate, and a secure prefix
    // without any certificate fails to start instead of opening a port whose every
    // connection is dropped at the handshake.
    [NonParallelizable]
    public sealed class SharedEndpointCertificateTest
    {
        [Test]
        public void DifferentFixedCertificatesCannotShareAnEndpoint()
        {
            using var first = HttpsSmoke.CreateCertificate();
            using var second = HttpsSmoke.CreateCertificate();
            Assert.That(second.GetCertHashString(), Is.Not.EqualTo(first.GetCertHashString()));
            var endpoint = HttpsSmoke.GetUrl();
            using var opener = new EmbedIO.Net.HttpListener(first);
            opener.AddPrefix(endpoint + "first/");
            opener.Start();
            using var joiner = new EmbedIO.Net.HttpListener(second);
            joiner.AddPrefix(endpoint + "second/");
            var error = Assert.Throws<System.Net.HttpListenerException>(() => joiner.Start());
            Assert.That(error?.Message, Does.Contain("different server certificates"));
            Assert.That(joiner.IsListening, Is.False);
            Assert.That(opener.IsListening, Is.True);
            // The refusal published nothing: once the opener leaves, the joiner can take the port alone.
            opener.Stop();
            joiner.Start();
            Assert.That(joiner.IsListening, Is.True);
            joiner.Stop();
        }

        [Test]
        public void TheSameCertificateLoadedTwiceCanShareAnEndpoint()
        {
            using var original = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var copy = X509CertificateLoader.LoadPkcs12(original.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            Assert.That(ReferenceEquals(original, copy), Is.False);
            Assert.That(copy.GetCertHashString(), Is.EqualTo(original.GetCertHashString()));
            var endpoint = HttpsSmoke.GetUrl();
            using var opener = new EmbedIO.Net.HttpListener(original);
            opener.AddPrefix(endpoint + "first/");
            opener.Start();
            using var joiner = new EmbedIO.Net.HttpListener(copy);
            joiner.AddPrefix(endpoint + "second/");
            joiner.Start();
            Assert.That(joiner.IsListening, Is.True);
            Assert.That(opener.IsListening, Is.True);
            joiner.Stop();
            opener.Stop();
        }

        [Test]
        public void AFixedCertificateAndASourceCannotShareAnEndpoint()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var endpoint = HttpsSmoke.GetUrl();
            using var opener = new EmbedIO.Net.HttpListener(certificate);
            opener.AddPrefix(endpoint + "first/");
            opener.Start();
            // The source holds the opener's certificate now, but it may rotate away while the endpoint is shared.
            using var joiner = EmbedIO.Net.HttpListener.CreateWithCertificateSource(new ServerCertificateSource(certificate));
            joiner.AddPrefix(endpoint + "second/");
            var error = Assert.Throws<System.Net.HttpListenerException>(() => joiner.Start());
            Assert.That(error?.Message, Does.Contain("different server certificates"));
            Assert.That(joiner.IsListening, Is.False);
            Assert.That(opener.IsListening, Is.True);
            opener.Stop();
        }

        [Test]
        public void ListenersBorrowingTheSameSourceCanShareAnEndpoint()
        {
            using var certificate = HttpsSmoke.CreateCertificate();
            var source = new ServerCertificateSource(certificate);
            var endpoint = HttpsSmoke.GetUrl();
            using var opener = EmbedIO.Net.HttpListener.CreateWithCertificateSource(source);
            opener.AddPrefix(endpoint + "first/");
            opener.Start();
            using var joiner = EmbedIO.Net.HttpListener.CreateWithCertificateSource(source);
            joiner.AddPrefix(endpoint + "second/");
            joiner.Start();
            Assert.That(joiner.IsListening, Is.True);
            Assert.That(opener.IsListening, Is.True);
            joiner.Stop();
            opener.Stop();
        }

        [Test]
        public async Task ARefusedOwnerNeverServesAndTheOpenerKeepsItsCertificate()
        {
            using var openerCertificate = HttpsSmoke.CreateCertificate();
            using var otherCertificate = HttpsSmoke.CreateCertificate();
            var endpoint = HttpsSmoke.GetUrl();
            var openerCalls = 0;
            var otherCalls = 0;
            using var opener = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO)
                .WithCertificate(openerCertificate).WithUrlPrefix(endpoint + "opener/"))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    Interlocked.Increment(ref openerCalls);
                    return context.SendStringAsync("opener", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var other = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO)
                .WithCertificate(otherCertificate).WithUrlPrefix(endpoint + "other/"))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    Interlocked.Increment(ref otherCalls);
                    return context.SendStringAsync("other", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Task? openerRun = null;
            Task? otherRun = null;
            Exception? primary = null;
            try
            {
                openerRun = Start(opener, stop.Token, out var openerListening);
                await Ready(opener, openerRun, openerListening, stop.Token);
                otherRun = Start(other, stop.Token, out var otherListening);
                var refusal = await Assert.ThrowsAsync<System.Net.HttpListenerException>(
                    () => Ready(other, otherRun, otherListening, stop.Token));
                Assert.That(refusal?.Message, Does.Contain("different server certificates"));
                Assert.That(other.State, Is.Not.EqualTo(WebServerState.Listening));
                var served = await Observe(endpoint + "opener/", openerCertificate, stop.Token);
                Assert.That(served.Status, Is.EqualTo(HttpStatusCode.OK), served.Body);
                Assert.That(served.Body, Is.EqualTo("opener"));
                // The endpoint presents the opener's certificate; a client that trusts only the
                // refused owner's certificate never completes a handshake and never reaches a handler.
                var pinnedToOther = await Observe(endpoint + "other/", otherCertificate, stop.Token);
                Assert.That(pinnedToOther.Status, Is.Null, pinnedToOther.Body);
                Assert.That(pinnedToOther.Error, Is.Not.Null);
                Assert.That(Volatile.Read(ref otherCalls), Is.Zero);
                Assert.That(Volatile.Read(ref openerCalls), Is.EqualTo(1));
            }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                var cleanup = new List<Exception>();
                try { stop.Cancel(); }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { cleanup.Add(error); }
                try
                {
                    if (openerRun != null) await openerRun.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { cleanup.Add(error); }
                if (primary == null && cleanup.Count != 0) ExceptionDispatchInfo.Capture(cleanup[0]).Throw();
                if (primary == null) Assert.That(otherRun == null || otherRun.IsCompleted, Is.True);
            }
        }

        [Test]
        public void ASecurePrefixWithoutACertificateFailsToStart()
        {
            var endpoint = HttpsSmoke.GetUrl();
            using var listener = new EmbedIO.Net.HttpListener();
            listener.AddPrefix(endpoint);
            var error = Assert.Throws<InvalidOperationException>(() => listener.Start());
            Assert.That(error?.Message, Does.Contain("server certificate"));
            Assert.That(listener.IsListening, Is.False);
            // Nothing was published, so a listener that has a certificate takes the port.
            using var certificate = HttpsSmoke.CreateCertificate();
            using var replacement = new EmbedIO.Net.HttpListener(certificate);
            replacement.AddPrefix(endpoint);
            replacement.Start();
            Assert.That(replacement.IsListening, Is.True);
            replacement.Stop();
        }

        [Test]
        public async Task AServerWithASecurePrefixAndNoCertificateFailsToStart()
        {
            var endpoint = HttpsSmoke.GetUrl();
            using var server = new WebServer(options => options.WithMode(HttpListenerMode.EmbedIO).WithUrlPrefix(endpoint))
                .WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                    context.SendStringAsync("never", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = Start(server, stop.Token, out var listening);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Ready(server, running, listening, stop.Token));
            Assert.That(error?.Message, Does.Contain("server certificate"));
            Assert.That(server.State, Is.Not.EqualTo(WebServerState.Listening));
            Assert.That(running.IsCompleted, Is.True);
        }

        private static async Task<(HttpStatusCode? Status, string Body, Exception? Error)> Observe(string url, X509Certificate2 trusted, CancellationToken token)
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                SslOptions = new SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                        && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                        && peer.GetCertHashString() == trusted.GetCertHashString(),
                },
            };
            using var client = new HttpClient(handler)
            {
                DefaultRequestVersion = HttpVersion.Version11,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(10),
            };
            try
            {
                using var response = await client.GetAsync(url, token);
                return (response.StatusCode, await response.Content.ReadAsStringAsync(token), null);
            }
            catch (HttpRequestException error)
            {
                return (null, error.ToString(), error);
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
