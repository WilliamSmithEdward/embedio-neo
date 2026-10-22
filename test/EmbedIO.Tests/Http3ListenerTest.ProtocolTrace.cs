using System;
using System.Collections.Generic;
using System.Diagnostics;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private sealed class ListenerAcceptTrace : TraceListener
        {
            private readonly object _gate = new();
            private readonly List<string> _records = [];
            private bool _enabled;

            internal ListenerAcceptTrace() => Diagnostics.Log.Source.Listeners.Add(this);

            internal void Select(bool enabled)
            {
                lock (_gate)
                {
                    _records.Clear();
                    _enabled = enabled;
                }
            }

            internal string[] Snapshot()
            {
                lock (_gate) return [.. _records];
            }

            public override void Write(string? message) { }

            public override void WriteLine(string? message)
            {
                if (message == null || !message.Contains("HTTP/3 accept loop failed.", StringComparison.Ordinal))
                    return;

                lock (_gate)
                {
                    if (_enabled && _records.Count < 4)
                        _records.Add(message[..Math.Min(message.Length, 4096)]);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Diagnostics.Log.Source.Listeners.Remove(this);

                base.Dispose(disposing);
            }
        }

        private static QuicProtocolFailureTrace? _uploadProtocolTrace;
        private static ListenerAcceptTrace? _listenerAcceptTrace;

        [Test]
        public void ListenerAcceptTraceKeepsOnlyBoundedAcceptFailures()
        {
            using var trace = new ListenerAcceptTrace();
            trace.Select(true);
            var source = Diagnostics.Log.Source;
            var previous = source.Switch.Level;

            try
            {
                source.Switch.Level = SourceLevels.Error;
                source.TraceEvent(TraceEventType.Error, 0, "Unrelated failure.");

                for (var i = 0; i < 6; i++)
                    source.TraceEvent(TraceEventType.Error, 0, "HTTP/3 accept loop failed. marker={0}", i);

                var records = trace.Snapshot();

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(records, Has.Length.EqualTo(4));
                    Assert.That(records[0], Does.Contain("marker=0"));
                    Assert.That(records[3], Does.Contain("marker=3"));
                }
            }

            finally
            {
                source.Switch.Level = previous;
            }
        }

        [OneTimeSetUp]
        public static void InstallQuicFailureObservation()
        {
            _uploadProtocolTrace = QuicProtocolFailureTrace.Start();
            _listenerAcceptTrace = new ListenerAcceptTrace();
        }

        [SetUp]
        public static void SelectQuicFailureObservation()
        {
            _uploadProtocolTrace?.Select(
                TestContext.CurrentContext.Test.MethodName == nameof(WebServerModulesEchoConcurrentHttp3Uploads)
            );
            _listenerAcceptTrace?.Select(true);
        }

        [TearDown]
        public static void ReportQuicFailure()
        {
            if (TestContext.CurrentContext.Result.Outcome.Status != TestStatus.Failed)
            {
                _listenerAcceptTrace?.Select(false);
                return;
            }

            var listenerRecords = _listenerAcceptTrace?.Snapshot() ?? [];
            _listenerAcceptTrace?.Select(false);
            TestContext.Error.WriteLine("HTTP/3 accept-loop error records: " + listenerRecords.Length);

            foreach (var entry in listenerRecords) TestContext.Error.WriteLine(entry);

            if (TestContext.CurrentContext.Test.MethodName != nameof(WebServerModulesEchoConcurrentHttp3Uploads))
                return;

            var trace = _uploadProtocolTrace;
            var snapshot = trace?.Snapshot() ?? [];
            TestContext.Error.WriteLine("Caught QUIC protocol exception records: " + snapshot.Length);
            TestContext.Error.WriteLine(
                "QUIC exception observation incomplete: " + (trace == null || trace.CaptureFailed)
            );

            foreach (var entry in snapshot) TestContext.Error.WriteLine(entry);
        }

        [OneTimeTearDown]
        public static void RemoveUploadProtocolObservation()
        {
            _uploadProtocolTrace?.Dispose();
            _uploadProtocolTrace = null;
            _listenerAcceptTrace?.Dispose();
            _listenerAcceptTrace = null;
        }
    }
}
