using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Diagnostics;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue553_CustomDiagnostics
    {
        [TestCase(SourceLevels.Verbose, true, true)]
        [TestCase(SourceLevels.Information, true, true)]
        [TestCase(SourceLevels.Warning, false, true)]
        [TestCase(SourceLevels.Off, false, false)]
        public async Task SourceThresholdControlsRealStartupRequestAndErrorEvents(SourceLevels level, bool information, bool errors)
        {
            using var capture = new Capture(level);
            var url = Resources.GetServerAddress().Replace("localhost", "127.0.0.1");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, _ => throw new InvalidOperationException("diagnostic-553-error")));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                using var response = await client.GetAsync(url);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                server.Dispose();
            }
            Assert.That(capture.Events.Any(e => e.Type == TraceEventType.Information && e.Text.Contains("Started HTTP Listener")), Is.EqualTo(information));
            Assert.That(capture.Events.Any(e => e.Type == TraceEventType.Information && e.Text.Contains("Listener closed.")), Is.EqualTo(information));
            Assert.That(capture.Events.Any(e => e.Type == TraceEventType.Error && e.Text.Contains("diagnostic-553-error")), Is.EqualTo(errors));
            if (level == SourceLevels.Off) Assert.That(capture.Events, Is.Empty);
        }

        [TestCase(SourceLevels.Warning)]
        [TestCase(SourceLevels.Off)]
        public async Task ListenerFiltersAreIndependentAndDetachmentStopsDelivery(SourceLevels filter)
        {
            using var capture = new Capture(SourceLevels.Verbose);
            using var filtered = new EventListener { Filter = new EventTypeFilter(filter) };
            Log.Source.Listeners.Add(filtered);
            try
            {
                using var certificate = HttpsSmoke.CreateCertificate();
                var url = HttpsSmoke.GetUrl();
                using var server = HttpsSmoke.CreateServer(url, certificate);
                using var stop = new CancellationTokenSource();
                var running = server.RunAsync(stop.Token);
                try
                {
                    using var client = HttpsSmoke.CreateClient(certificate);
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("encrypted"));
                    Assert.That(capture.Events.Any(e => e.Text.Contains("Started HTTP Listener")), Is.True);
                    Assert.That(filtered.Events, Is.Empty);
                    Log.Source.Listeners.Remove(filtered);
                    var count = filtered.Events.Count;
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("encrypted"));
                    Assert.That(filtered.Events.Count, Is.EqualTo(count));
                }
                finally
                {
                    stop.Cancel();
                    await running.WaitAsync(TimeSpan.FromSeconds(10));
                    server.Dispose();
                }
            }
            finally { Log.Source.Listeners.Remove(filtered); }
        }

        [Test]
        public async Task ConcurrentRequestEventsRemainWholeAndCanBeConsumedOnAnotherThread()
        {
            using var capture = new Capture(SourceLevels.Information);
            var url = Resources.GetServerAddress().Replace("localhost", "127.0.0.1");
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(new ActionModule("/", HttpVerbs.Get, c => c.SendStringAsync("ok", "text/plain", WebServer.Utf8NoBomEncoding)));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => client.GetStringAsync(url + "request-" + i)));
                Assert.That(replies, Is.All.EqualTo("ok"));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                server.Dispose();
            }
            var records = await Task.Run(() => capture.Events.ToArray());
            for (var i = 0; i < 12; i++)
                Assert.That(records.Count(e => e.Text.Contains(" GET /request-" + i + ": \"200 OK\" sent in ")), Is.EqualTo(1));
            Assert.That(records.Count(e => e.Text.Contains(" GET /request-")), Is.EqualTo(12));
        }

        [Test]
        public async Task PrivateKeyPfxServesHttpsWithoutWindowsRegistrationAndRejectedTlsDoesNotDispatchHttp()
        {
            using var capture = new Capture(SourceLevels.Verbose);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
            using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx, "test-553-only"), "test-553-only");
            var url = HttpsSmoke.GetUrl();
            var requests = 0;
            var options = new WebServerOptions().WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate);
            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(options.AutoLoadCertificate, Is.False);
            Assert.That(options.AutoRegisterCertificate, Is.False);
            using var server = new WebServer(options).WithModule(new ActionModule("/", HttpVerbs.Get, c =>
            {
                Interlocked.Increment(ref requests);
                return c.SendStringAsync("private-key-553", "text/plain", WebServer.Utf8NoBomEncoding);
            }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var untrusted = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
                await Assert.ThatAsync(async () => await untrusted.GetStringAsync(url), Throws.TypeOf<HttpRequestException>());
                Assert.That(requests, Is.Zero);
                using var trusted = HttpsSmoke.CreateClient(certificate);
                Assert.That(await trusted.GetStringAsync(url), Is.EqualTo("private-key-553"));
                Assert.That(requests, Is.EqualTo(1));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                server.Dispose();
            }
            Assert.That(capture.Events.Any(e => e.Text.Contains("Started HTTP Listener")), Is.True);
            Assert.That(capture.Events.Any(e => e.Text.Contains("Listener closed.")), Is.True);
        }

        private sealed class Capture : IDisposable
        {
            private readonly SourceLevels _previous = Log.Source.Switch.Level;
            private readonly EventListener _listener = new();
            public Capture(SourceLevels level)
            {
                Log.Source.Switch.Level = level;
                Log.Source.Listeners.Add(_listener);
            }
            public ConcurrentQueue<(TraceEventType Type, string Text)> Events => _listener.Events;
            public void Dispose()
            {
                Log.Source.Listeners.Remove(_listener);
                Log.Source.Switch.Level = _previous;
                _listener.Dispose();
            }
        }

        private sealed class EventListener : TraceListener
        {
            public ConcurrentQueue<(TraceEventType Type, string Text)> Events { get; } = new();
            public override bool IsThreadSafe => true;
            public override void Write(string? message) => throw new InvalidOperationException("Expected complete TraceEvent records.");
            public override void WriteLine(string? message) => throw new InvalidOperationException("Expected complete TraceEvent records.");
            public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, params object?[]? args)
            {
                if (Filter != null && !Filter.ShouldTrace(cache, source, type, id, format, args, null, null)) return;
                Events.Enqueue((type, args == null ? format ?? string.Empty : string.Format(CultureInfo.InvariantCulture, format!, args)));
            }
        }
    }
}
