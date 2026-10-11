using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class TcpDrainReplacementTest
    {
        private const int Rounds = 16;
        private const int Width = 16;

        [TestCase("127.0.0.1", false)]
        [TestCase("localhost", false)]
        [TestCase("localhost", true)]
        [TestCase("[::1]", true)]
        [TestCase("*", false)]
        [TestCase("*", true)]
        [TestCase("+", false)]
        [TestCase("+", true)]
        public async Task ConnectChurnAcrossDrainAndReplacementHasNoUnansweredAttempts(string host, bool ipv6)
        {
            if (ipv6 && !Socket.OSSupportsIPv6) Assert.Ignore("IPv6 is unavailable on this host.");
            var port = new Uri(Resources.GetServerAddress()).Port;
            var prefix = $"http://{host}:{port}/";
            var authority = host is "*" or "+" ? "localhost" : host;
            var target = new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            for (var generation = 0; generation < Rounds; generation++)
            {
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var expected = $"generation-{generation}";
                using var retiring = Server(prefix, expected, entered, release);
                var running = retiring.RunAsync(deadline.Token);
                object[] endpoints = Array.Empty<object>();
                try
                {
                    // An accepted, incomplete response keeps graceful drain active.
                    var held = Exchange(target, expected, "/held", generation, "accepted-response", deadline.Token, authority);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                    endpoints = Endpoints(retiring.Listener);
                    Record(generation, "before-drain", endpoints);
                    var churn = Enumerable.Range(0, Width).Select(index => Connect(target, generation, index, deadline.Token)).ToArray();
                    var draining = retiring.DrainAsync(TimeSpan.FromSeconds(5), deadline.Token);
                    Record(generation, "drain-called", endpoints);
                    await Task.WhenAll(churn);
                    release.TrySetResult();
                    await held;
                    await draining.WaitAsync(deadline.Token);
                    await running.WaitAsync(deadline.Token);
                    Record(generation, "drain-completed", endpoints);
                    await AssertRetired(endpoints);
                    // Deliberately retain the old server object while its successor serves.
                    using var replacement = Server(prefix, $"replacement-{generation}");
                    var replacing = replacement.RunAsync(deadline.Token);
                    try
                    {
                        var current = Endpoints(replacement.Listener);
                        Assert.That(current.Intersect(endpoints), Is.Empty, "A stopped endpoint cannot own the replacement.");
                        Record(generation, "replacement-started", current);
                        await Task.WhenAll(Enumerable.Range(0, Width).Select(index =>
                            Exchange(target, $"replacement-{generation}", "/", generation, $"replacement-{index}", deadline.Token, authority)));
                        retiring.Dispose();
                        await Exchange(target, $"replacement-{generation}", "/", generation, "after-old-dispose", deadline.Token, authority);
                        Record(generation, "replacement-healthy", current);
                        await replacement.DrainAsync(TimeSpan.FromSeconds(5), deadline.Token);
                        await replacing.WaitAsync(deadline.Token);
                        await AssertRetired(current);
                    }
                    finally { replacement.Dispose(); await replacing.WaitAsync(TimeSpan.FromSeconds(5)); }
                }
                finally
                {
                    release.TrySetResult();
                    retiring.Dispose();
                    await running.WaitAsync(TimeSpan.FromSeconds(5));
                    Record(generation, "retiring-cleanup", endpoints);
                }
            }
        }

        [Test]
        public async Task ReplacementAcceptsFreshRequestAfterFortySecondsWithoutTraffic()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var prefix = $"http://127.0.0.1:{port}/";
            var target = new IPEndPoint(IPAddress.Loopback, new Uri(prefix).Port);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            using var retired = Server(prefix, "old");
            var oldRun = retired.RunAsync(deadline.Token);
            await Exchange(target, "old", "/", 0, "before-drain", deadline.Token);
            var oldEndpoints = Endpoints(retired.Listener);
            await retired.DrainAsync(TimeSpan.FromSeconds(5), deadline.Token);
            await oldRun.WaitAsync(deadline.Token);
            await AssertRetired(oldEndpoints);
            using var replacement = Server(prefix, "new");
            var running = replacement.RunAsync(deadline.Token);
            try
            {
                await Exchange(target, "new", "/", 1, "before-quiet", deadline.Token);
                var endpoints = Endpoints(replacement.Listener);
                Record(1, "quiet-start", endpoints);
                await Task.Delay(TimeSpan.FromSeconds(40), deadline.Token);
                Record(1, "quiet-end", endpoints);
                await Exchange(target, "new", "/", 1, "after-quiet", deadline.Token);
            }
            finally { replacement.Dispose(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static WebServer Server(string prefix, string body, TaskCompletionSource? entered = null, TaskCompletionSource? release = null)
            => new WebServer(HttpListenerMode.EmbedIO, prefix).WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/held" && entered != null && release != null)
                { entered.TrySetResult(); await release.Task.WaitAsync(context.CancellationToken); }
                context.Response.ContentLength64 = Encoding.ASCII.GetByteCount(body);
                await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes(body), context.CancellationToken);
            }));

        private static async Task Connect(IPEndPoint target, int generation, int index, CancellationToken outer)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var peer = new TcpClient(target.AddressFamily);
            var start = Stopwatch.GetTimestamp();
            var outcome = "connected";
            try { await peer.ConnectAsync(target.Address, target.Port, deadline.Token); }
            catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset or SocketError.ConnectionAborted)
            { outcome = error.SocketErrorCode.ToString(); }
            catch (Exception error) { outcome = error.ToString(); throw; }
            finally { Attempt(generation, $"disruption-{index}", target, start, outcome); }
        }

        private static async Task Exchange(IPEndPoint target, string expected, string path, int generation, string phase, CancellationToken outer, string authority = "127.0.0.1")
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using var peer = new TcpClient(target.AddressFamily);
            var start = Stopwatch.GetTimestamp();
            var outcome = "success";
            try
            {
                await peer.ConnectAsync(target.Address, target.Port, deadline.Token);
                Attempt(generation, phase + "-connect", target, start, "connected");
                using var wire = peer.GetStream();
                await wire.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {authority}:{target.Port}\r\nConnection: close\r\n\r\n"), deadline.Token);
                using var received = new MemoryStream();
                await wire.CopyToAsync(received, deadline.Token);
                var response = Encoding.ASCII.GetString(received.ToArray());
                Assert.That(response, Does.StartWith("HTTP/1.1 200 "));
                Assert.That(response, Does.EndWith("\r\n\r\n" + expected));
            }
            catch (Exception error) { outcome = error.ToString(); throw; }
            finally { Attempt(generation, phase, target, start, outcome); }
        }

        private static void Attempt(int generation, string phase, IPEndPoint target, long start, string outcome)
            => TestContext.Out.WriteLine($"{DateTimeOffset.UtcNow:O} generation={generation} phase={phase} target={target} elapsedMs={Stopwatch.GetElapsedTime(start).TotalMilliseconds:F3} outcome={outcome}");

        private static object[] Endpoints(IHttpListener listener)
        {
            var field = typeof(Net.EndPointManager).GetField("Registrations", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing registration map.");
            var map = (IDictionary)(field.GetValue(null) ?? throw new AssertionException("Missing registrations."));
            var prefixes = (IDictionary)(map[listener] ?? throw new AssertionException("Missing listener registration."));
            return prefixes.Values.Cast<IEnumerable>().SelectMany(value => value.Cast<object>()).Distinct().ToArray();
        }

        private static async Task AssertRetired(object[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var type = endpoint.GetType();
                var socket = (Socket)(type.GetProperty("ListeningSocket", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint)
                    ?? throw new AssertionException("Missing listening socket."));
                Assert.That(socket.SafeHandle.IsClosed, Is.True, "Drain completion must release the listener handle.");
                var worker = (Task)(type.GetField("_acceptWorker", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint)
                    ?? throw new AssertionException("Missing accept worker."));
                await worker.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(worker.IsCompletedSuccessfully, Is.True, "Retirement must finish the accept worker.");
            }
        }

        private static void Record(int generation, string phase, object[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                var type = endpoint.GetType();
                var worker = (Task)(type.GetField("_acceptWorker", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint)
                    ?? throw new AssertionException("Missing accept worker."));
                var socket = (Socket)(type.GetProperty("ListeningSocket", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint)
                    ?? throw new AssertionException("Missing listening socket."));
                var local = socket.SafeHandle.IsClosed ? "closed" : socket.LocalEndPoint?.ToString();
                TestContext.Out.WriteLine($"{DateTimeOffset.UtcNow:O} generation={generation} phase={phase} local={local} endpointId={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(endpoint)} worker={worker.Status} stopped={type.GetProperty("AdmissionStopped", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(endpoint)} socketClosed={socket.SafeHandle.IsClosed} error={worker.Exception}");
            }
        }
    }
}
