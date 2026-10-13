using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void SharedPortDiagnosticsPreserveErrorThroughInfoFloodForExistingAndNewSources(bool sourceAlreadyExists)
        {
            using var existing = sourceAlreadyExists ? new SharedPortControlSource() : null;
            using var capture = new SharedPortCapture("EmbedIO.Tests.SharedPortCaptureControl");
            using var created = sourceAlreadyExists ? null : new SharedPortControlSource();
            var source = existing ?? created ?? throw new AssertionException("Missing owned control source.");
            Assert.That(source.ConstructionException, Is.Null);
            source.Failure("owned-server-handshake-sentinel");
            for (var i = 0; i < 1024; i++) source.Information("owned-info-flood");
            using var snapshot = System.Text.Json.JsonDocument.Parse(capture.Snapshot(new IOException("owned-primary"), null, null));
            var root = snapshot.RootElement;
            Assert.That(root.GetProperty("ProviderObserved").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("InfoDropped").GetInt64(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("CaptureFailures").GetInt64(), Is.Zero);
            Assert.That(root.GetProperty("Errors").EnumerateArray().Any(record =>
                record.GetProperty("Payload").EnumerateArray().Any(value => value.GetString() == "owned-server-handshake-sentinel")), Is.True);
            Assert.That(root.GetProperty("Failure").GetString(), Does.Contain("owned-primary"));
        }

        [Test]
        public void SharedPortDiagnosticsPreservePrimaryWhenExceptionFormattingThrows()
        {
            var marker = new OutOfMemoryException("controlled diagnostic formatting marker");
            var primary = new SharedPortFormattingException(marker);
            Exception? secondary = null;
            using var capture = new SharedPortCapture("EmbedIO.Tests.SharedPortCaptureControl");
            var actual = Assert.Catch<SharedPortFormattingException>(() =>
            {
                try { throw primary; }
                catch (Exception error)
                {
                    secondary = capture.WriteFailure(error, null, null);
                    throw;
                }
            });
            Assert.That(actual, Is.SameAs(primary));
            Assert.That(secondary, Is.SameAs(marker));
            using var snapshot = System.Text.Json.JsonDocument.Parse(capture.Snapshot(new IOException("observable fallback"), null, null));
            Assert.That(snapshot.RootElement.GetProperty("FormatFailures").GetInt64(), Is.EqualTo(1));
            Assert.That(snapshot.RootElement.GetProperty("FirstFormatFailure").GetString(), Is.EqualTo(typeof(OutOfMemoryException).FullName));
        }

        [Test]
        public void SharedPortDiagnosticsRetainProviderIdentityAndReportReservedErrorOverflow()
        {
            using var source = new SharedPortControlSource();
            using var capture = new SharedPortCapture("EmbedIO.Tests.SharedPortCaptureControl");
            Assert.That(source.ConstructionException, Is.Null);
            source.Failure("original-error-without-filter-keywords");
            for (var index = 0; index < 70; index++) source.Failure("error-overflow-control");
            for (var index = 0; index < 1024; index++) source.Information("later-information");
            using var snapshot = System.Text.Json.JsonDocument.Parse(capture.Snapshot(new IOException("primary remains failed"), null, null));
            var root = snapshot.RootElement;
            Assert.That(root.GetProperty("Providers").EnumerateArray().Any(record =>
                record.GetProperty("Name").GetString() == source.Name && record.GetProperty("Guid").GetGuid() == source.Guid), Is.True);
            Assert.That(root.GetProperty("ProvidersDropped").GetInt64(), Is.Zero);
            Assert.That(root.GetProperty("EnableRequestsReturned").GetInt64(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("ProviderReportsEnabled").GetBoolean(), Is.True);
            Assert.That(root.GetProperty("ProviderEventsReceived").GetInt64(), Is.GreaterThanOrEqualTo(1095));
            Assert.That(root.GetProperty("ProviderEventsFiltered").GetInt32(), Is.Zero);
            Assert.That(root.GetProperty("ErrorDropped").GetInt64(), Is.EqualTo(7));
            Assert.That(root.GetProperty("Errors").EnumerateArray().Any(record =>
                record.GetProperty("EventId").GetInt32() == 2 && record.GetProperty("Payload").EnumerateArray().Any(value =>
                    value.GetString() == "original-error-without-filter-keywords")), Is.True);
            Assert.That(root.GetProperty("InfoDropped").GetInt64(), Is.GreaterThan(0));
        }

        private static bool IsRecoverableSharedPortCleanup(Exception error)
            => error is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.All(EmbedIO.Internal.ExceptionPolicy.IsRecoverable)
                : EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error);

        private static async Task CancelAndJoinSharedPortAsync(CancellationTokenSource firstStop, CancellationTokenSource secondStop,
            Task first, Task second, Exception? primary, SharedPortCapture capture)
        {
            try
            {
                try { firstStop.Cancel(); }
                catch (Exception error) when (primary != null && IsRecoverableSharedPortCleanup(error))
                { capture.Mark("cleanup-first-cancel-failure", error.GetType().FullName ?? "unknown"); }
            }
            finally
            {
                try
                {
                    try { secondStop.Cancel(); }
                    catch (Exception error) when (primary != null && IsRecoverableSharedPortCleanup(error))
                    { capture.Mark("cleanup-second-cancel-failure", error.GetType().FullName ?? "unknown"); }
                }
                finally
                {
                    var joining = Task.WhenAll(first, second);
                    try { await joining.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception) when (joining.Exception is { } allFaults && !IsRecoverableSharedPortCleanup(allFaults))
                    { throw allFaults; }
                    catch (Exception error) when (primary != null && IsRecoverableSharedPortCleanup(error))
                    { capture.Mark("cleanup-join-failure", error.GetType().FullName ?? "unknown"); }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task SharedPortCleanupAttemptsBothCancellationsAndJoinsWithoutHidingCriticalLeaves(bool criticalLeaf)
        {
            using var firstStop = new CancellationTokenSource();
            using var secondStop = new CancellationTokenSource();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception callbackFault = criticalLeaf ? new OutOfMemoryException("controlled excluded callback marker") : new IOException("controlled callback marker");
            using var firstRegistration = firstStop.Token.Register(() =>
            {
                first.TrySetResult();
                throw new AggregateException(new AggregateException(callbackFault));
            });
            using var secondRegistration = secondStop.Token.Register(() => second.TrySetResult());
            using var capture = new SharedPortCapture("EmbedIO.Tests.SharedPortCaptureControl");
            var primary = new IOException("original handshake primary marker");
            var actual = await Assert.CatchAsync<Exception>(async () =>
            {
                try { throw primary; }
                finally { await CancelAndJoinSharedPortAsync(firstStop, secondStop, first.Task, second.Task, primary, capture); }
            });
            Assert.That(firstStop.IsCancellationRequested && secondStop.IsCancellationRequested, Is.True);
            Assert.That(first.Task.IsCompletedSuccessfully && second.Task.IsCompletedSuccessfully, Is.True);
            if (criticalLeaf)
            {
                Assert.That(actual, Is.InstanceOf<AggregateException>());
                var aggregate = actual as AggregateException ?? throw new AssertionException("Missing actual critical cancellation aggregate.");
                Assert.That(aggregate.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, callbackFault)), Is.True);
            }
            else Assert.That(actual, Is.SameAs(primary));
        }

        [Test]
        public async Task SharedPortCleanupPropagatesAllJoinedFaultsWhenAwaitSelectsRecoverableFirstLeaf()
        {
            using var firstStop = new CancellationTokenSource();
            using var secondStop = new CancellationTokenSource();
            using var capture = new SharedPortCapture("EmbedIO.Tests.SharedPortCaptureControl");
            var primary = new IOException("original handshake primary marker");
            var firstFault = new IOException("first joined IO marker");
            var secondFault = new OutOfMemoryException("controlled joined excluded marker");
            var first = Task.FromException(firstFault);
            var second = Task.FromException(secondFault);
            var actual = await Assert.CatchAsync<AggregateException>(() =>
                CancelAndJoinSharedPortAsync(firstStop, secondStop, first, second, primary, capture));
            var failures = actual?.Flatten().InnerExceptions ?? throw new AssertionException("Missing actual joined critical aggregate.");
            Assert.That(failures.Any(error => ReferenceEquals(error, firstFault)), Is.True);
            Assert.That(failures.Any(error => ReferenceEquals(error, secondFault)), Is.True);
            Assert.That(firstStop.IsCancellationRequested && secondStop.IsCancellationRequested, Is.True);
            Assert.That(first.IsFaulted && second.IsFaulted, Is.True);
        }

        private sealed class SharedPortFormattingException : Exception
        {
            private readonly Exception _formatFailure;
            internal SharedPortFormattingException(Exception formatFailure) => _formatFailure = formatFailure;
            public override string ToString() => throw _formatFailure;
        }

        [EventSource(Name = "EmbedIO.Tests.SharedPortCaptureControl")]
        private sealed class SharedPortControlSource : EventSource
        {
            public static class Keywords
            {
                public const EventKeywords Default = (EventKeywords)1;
            }

            [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Default)]
            public void Information(string message) => WriteEvent(1, message);
            [Event(2, Level = EventLevel.Error, Keywords = Keywords.Default)]
            public void Failure(string message) => WriteEvent(2, message);
        }

        private sealed class SharedPortCapture : EventListener
        {
            private const string Provider = "Private.InternalDiagnostics.System.Net.Quic";
            private readonly object _gate = new();
            private readonly Queue<object> _info = new();
            private readonly List<object> _errors = new();
            private readonly List<object> _phases = new();
            private readonly List<object> _providers = new();
            private long _providersDropped;
            private int _ready;
            private long _sequence;
            private long _infoDropped;
            private long _errorDropped;
            private long _truncated;
            private long _phaseDropped;
            private int _providerObserved;
            private long _enableRequestsReturned;
            private long _providerEventsReceived;
            private bool _providerReportsEnabled;
            private readonly string _provider;
            private readonly string _taskRoles;
            private long _captureFailures;
            private string? _firstCaptureFailure;
            private long _formatFailures;
            private string? _firstFormatFailure;
            private long _fallbackFailures;

            internal SharedPortCapture(string provider = Provider, string taskRoles = "TCP server / QUIC server")
            {
                _provider = provider;
                _taskRoles = taskRoles;
                Volatile.Write(ref _ready, 1);
                foreach (var source in EventSource.GetSources()) Enable(source);
            }

            protected override void OnEventSourceCreated(EventSource source)
            {
                // EventListener's base constructor can call this before fields initialize.
                if (Volatile.Read(ref _ready) != 0) Enable(source);
            }

            private void Enable(EventSource source)
            {
                if (source.Name != _provider) return;
                lock (_gate)
                {
                    if (_providers.Count < 16)
                        _providers.Add(new { source.Name, source.Guid, Assembly = source.GetType().Assembly.GetName().FullName });
                    else _providersDropped++;
                }
                Interlocked.Exchange(ref _providerObserved, 1);
                EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
                lock (_gate)
                {
                    _enableRequestsReturned++;
                    _providerReportsEnabled |= source.IsEnabled(EventLevel.Informational, (EventKeywords)1);
                }
            }

            protected override void OnEventWritten(EventWrittenEventArgs value)
            {
                if (value.EventSource.Name != _provider) return;
                Interlocked.Increment(ref _providerEventsReceived);
                try { Capture(value); }
                catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
                {
                    lock (_gate)
                    {
                        _captureFailures++;
                        _firstCaptureFailure ??= error.GetType().FullName;
                    }
                }
            }

            private void Capture(EventWrittenEventArgs value)
            {
                lock (_gate)
                {
                    var payload = new List<string>();
                    var remaining = 16384;
                    if (value.Payload != null)
                        foreach (var item in value.Payload)
                        {
                            if (payload.Count == 16) { _truncated++; break; }
                            var text = item?.ToString() ?? "<null>";
                            if (text.Length > remaining)
                            {
                                text = text[..remaining];
                                _truncated++;
                            }
                            payload.Add(text);
                            remaining -= text.Length;
                        }
                    var record = new
                    {
                        Sequence = ++_sequence,
                        Timestamp = Stopwatch.GetTimestamp(),
                        Source = value.EventSource.Name,
                        SourceId = value.EventSource.Guid,
                        value.EventId,
                        value.EventName,
                        value.Level,
                        value.ActivityId,
                        value.RelatedActivityId,
                        Payload = payload,
                    };
                    if (value.Level is EventLevel.Critical or EventLevel.Error)
                    {
                        if (_errors.Count < 64) _errors.Add(record);
                        else _errorDropped++;
                    }
                    else
                    {
                        if (_info.Count == 512) { _info.Dequeue(); _infoDropped++; }
                        _info.Enqueue(record);
                    }
                }
            }

            internal void Mark(string phase, string detail)
            {
                lock (_gate)
                {
                    if (_phases.Count == 128) { _phaseDropped++; return; }
                    if (detail.Length > 16384) { detail = detail[..16384]; _truncated++; }
                    _phases.Add(new { Sequence = ++_sequence, Timestamp = Stopwatch.GetTimestamp(), Phase = phase, Detail = detail });
                }
            }

            private static object ReadMappedNativeModuleIdentity()
            {
                var records = new List<object>();
                var failures = new List<string>();
                var inspected = 0;
                var matched = 0;
                var truncated = false;
                if (!OperatingSystem.IsWindows())
                    return new { Available = false, Reason = "Public Process.Modules capture targets Windows", Records = records, Failures = failures, Inspected = 0, Truncated = false };
                try
                {
                    using var process = Process.GetCurrentProcess();
                    foreach (ProcessModule module in process.Modules)
                    {
                        if (++inspected > 2048) { truncated = true; break; }
                        if (!module.ModuleName.Contains("msquic", StringComparison.OrdinalIgnoreCase)) continue;
                        if (++matched > 8) { truncated = true; break; }
                        var path = module.FileName;
                        try
                        {
                            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var length = file.Length;
                            if (length > 32 * 1024 * 1024) throw new IOException("Mapped module file exceeds diagnostic hash bound.");
                            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                            var buffer = new byte[8192];
                            long read = 0;
                            int count;
                            while ((count = file.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                read += count;
                                if (read > 32 * 1024 * 1024) throw new IOException("Mapped module file grew beyond diagnostic hash bound.");
                                hash.AppendData(buffer, 0, count);
                            }
                            records.Add(new
                            {
                                Name = module.ModuleName,
                                Path = path,
                                FileVersion = module.FileVersionInfo.FileVersion,
                                FileLengthAtOpen = length,
                                BytesHashed = read,
                                FileSHA256 = Convert.ToHexString(hash.GetHashAndReset()),
                                Scope = "Public mapped path and current file bytes; not native memory hash"
                            });
                        }
                        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                        {
                            failures.Add(module.ModuleName + ":" + error.GetType().FullName + ":0x" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                        }
                    }
                }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error))
                {
                    failures.Add("Process.Modules:" + error.GetType().FullName + ":0x" + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture));
                }
                return new
                {
                    Available = records.Count != 0,
                    Reason = records.Count == 0 ? "No readable mapped MsQuic file identity captured" : "Public mapped file identity captured",
                    Records = records,
                    Failures = failures,
                    Inspected = inspected,
                    Truncated = truncated
                };
            }

            internal string Snapshot(Exception failure, Task? tcp, Task? quic)
            {
                // File/module observation is outside the callback storage gate.
                var nativeIdentity = ReadMappedNativeModuleIdentity();
                object snapshot;
                lock (_gate)
                    snapshot = new
                    {
                        NativeModuleIdentity = nativeIdentity,
                        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        ProcessId = Environment.ProcessId,
                        Case = TestContext.CurrentContext.Test.FullName,
                        CoreAssembly = typeof(WebServer).Assembly.GetName().FullName,
                        CoreModuleId = typeof(WebServer).Assembly.ManifestModule.ModuleVersionId,
                        QuicAssembly = typeof(QuicConnection).Assembly.GetName().FullName,
                        QuicModuleId = typeof(QuicConnection).Assembly.ManifestModule.ModuleVersionId,
                        ProviderObserved = Volatile.Read(ref _providerObserved) != 0,
                        Providers = _providers.ToArray(),
                        ProvidersDropped = _providersDropped,
                        EnableRequestsReturned = _enableRequestsReturned,
                        ProviderReportsEnabled = _providerReportsEnabled,
                        ProviderEventsReceived = Interlocked.Read(ref _providerEventsReceived),
                        ProviderEventsFiltered = 0,
                        ProviderEventsRetained = _info.Count + _errors.Count,
                        CaptureFailures = _captureFailures,
                        FormatFailures = _formatFailures,
                        FirstFormatFailure = _firstFormatFailure,
                        FallbackFailures = _fallbackFailures,
                        FirstCaptureFailure = _firstCaptureFailure,
                        Frequency = Stopwatch.Frequency,
                        Failure = failure.ToString(),
                        TaskRoles = _taskRoles,
                        TcpTask = tcp?.Status.ToString() ?? "not-started",
                        QuicTask = quic?.Status.ToString() ?? "not-started",
                        InfoDropped = _infoDropped,
                        ErrorDropped = _errorDropped,
                        PayloadsTruncated = _truncated,
                        PhasesDropped = _phaseDropped,
                        Events = _info.ToArray(),
                        Errors = _errors.ToArray(),
                        Phases = _phases.ToArray(),
                    };
                return System.Text.Json.JsonSerializer.Serialize(snapshot);
            }

            internal Exception? WriteFailure(Exception failure, Task? tcp, Task? quic)
            {
                // An existing failed request/assertion remains primary even if its
                // best-effort diagnostics incur a catchable secondary managed error.
                // Controlled formatting OOM remains observable; fatal stack/access faults escape.
                try { TestContext.Out.WriteLine(Snapshot(failure, tcp, quic)); }
                catch (Exception outputFailure) when (outputFailure is not StackOverflowException and not AccessViolationException)
                {
                    lock (_gate)
                    {
                        _formatFailures++;
                        _firstFormatFailure ??= outputFailure.GetType().FullName;
                    }
                    try { TestContext.Error.WriteLine($"Shared-port diagnostics degraded: {outputFailure.GetType().FullName}; original exception preserved."); }
                    catch (Exception fallbackFailure) when (fallbackFailure is not StackOverflowException and not AccessViolationException)
                    {
                        // All output can be unavailable. Retain degradation in the
                        // owned observer; never convert the original failure to success.
                        lock (_gate)
                        {
                            _fallbackFailures++;
                            _firstFormatFailure ??= fallbackFailure.GetType().FullName;
                        }
                    }
                    return outputFailure;
                }
                return null;
            }
        }
    }
}
