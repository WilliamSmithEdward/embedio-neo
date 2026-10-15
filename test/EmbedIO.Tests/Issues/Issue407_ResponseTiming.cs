using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Diagnostics;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue407_ResponseTiming
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task IdleBetweenRequestsDoesNotBecomeLoggedProcessingTime(bool secure, bool sessions)
        {
            await using var host = new Fixture(secure, sessions);
            await host.Connect();
            await host.Send("GET /first HTTP/1.1");
            host.ReadCookie(await host.Head());
            Assert.That(host.HasCookie, Is.EqualTo(sessions));
            var first = await host.Report("/first");
            var previous = first;
            for (var i = 0; i < 2; i++)
            {
                await host.IdleAfter(previous);
                var held = Stopwatch.StartNew();
                await Task.Delay(180, host.Token);
                held.Stop();
                var path = "/idle-" + i;
                await host.Send("GET " + path + " HTTP/1.1");
                await host.Head();
                var next = await host.Report(path);
                Assert.That(next.Context.RemoteEndPoint.Port, Is.EqualTo(first.Context.RemoteEndPoint.Port));
                Assert.That(next.SessionCount, Is.EqualTo(sessions ? i + 2 : 0));
                Assert.That(next.AgeAtLog - next.LoggedMilliseconds, Is.GreaterThanOrEqualTo(held.ElapsedMilliseconds - 5),
                    "Context age keeps its creation semantics, but the processing log must exclude the observed idle interval.");
                previous = next;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task HeaderReadWaitingIsOutsideServerHandling(bool secure)
        {
            await using var host = new Fixture(secure, false);
            var wire = await host.Connect();
            await host.Send("GET /first HTTP/1.1");
            await host.Head();
            await host.IdleAfter(await host.Report("/first"));
            await wire.WriteAsync(Encoding.ASCII.GetBytes("GET /headers HTTP/1.1\r\n"), host.Token);
            var held = Stopwatch.StartNew();
            await Task.Delay(180, host.Token);
            held.Stop();
            await wire.WriteAsync(Encoding.ASCII.GetBytes($"Host: {host.Url.Host}:{host.Url.Port}\r\n\r\n"), host.Token);
            await host.Head();
            var report = await host.Report("/headers");
            Assert.That(report.AgeAtLog - report.LoggedMilliseconds, Is.GreaterThanOrEqualTo(held.ElapsedMilliseconds - 5));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AwaitedRequestBodyTimeIsIncluded(bool secure)
        {
            await using var host = new Fixture(secure, false, "body");
            var wire = await host.Connect();
            await host.Send("POST /measure HTTP/1.1", "Content-Length: 1\r\n");
            await host.Entered.Task.WaitAsync(host.Token);
            var held = Stopwatch.StartNew();
            await Task.Delay(180, host.Token);
            held.Stop();
            await wire.WriteAsync(new byte[] { 65 }, host.Token);
            await host.Head();
            var report = await host.Report("/measure");
            Assert.That(report.LoggedMilliseconds, Is.GreaterThanOrEqualTo(held.ElapsedMilliseconds - 5));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task CompletionCallbacksAreIncludedEvenAfterClientReceivesResponse(bool secure)
        {
            await using var host = new Fixture(secure, false, "close");
            await host.Connect();
            await host.Send("GET /measure HTTP/1.1");
            await host.Head();
            await host.Closing.Task.WaitAsync(host.Token);
            Assert.That(host.Probes["/measure"].Logged.Task.IsCompleted, Is.False);
            var held = Stopwatch.StartNew();
            await Task.Delay(180, host.Token);
            held.Stop();
            host.CloseGate.Set();
            var report = await host.Report("/measure");
            Assert.That(report.LoggedMilliseconds, Is.GreaterThanOrEqualTo(held.ElapsedMilliseconds - 5));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task AwaitedRestControllerPreservesJsonAndPerRequestTiming(bool secure, bool http2)
        {
            await using var host = new Fixture(secure, true, "api");
            using var client = secure ? HttpsSmoke.CreateClient(host.Certificate) : new HttpClient(new SocketsHttpHandler { UseProxy = false });
            async Task<Probe> Request(string name)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, host.Url + "api/" + name)
                { Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await client.SendAsync(request, host.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Version.Major, Is.EqualTo(http2 ? 2 : 1));
                Assert.That(await response.Content.ReadAsStringAsync(host.Token), Is.EqualTo("{\"status\":\"ok\"}"));
                return await host.Report("/api/" + name);
            }
            var first = await Request("first");
            if (!http2) await host.IdleAfter(first);
            var held = Stopwatch.StartNew();
            await Task.Delay(180, host.Token);
            held.Stop();
            var second = await Request("second");
            Assert.That(second.Context.RemoteEndPoint.Port, Is.EqualTo(first.Context.RemoteEndPoint.Port));
            Assert.That(second.LoggedMilliseconds, Is.GreaterThanOrEqualTo(0));
            if (!http2)
                Assert.That(second.AgeAtLog - second.LoggedMilliseconds, Is.GreaterThanOrEqualTo(held.ElapsedMilliseconds - 5));
            else Assert.That(second.Context.Id, Is.Not.EqualTo(first.Context.Id));
        }

        public sealed class TimingController : WebApiController
        {
            private readonly Action<IHttpContext> _record;
            public TimingController(Action<IHttpContext> record) => _record = record;
            [Route(HttpVerbs.Get, "/{name}")]
            public async Task Get(string name)
            {
                _record(HttpContext);
                await HttpContext.SendDataAsync(new { status = "ok" });
            }
        }

        private sealed class Probe
        {
            internal Probe(IHttpContext context) => Context = context;
            internal IHttpContext Context { get; }
            internal long AgeAtLog { get; set; }
            internal int SessionCount { get; set; }
            internal long LoggedMilliseconds { get; set; }
            internal TaskCompletionSource<Probe> Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Fixture : IAsyncDisposable
        {
            private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
            private readonly CancellationTokenSource _stop = new();
            private readonly WebServer _server;
            private readonly Task _running;
            private readonly bool _sessions;
            private readonly TcpClient _peer = new();
            private readonly X509Certificate2? _certificate;
            private readonly SourceLevels _previous = Log.Source.Switch.Level;
            private readonly ReceiptListener _capture;
            private Stream? _wire;
            private string _cookie = string.Empty;
            internal Fixture(bool secure, bool sessions, string kind = "fast")
            {
                _sessions = sessions;
                Url = new UriBuilder(Resources.GetServerAddress()) { Host = "127.0.0.1", Scheme = secure ? "https" : "http" }.Uri;
                _certificate = secure ? HttpsSmoke.CreateCertificate() : null;
                _capture = new ReceiptListener(Probes);
                Log.Source.Switch.Level = SourceLevels.Information;
                Log.Source.Listeners.Add(_capture);
                var options = new WebServerOptions().WithUrlPrefix(Url.ToString()).WithMode(HttpListenerMode.EmbedIO);
                if (_certificate != null) options.WithCertificate(_certificate);
                _server = new WebServer(options);
                if (sessions) _server.WithLocalSessionManager();
                if (kind == "api") _server.WithWebApi("/api", m => m.WithController(() => new TimingController(Record)));
                else _server.WithModule(new ActionModule("/", HttpVerbs.Any, async c =>
                {
                    Record(c);
                    if (c.Request.Url.AbsolutePath == "/measure")
                    {
                        Entered.TrySetResult(true);
                        if (kind == "body")
                        {
                            var one = new byte[1];
                            Assert.That(await c.Request.InputStream.ReadAsync(one, c.CancellationToken), Is.EqualTo(1));
                            Assert.That(one[0], Is.EqualTo(65));
                        }
                        if (kind == "close") c.OnClose(_ => { Closing.TrySetResult(true); CloseGate.Wait(Token); });
                    }
                    c.Response.StatusCode = 204;
                    c.Response.ContentLength64 = 0;
                }));
                _running = _server.RunAsync(_stop.Token);
            }
            internal ConcurrentDictionary<string, Probe> Probes { get; } = new();
            internal TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal TaskCompletionSource<bool> Closing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal ManualResetEventSlim CloseGate { get; } = new();
            internal Uri Url { get; }
            internal bool HasCookie => _cookie.Length != 0;
            internal CancellationToken Token => _deadline.Token;
            internal X509Certificate2 Certificate => _certificate ?? throw new AssertionException("Expected test certificate.");
            private void Record(IHttpContext context)
            {
                var count = 0;
                if (_sessions)
                {
                    count = context.Session["count"] is int previous ? previous + 1 : 1;
                    context.Session["count"] = count;
                }
                Probes[context.Request.Url.AbsolutePath] = new Probe(context) { SessionCount = count };
            }
            internal async Task<Stream> Connect()
            {
                await _peer.ConnectAsync(Url.Host, Url.Port, Token);
                _wire = _certificate == null ? _peer.GetStream() : new SslStream(_peer.GetStream(), false,
                    (_, presented, _, _) => presented != null && presented.GetRawCertData().AsSpan().SequenceEqual(Certificate.RawData));
                if (_wire is SslStream tls) await tls.AuthenticateAsClientAsync("localhost").WaitAsync(Token);
                return _wire;
            }
            internal Task Send(string firstLine, string fields = "") => (_wire ?? throw new AssertionException("Missing peer.")).WriteAsync(
                Encoding.ASCII.GetBytes($"{firstLine}\r\nHost: {Url.Host}:{Url.Port}\r\n{_cookie}{fields}\r\n"), Token).AsTask();
            internal void ReadCookie(string head)
            {
                var match = Regex.Match(head, @"(?im)^Set-Cookie: ([^;\r\n]+)");
                if (match.Success) _cookie = "Cookie: " + match.Groups[1].Value + "\r\n";
            }
            internal async Task<string> Head()
            {
                var wire = _wire ?? throw new AssertionException("Missing peer.");
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    Assert.That(head.Length, Is.LessThan(8192));
                    if (await wire.ReadAsync(one, Token) == 0) throw new IOException("Unexpected response EOF.");
                    head.Append((char)one[0]);
                }
                Assert.That(head.ToString(), Does.StartWith("HTTP/1.1 204 "));
                return head.ToString();
            }
            internal async Task<Probe> Report(string path)
            {
                while (!Probes.ContainsKey(path)) { Token.ThrowIfCancellationRequested(); await Task.Yield(); }
                return await Probes[path].Logged.Task.WaitAsync(Token);
            }
            internal async Task IdleAfter(Probe previous)
            {
                var session = previous.Context.GetType().GetProperty("Connection", Hidden)?.GetValue(previous.Context)
                    ?? throw new AssertionException("Missing HTTP/1 session.");
                var type = session.GetType();
                var gate = type.GetField("_connectionSync", Hidden)?.GetValue(session) ?? throw new AssertionException("Missing session gate.");
                var bound = type.GetField("_contextBound", Hidden) ?? throw new AssertionException("Missing ownership flag.");
                var current = type.GetField("_context", Hidden) ?? throw new AssertionException("Missing next context.");
                while (true)
                {
                    Token.ThrowIfCancellationRequested();
                    lock (gate)
                        if (bound.GetValue(session) is false && !ReferenceEquals(current.GetValue(session), previous.Context)) return;
                    await Task.Yield();
                }
            }
            public async ValueTask DisposeAsync()
            {
                CloseGate.Set();
                try { _stop.Cancel(); await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally
                {
                    _server.Dispose(); _wire?.Dispose(); _peer.Dispose(); _certificate?.Dispose();
                    Log.Source.Listeners.Remove(_capture); Log.Source.Switch.Level = _previous; _capture.Dispose();
                    CloseGate.Dispose(); _deadline.Dispose(); _stop.Dispose();
                }
            }
        }

        private sealed class ReceiptListener : TraceListener
        {
            private readonly ConcurrentDictionary<string, Probe> _probes;
            internal ReceiptListener(ConcurrentDictionary<string, Probe> probes) => _probes = probes;
            public override bool IsThreadSafe => true;
            public override void Write(string? message) => throw new InvalidOperationException("Expected complete TraceEvent records.");
            public override void WriteLine(string? message) => throw new InvalidOperationException("Expected complete TraceEvent records.");
            public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, params object?[]? args)
            {
                if (type != TraceEventType.Information) return;
                var text = args == null ? format ?? string.Empty : string.Format(CultureInfo.InvariantCulture, format ?? string.Empty, args);
                var match = Regex.Match(text, @" sent in (\d+)ms ");
                if (!match.Success) return;
                foreach (var probe in _probes.Values)
                    if (text.Contains("[" + probe.Context.Id + "]", StringComparison.Ordinal))
                    {
                        probe.AgeAtLog = probe.Context.Age;
                        probe.LoggedMilliseconds = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                        probe.Logged.TrySetResult(probe);
                    }
            }
        }
    }
}
