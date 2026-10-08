using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.DependencyInjection;
using EmbedIO.Tests.TestObjects;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using NeoLog = EmbedIO.Diagnostics.Log;

namespace EmbedIO.Tests.Issues
{
    [NonParallelizable]
    public class Issue475_DiagnosticsForwarding
    {
        private SourceLevels _oldLevel;
        private bool _oldGlobalLock;

        [SetUp]
        public void SetUp()
        {
            _oldLevel = NeoLog.Source.Switch.Level;
            _oldGlobalLock = Trace.UseGlobalLock;
            NeoLog.Source.Switch.Level = SourceLevels.All;
            Trace.UseGlobalLock = true;
        }

        [TearDown]
        public void TearDown()
        {
            NeoLog.Source.Switch.Level = _oldLevel;
            Trace.UseGlobalLock = _oldGlobalLock;
        }

        [Test]
        public void NullFactoryDoesNotChangeListeners()
        {
            var before = Snapshot();
            Assert.That(() => DiagnosticsLoggingExtensions.ForwardEmbedIODiagnostics(null), Throws.ArgumentNullException);
            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedFactoryConstructionDoesNotLeaveARegistration(bool nullLogger)
        {
            var before = Snapshot();
            using var factory = new CaptureFactory { FailCreate = !nullLogger };
            var source = nullLogger ? DispatchProxy.Create<ILoggerFactory, NullLoggerFactory>() : (ILoggerFactory)factory;
            Assert.That(() => source.ForwardEmbedIODiagnostics(), Throws.Exception);
            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(factory.Disposed, Is.False);
        }

        [TestCase(TraceEventType.Critical, LogLevel.Critical)]
        [TestCase(TraceEventType.Error, LogLevel.Error)]
        [TestCase(TraceEventType.Warning, LogLevel.Warning)]
        [TestCase(TraceEventType.Information, LogLevel.Information)]
        [TestCase(TraceEventType.Verbose, LogLevel.Debug)]
        public void ForwardsSeverityCategoryIdAndLiteralBraces(TraceEventType type, LogLevel level)
        {
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            NeoLog.Source.TraceEvent(type, 42, "[component] {literal}");
            var entry = factory.Events.Single();
            Assert.That(entry.Level, Is.EqualTo(level));
            Assert.That(entry.Id.Id, Is.EqualTo(42));
            Assert.That(entry.Category, Is.EqualTo("EmbedIO"));
            Assert.That(entry.Text, Is.EqualTo("[component] {literal}"));
            Assert.That(entry.Exception, Is.Null);
            Assert.That(entry.Fields.Single(p => p.Key == "{OriginalFormat}").Value, Is.EqualTo("{TraceMessage}"));
            Assert.That(registration.ForwardingFailures, Is.Zero);
        }

        [TestCase(TraceEventType.Start)]
        [TestCase(TraceEventType.Stop)]
        [TestCase(TraceEventType.Transfer)]
        public void ActivityEventsAreNotSeverityLevels(TraceEventType type)
        {
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            NeoLog.Source.TraceEvent(type, 0, "activity");
            Assert.That(factory.Events, Is.Empty);
            Assert.That(factory.EnabledCalls, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnsupportedTraceDataIsNotMisrepresentedAsInformation(bool array)
        {
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            if (array) NeoLog.Source.TraceData(TraceEventType.Error, 42, new object[] { "one", "two" });
            else NeoLog.Source.TraceData(TraceEventType.Error, 42, "one");
            Assert.That(factory.Events, Is.Empty);
        }

        [Test]
        public void SourceAndProviderFilteringPrecedeFormatting()
        {
            using var factory = new CaptureFactory { Enabled = false };
            using var registration = factory.ForwardEmbedIODiagnostics();
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "{0}", new BadText());
            factory.Enabled = true;
            NeoLog.Source.Switch.Level = SourceLevels.Off;
            NeoLog.Source.TraceEvent(TraceEventType.Error, 0, "{0}", new BadText());
            NeoLog.Source.Switch.Level = SourceLevels.Information;
            NeoLog.Source.TraceEvent(TraceEventType.Verbose, 0, "{0}", new BadText());
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "visible");
            Assert.That(factory.Events.Single().Text, Is.EqualTo("visible"));
            Assert.That(registration.ForwardingFailures, Is.Zero);
            Assert.That(NeoLog.Source.Switch.Level, Is.EqualTo(SourceLevels.Information));
        }

        [TestCase("provider")]
        [TestCase("enabled")]
        [TestCase("format")]
        public void FailureIsContainedAndNextEventRecovers(string failure)
        {
            using var factory = new CaptureFactory { FailLog = failure == "provider", FailEnabled = failure == "enabled" };
            using var registration = factory.ForwardEmbedIODiagnostics();
            Assert.DoesNotThrow(() => NeoLog.Source.TraceEvent(TraceEventType.Warning, 0,
                failure == "format" ? "{1}" : "{0}", "failed"));
            Assert.That(registration.ForwardingFailures, Is.EqualTo(1));
            factory.FailLog = factory.FailEnabled = false;
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "recovered");
            Assert.That(factory.Events.Single().Text, Is.EqualTo("recovered"));
        }

