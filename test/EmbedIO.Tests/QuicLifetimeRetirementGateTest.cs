using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Retirement gate for the QUIC endpoint-release retry and the raw-runtime rebind
    // quarantine tracked by issue #202. Every case restarts EmbedIO HTTP/3 servers on
    // the endpoint they have just released and requires that the production retry in
    // QuicEndpointReleases never had to bridge an AddressAlreadyInUse failure. On a
    // runtime whose MsQuic still keeps the UDP port bound after disposal returns, the
    // retry hides that from ordinary tests; this gate fails instead. It passes only
    // when the shipped dependency no longer needs the workaround.
    //
    // The gate is opt-in: every case is skipped unless EMBEDIO_QUIC_RETIREMENT_GATE=1.
    // Select it together with the raw runtime cases, which have no retry, by
    //   --filter "TestCategory=QuicLifetimeRetirementGate|FullyQualifiedName~QuicRuntimeRebindTest"
    // CI runs that selection through the quic-retirement-gate dispatch input without
    // any quarantine. EMBEDIO_QUIC_RETIREMENT_GATE_OUTPUT names a directory for the
    // JSON ledger. See docs/project/quic-lifetime.md.
    [NonParallelizable]
    public sealed class QuicLifetimeRetirementGateTest
    {
        private const string GateCategory = "QuicLifetimeRetirementGate";
        private static readonly Type? ReleasesType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QuicEndpointReleases");
        private static readonly object LedgerSync = new();

        // Darwin listener-only ordering: an idle listener is disposed and its endpoint
        // is bound again at once, 64 times, then the endpoint must serve a request.
        [Test, Category(GateCategory)]
        public Task IdleListenerRestartsWithoutRetry() => Gate("idle-listener", async (certificate, token) =>
        {
            var prefix = Prefixes(1)[0];
            using var server = Server(certificate, prefix, "idle");
            var cycles = 0;
            for (; cycles < 64; ++cycles)
            {
                token.ThrowIfCancellationRequested();
                server.Listener.Start();
                Assert.That(server.Listener.IsListening, Is.True);
                server.Listener.Stop();
            }
            await ServeOnceAsync(server, certificate, prefix, "idle", false, token).ConfigureAwait(false);
            return cycles;
        });

        // Accepted-connection binding lifetime on every OS: each cycle serves a request,
        // stops, and starts a new server on the same endpoint at once. Odd cycles keep
        // the client connection open until the server has stopped, so the server ends
        // the accepted connection itself.
        [Test, Category(GateCategory)]
        public Task ServedListenerRestartsWithoutRetry() => Gate("served-listener", async (certificate, token) =>
        {
            var prefix = Prefixes(1)[0];
            var cycles = 0;
            for (; cycles < 32; ++cycles)
            {
                token.ThrowIfCancellationRequested();
                using var server = Server(certificate, prefix, "served");
                await ServeOnceAsync(server, certificate, prefix, "served", cycles % 2 == 1, token).ConfigureAwait(false);
            }
            return cycles;
        });

        // Graceful drain: after a served request, DrainAsync and RunAsync complete and a
        // new server binds the drained endpoint immediately.
        [Test, Category(GateCategory)]
        public Task DrainedListenerRestartsWithoutRetry() => Gate("drained-listener", async (certificate, token) =>
        {
            var prefix = Prefixes(1)[0];
            var cycles = 0;
            for (; cycles < 16; ++cycles)
            {
                token.ThrowIfCancellationRequested();
                using var server = Server(certificate, prefix, "drained");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
                var running = server.RunAsync(stop.Token);
                try
                {
                    using (var client = Client(certificate))
                        Assert.That(await client.GetStringAsync(prefix, stop.Token).ConfigureAwait(false), Is.EqualTo("drained"));
                    await server.DrainAsync(TimeSpan.FromSeconds(15), stop.Token).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None).ConfigureAwait(false);
                    await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                    Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                }
                finally
                {
                    stop.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                }
            }
            return cycles;
        });

        // Contention: four servers on distinct endpoints restart concurrently while each
        // serves a request per cycle, so one endpoint's native cleanup overlaps the
        // binds, handshakes and closes of the others.
        [Test, Category(GateCategory)]
        public Task ContendedRestartsCompleteWithoutRetry() => Gate("contended-restarts", async (certificate, token) =>
        {
            var prefixes = Prefixes(4);
            var lanes = await Task.WhenAll(prefixes.Select((prefix, lane) => Task.Run(async () =>
            {
                var body = "lane" + lane.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var cycles = 0;
                for (; cycles < 16; ++cycles)
                {
                    token.ThrowIfCancellationRequested();
                    using var server = Server(certificate, prefix, body);
                    await ServeOnceAsync(server, certificate, prefix, body, cycles % 2 == 1, token).ConfigureAwait(false);
                }
                return cycles;
            }, token))).ConfigureAwait(false);
            return lanes.Sum();
        });

        private static async Task Gate(string name, Func<X509Certificate2, CancellationToken, Task<int>> body)
        {
            if (Environment.GetEnvironmentVariable("EMBEDIO_QUIC_RETIREMENT_GATE") != "1")
            {
                Assert.Ignore("QUIC lifetime retirement gate; select it with EMBEDIO_QUIC_RETIREMENT_GATE=1.");
                return;
            }
            var supported = QuicListener.IsSupported && QuicConnection.IsSupported && ReleasesType != null;
            Assert.That(supported, Is.True, "The retirement gate requires QUIC support in the host and in the selected asset.");
            QuicDependencyEvidence.VerifyIfRequested();
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var before = Retries;
            var watch = Stopwatch.StartNew();
            int cycles;
            try
            {
                cycles = await body(certificate, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Record(name, 0, before, Retries, "failed", watch.Elapsed, error.GetType().Name + ": " + error.Message);
                throw;
            }
            var bridged = Retries - before;
            Record(name, cycles, before, before + bridged, bridged == 0 ? "passed" : "retried", watch.Elapsed, null);
            Assert.That(bridged, Is.Zero,
                $"The endpoint-release retry bridged {bridged} AddressAlreadyInUse bind failure(s) during {cycles} {name} cycles; the runtime still needs the workaround.");
        }

        private static long Retries
        {
            get
            {
                var type = ReleasesType ?? throw new AssertionException("The selected asset has no HTTP/3 transport.");
                var shared = type.GetProperty("Shared", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                    ?? throw new AssertionException("Missing shared release policy.");
                var retries = type.GetProperty("Retries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(shared)
                    ?? throw new AssertionException("Missing retry counter.");
                return (long)retries;
            }
        }

        private static void Record(string gate, int cycles, long before, long after, string outcome, TimeSpan elapsed, string? error)
        {
            var runtime = RuntimeInformation.FrameworkDescription;
            TestContext.Out.WriteLine($"EmbedIO HTTP/3 retirement gate: gate={gate}, cycles={cycles}, bridged={after - before}, outcome={outcome}, elapsed={elapsed.TotalMilliseconds:F0}ms, runtime={runtime}, os={RuntimeInformation.OSDescription}");
            var directory = Environment.GetEnvironmentVariable("EMBEDIO_QUIC_RETIREMENT_GATE_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            var line = JsonSerializer.Serialize(new
            {
                gate,
                cycles,
                retriesBefore = before,
                retriesAfter = after,
                bridged = after - before,
                outcome,
                error,
                elapsedMilliseconds = (long)elapsed.TotalMilliseconds,
                runtime,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processId = Environment.ProcessId,
                source = Environment.GetEnvironmentVariable("EMBEDIO_SOURCE_SHA"),
                timestampUtc = DateTime.UtcNow.ToString("O"),
            });
            lock (LedgerSync)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "gate-ledger.jsonl"), line + "\n");
            }
        }

        private static WebServer Server(X509Certificate2 certificate, string prefix, string body)
            => new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithAction("/", HttpVerbs.Get, context => context.SendStringAsync(body, "text/plain", WebServer.Utf8NoBomEncoding));

        // Run the server, serve one request, then stop it. With keepClientOpen the
        // client connection stays open until the server has stopped.
        private static async Task ServeOnceAsync(WebServer server, X509Certificate2 certificate, string prefix, string expected, bool keepClientOpen, CancellationToken token)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            try
            {
                Assert.That(await client.GetStringAsync(prefix, stop.Token).ConfigureAwait(false), Is.EqualTo(expected));
                if (!keepClientOpen) client.Dispose();
            }
            finally
            {
                // A failed start faults the run task; its error replaces the request's.
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Distinct loopback ports, held together while chosen so lanes cannot collide.
        private static string[] Prefixes(int count)
        {
            var sockets = new List<Socket>(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    sockets.Add(socket);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                }
                return sockets.Select(socket => $"https://localhost:{((IPEndPoint)(socket.LocalEndPoint ?? throw new AssertionException("Missing endpoint."))).Port}/").ToArray();
            }
            finally
            {
                foreach (var socket in sockets) socket.Dispose();
            }
        }

        private static HttpClient Client(X509Certificate2 certificate) => new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = new SslClientAuthenticationOptions
            { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() },
        })
        {
            DefaultRequestVersion = HttpVersion.Version30,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(15),
        };
    }
}
