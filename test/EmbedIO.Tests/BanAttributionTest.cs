using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A ban must fall on the client that earned it. The regex criterion reads diagnostic
    // messages, which are shared by the whole process, so the client a message belongs to is
    // taken from the request the message names, not from whichever request passed through the
    // module most recently.
    public class BanAttributionTest
    {
        // A 404 on a route this module does not guard names a request the module never saw,
        // so it is charged to nobody rather than to the last client seen on the guarded route.
        [Test]
        public async Task AMessageFromAnUnguardedRouteBansNobody()
        {
            await WithGuard("/guard-unguarded", async (port, route) =>
            {
                Assert.That(await Status(port, route).ConfigureAwait(false), Is.EqualTo(200), "the guarded route starts open");
                Assert.That(await Status(port, "/outside-the-guard").ConfigureAwait(false), Is.EqualTo(404), "the unguarded route answers 404");
                await Task.Delay(400).ConfigureAwait(false);
                Assert.That(await Status(port, route).ConfigureAwait(false), Is.EqualTo(200), "a 404 outside the guard must not ban");
            }).ConfigureAwait(false);
        }

        // The feature still works: an offence on the guarded route bans the client that caused it.
        [Test]
        public async Task AMatchingMessageOnTheGuardedRouteStillBansItsClient()
        {
            // No responder, so every request under the guard answers 404 and earns the match.
            await WithGuard("/", async (port, _) =>
            {
                Assert.That(await Status(port, "/missing-resource").ConfigureAwait(false), Is.EqualTo(404));
                await Task.Delay(400).ConfigureAwait(false);
                Assert.That(await Status(port, "/missing-resource").ConfigureAwait(false), Is.EqualTo(403), "the offending client is banned");
            }, respond: false).ConfigureAwait(false);
        }

        // The sharper case: two clients, and the one that did nothing must stay admitted.
        [Test]
        public async Task AnOffencesMessageIsNotChargedToAnotherClient()
        {
            const string Bystander = "127.0.0.1";
            const string Offender = "127.0.0.2";
            if (!CanBindLocally(Offender))
                Assert.Ignore($"This host has no {Offender} to present as a second client address.");

            await WithGuard("/guard-two-clients", async (port, route) =>
            {
                Assert.That(await Status(port, route, Bystander).ConfigureAwait(false), Is.EqualTo(200));
                Assert.That(await Status(port, "/outside-the-guard", Offender).ConfigureAwait(false), Is.EqualTo(404));
                await Task.Delay(400).ConfigureAwait(false);
                Assert.That(await Status(port, route, Bystander).ConfigureAwait(false), Is.EqualTo(200), "the bystander must not inherit the offence");
            }).ConfigureAwait(false);
        }

        // Pins the prefix the attribution depends on. IPBanningModule.ResolveMessageClient reads
        // the request id from the start of a diagnostic message, and nothing else enforces that
        // shape, so this fails loudly if the message format changes.
        [Test]
        public async Task ARequestDiagnosticMessageStartsWithItsRequestId()
        {
            var records = new ConcurrentQueue<string>();
            var trace = new CapturingTrace(records);
            var source = EmbedIO.Diagnostics.Log.Source;
            var previousLevel = source.Switch.Level;
            string? observedId = null;

            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .OnGet("/identified", context =>
                {
                    observedId = context.Id;
                    return context.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding);
                });
            using var stop = new CancellationTokenSource();
            source.Listeners.Add(trace);
            Task? running = null;
            try
            {
                source.Switch.Level = SourceLevels.Information;
                running = server.RunAsync(stop.Token);
                Assert.That(server.Listener.IsListening, Is.True, $"The fixture must own its endpoint: {url}");
                Assert.That(await Status(new Uri(url).Port, "/identified").ConfigureAwait(false), Is.EqualTo(200));
                await Task.Delay(400).ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                if (running != null)
                {
                    try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { }
                }

                source.Listeners.Remove(trace);
                source.Switch.Level = previousLevel;
            }

            Assert.That(observedId, Is.Not.Null.And.Not.Empty, "the handler must have seen its context id");
            Assert.That(records, Has.Some.Contains($"[{observedId}]"),
                "a request's diagnostic message must name its request id, which the ban attribution parses");
        }

        private static async Task WithGuard(string route, Func<int, string, Task> body, bool respond = true)
        {
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
            var ban = new IPBanningModule(route, null, 10);
            // One matching message is enough, so the cases need no repetition to reach a ban.
            ban.RegisterCriterion(new IPBanningRegexCriterion(ban, new[] { "\"404" }, 1, 60));
            server.WithModule(ban);
            if (respond)
                server.WithModule(new Actions.ActionModule(route, HttpVerbs.Any,
                    context => context.SendStringAsync("guarded", "text/plain", WebServer.Utf8NoBomEncoding)));

            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True, $"The fixture must own its endpoint: {url}");
                await body(new Uri(url).Port, route == "/" ? "/" : route).ConfigureAwait(false);
            }
            finally
            {
                stop.Cancel();
                try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }

        private static bool CanBindLocally(string address)
        {
            try
            {
                using var socket = new TcpClient(new IPEndPoint(IPAddress.Parse(address), 0));
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        // A raw request, so the case can choose which local address presents itself as the client.
        private static async Task<int> Status(int port, string path, string? fromAddress = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var client = fromAddress == null
                ? new TcpClient()
                : new TcpClient(new IPEndPoint(IPAddress.Parse(fromAddress), 0));
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"),
                timeout.Token).ConfigureAwait(false);
            var buffer = new byte[1024];
            var count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
            var head = Encoding.ASCII.GetString(buffer, 0, count);
            var space = head.IndexOf(' ', StringComparison.Ordinal);
            return space < 0 ? 0 : int.Parse(head.Substring(space + 1, 3), System.Globalization.CultureInfo.InvariantCulture);
        }

        private sealed class CapturingTrace : TraceListener
        {
            private readonly ConcurrentQueue<string> _records;

            internal CapturingTrace(ConcurrentQueue<string> records) => _records = records;

            public override void Write(string? message) => _records.Enqueue(message ?? string.Empty);

            public override void WriteLine(string? message) => _records.Enqueue(message ?? string.Empty);
        }
    }
}
