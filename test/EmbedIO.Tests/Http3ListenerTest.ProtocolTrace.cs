using EmbedIO.Tests.TestObjects;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        private static QuicProtocolFailureTrace? _uploadProtocolTrace;

        [OneTimeSetUp]
        public static void InstallUploadProtocolObservation() =>
            _uploadProtocolTrace = QuicProtocolFailureTrace.Start();

        [SetUp]
        public static void SelectUploadProtocolObservation() =>
            _uploadProtocolTrace?.Select(
                TestContext.CurrentContext.Test.MethodName == nameof(WebServerModulesEchoConcurrentHttp3Uploads)
            );

        [TearDown]
        public static void ReportUploadProtocolFailure()
        {
            if (
                TestContext.CurrentContext.Test.MethodName != nameof(WebServerModulesEchoConcurrentHttp3Uploads)
                || TestContext.CurrentContext.Result.Outcome.Status != TestStatus.Failed
            ) return;

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
        }
    }
}