        [Test]
        public void InheritedListenerFilterIsRespectedAndFailureContained()
        {
            using var factory = new CaptureFactory();
            var before = Snapshot();
            using var registration = factory.ForwardEmbedIODiagnostics();
            var listener = Snapshot().Except(before).Single();
            listener.Filter = new EventTypeFilter(SourceLevels.Off);
            NeoLog.Source.TraceEvent(TraceEventType.Error, 0, "{0}", new BadText());
            Assert.That(factory.EnabledCalls, Is.Zero);
            listener.Filter = new BadFilter();
            Assert.DoesNotThrow(() => NeoLog.Source.TraceEvent(TraceEventType.Error, 0, "filtered"));
            Assert.That(registration.ForwardingFailures, Is.EqualTo(1));
            listener.Filter = null;
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "recovered");
            Assert.That(factory.Events.Single().Text, Is.EqualTo("recovered"));
        }

        [Test]
        public void InvariantFormattingAndEscapedLineBreaksPreserveData()
        {
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "{0:0.0}\r\nnext", 1.5);
            }
            finally { CultureInfo.CurrentCulture = culture; }
            Assert.That(factory.Events.Single().Text, Is.EqualTo("1.5\\r\\nnext"));
        }

        [Test]
        public void DisposalRemovesOnlyItsListenerAndBorrowsFactory()
        {
            using var factory = new CaptureFactory();
            var before = Snapshot();
            var level = NeoLog.Source.Switch.Level;
            var registration = factory.ForwardEmbedIODiagnostics();
            Assert.That(Snapshot().Length, Is.EqualTo(before.Length + 1));
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "first");
            registration.Dispose();
            registration.Dispose();
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "after");
            Assert.That(Snapshot(), Is.EqualTo(before));
            Assert.That(factory.Events.Single().Text, Is.EqualTo("first"));
            Assert.That(factory.Disposed, Is.False);
            Assert.That(NeoLog.Source.Switch.Level, Is.EqualTo(level));
            factory.CreateLogger("application").LogInformation("still usable");
            Assert.That(factory.Events.Last().Text, Is.EqualTo("still usable"));
        }

        [Test]
        public void SeparateDestinationsHaveIndependentRegistrations()
        {
            using var first = new CaptureFactory();
            using var second = new CaptureFactory();
            using var one = first.ForwardEmbedIODiagnostics();
            using var two = second.ForwardEmbedIODiagnostics();
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "shared source");
            one.Dispose();
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "second only");
            Assert.That(first.Events.Select(e => e.Text), Is.EqualTo(new[] { "shared source" }));
            Assert.That(second.Events.Select(e => e.Text), Is.EqualTo(new[] { "shared source", "second only" }));
        }

        [Test]
        public void RecursiveProviderCannotCreateAFeedbackLoop()
        {
            using var factory = new CaptureFactory { Callback = () => NeoLog.Source.TraceEvent(TraceEventType.Error, 0, "recursive") };
            using var registration = factory.ForwardEmbedIODiagnostics();
            NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "outer");
            Assert.That(factory.Events.Single().Text, Is.EqualTo("outer"));
            Assert.That(registration.RecursiveEventsDropped, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentRecordsRemainWhole(bool globalLock)
        {
            Trace.UseGlobalLock = globalLock;
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
                NeoLog.Source.TraceEvent(TraceEventType.Information, i, "begin-{0}-end", i))));
            Assert.That(factory.Events.Count, Is.EqualTo(100));
            foreach (var entry in factory.Events)
                Assert.That(entry.Text, Is.EqualTo($"begin-{entry.Id.Id}-end"));
        }

        [Test]
        public async Task ConcurrentDisposalWaitsForAnActiveCallback()
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var firstStarted = new ManualResetEventSlim();
            using var secondStarted = new ManualResetEventSlim();
            using var factory = new CaptureFactory { Callback = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); } };
            using var registration = factory.ForwardEmbedIODiagnostics();
            var emit = Task.Run(() => NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "active"));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var first = Task.Run(() => { firstStarted.Set(); registration.Dispose(); });
            var second = Task.Run(() => { secondStarted.Set(); registration.Dispose(); });
            try
            {
                Assert.That(firstStarted.Wait(TimeSpan.FromSeconds(5)) && secondStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
                var stillWaiting = Task.Delay(100);
                Assert.That(await Task.WhenAny(first, second, stillWaiting), Is.SameAs(stillWaiting));
            }
            finally { release.Set(); }
            await Task.WhenAll(emit, first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(factory.Events.Single().Text, Is.EqualTo("active"));
            Assert.That(registration.ForwardingFailures, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CallbackDisposalDoesNotSkipAnotherListener(bool globalLock)
        {
            Trace.UseGlobalLock = globalLock;
            using var factory = new CaptureFactory();
            var before = Snapshot();
            using var registration = factory.ForwardEmbedIODiagnostics();
            using var other = new MonitorListener();
            NeoLog.Source.Listeners.Add(other);
            try
            {
                factory.Callback = registration.Dispose;
                NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "outer");
                Assert.That(other.Messages.Single(), Is.EqualTo("outer"));
                if (!globalLock)
                {
                    Assert.That(Snapshot().Length, Is.EqualTo(before.Length + 2));
                    registration.Dispose();
                }
                Assert.That(SpinWait.SpinUntil(() => NeoLog.Source.Listeners.Count == before.Length + 1, TimeSpan.FromSeconds(5)), Is.True);
                NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "after");
                Assert.That(factory.Events.Single().Text, Is.EqualTo("outer"));
                Assert.That(other.Messages.Last(), Is.EqualTo("after"));
                Assert.That(factory.Disposed, Is.False);
            }
            finally { NeoLog.Source.Listeners.Remove(other); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CallbackCanDisposeAnotherDestinationWithoutSkippingAnObserver(bool globalLock)
        {
            Trace.UseGlobalLock = globalLock;
            using var first = new CaptureFactory();
            using var second = new CaptureFactory();
            var before = Snapshot();
            using var one = first.ForwardEmbedIODiagnostics();
            using var two = second.ForwardEmbedIODiagnostics();
            using var other = new MonitorListener();
            NeoLog.Source.Listeners.Add(other);
            try
            {
                first.Callback = two.Dispose;
                NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "outer");
                Assert.That(other.Messages.Single(), Is.EqualTo("outer"));
                Assert.That(second.Events, Is.Empty);
                if (!globalLock)
                {
                    Assert.That(Snapshot().Length, Is.EqualTo(before.Length + 3));
                    two.Dispose();
                }
                Assert.That(SpinWait.SpinUntil(() => NeoLog.Source.Listeners.Count == before.Length + 2, TimeSpan.FromSeconds(5)), Is.True);
                Assert.That(first.Events.Single().Text, Is.EqualTo("outer"));
            }
            finally { NeoLog.Source.Listeners.Remove(other); }
        }

        [Test]
        public async Task CallbackDisposalCanFinishWhileAnExternalDisposerWaits()
        {
            using var entered = new ManualResetEventSlim();
            using var continueCallback = new ManualResetEventSlim();
            using var disposerStarted = new ManualResetEventSlim();
            using var factory = new CaptureFactory();
            using var registration = factory.ForwardEmbedIODiagnostics();
            factory.Callback = () =>
            {
                entered.Set();
                if (!continueCallback.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                registration.Dispose();
            };
            var emit = Task.Run(() => NeoLog.Source.TraceEvent(TraceEventType.Information, 0, "outer"));
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var disposer = Task.Run(() => { disposerStarted.Set(); registration.Dispose(); });
            try
            {
                Assert.That(disposerStarted.Wait(TimeSpan.FromSeconds(5)), Is.True);
                var stillWaiting = Task.Delay(100);
                Assert.That(await Task.WhenAny(disposer, stillWaiting), Is.SameAs(stillWaiting));
            }
            finally { continueCallback.Set(); }
            await Task.WhenAll(emit, disposer).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(factory.Events.Single().Text, Is.EqualTo("outer"));
            Assert.That(registration.ForwardingFailures, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task RealHttpAndShutdownRetainApplicationLoggingOwnership(bool failingProvider)
        {
            using var factory = new CaptureFactory { FailLog = failingProvider };
            using var registration = factory.ForwardEmbedIODiagnostics();
            using var stop = new CancellationTokenSource();
            var url = Resources.GetServerAddress().Replace("localhost", "127.0.0.1", StringComparison.Ordinal);
            using (var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .OnGet("/hello", c => c.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding)))
            {
                var running = server.RunAsync(stop.Token);
                try
                {
                    using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
                    Assert.That(await client.GetStringAsync(url + "hello"), Is.EqualTo("hello"));
                }
                finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            }
            Assert.That(factory.Disposed, Is.False);
            if (failingProvider) Assert.That(registration.ForwardingFailures, Is.GreaterThan(0));
            else Assert.That(factory.Events.Any(e => (e.Text.IndexOf("Listener closed.", System.StringComparison.Ordinal) >= 0)), Is.True);
        }

        private static TraceListener[] Snapshot() => NeoLog.Source.Listeners.Cast<TraceListener>().ToArray();
        private sealed record Entry(LogLevel Level, EventId Id, string Category, string Text, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> Fields);

        private sealed class CaptureFactory : ILoggerFactory
        {
            public ConcurrentQueue<Entry> Events { get; } = new();
            public bool Enabled { get; set; } = true;
            public bool FailCreate { get; set; }
            public bool FailEnabled { get; set; }
            public bool FailLog { get; set; }
            public bool Disposed { get; private set; }
            public int EnabledCalls { get; private set; }
            public Action? Callback { get; set; }
            public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ILogger CreateLogger(string categoryName)
                => FailCreate ? throw new InvalidOperationException("create failed") : new CaptureLogger(this, categoryName);

            private sealed class CaptureLogger(CaptureFactory owner, string category) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel level)
                {
                    owner.EnabledCalls++;
                    return owner.FailEnabled ? throw new InvalidOperationException("enabled failed") : owner.Enabled;
                }
                public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                {
                    if (owner.FailLog) throw new InvalidOperationException("log failed");
                    owner.Callback?.Invoke();
                    var fields = state is IReadOnlyList<KeyValuePair<string, object?>> structured
                        ? structured.ToArray() : Array.Empty<KeyValuePair<string, object?>>();
                    owner.Events.Enqueue(new Entry(level, id, category, formatter(state, exception), exception, fields));
                }
            }
        }

        private sealed class MonitorListener : TraceListener
        {
            public ConcurrentQueue<string?> Messages { get; } = new();
            public override void Write(string? message) { }
            public override void WriteLine(string? message) { }
            public override void TraceEvent(TraceEventCache? cache, string source, TraceEventType type, int id, string? message) => Messages.Enqueue(message);
        }
        public class NullLoggerFactory : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => targetMethod?.Name == nameof(ILoggerFactory.CreateLogger) ? null : throw new NotSupportedException();
        }

        private sealed class BadText { public override string ToString() => throw new InvalidOperationException("must not format"); }
        private sealed class BadFilter : TraceFilter
        {
            public override bool ShouldTrace(TraceEventCache? cache, string source, TraceEventType type, int id, string? format, object?[]? args, object? data1, object?[]? data)
                => throw new InvalidOperationException("filter failed");
        }
    }
}
