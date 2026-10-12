using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicNativeDatagramTest
    {
        [TestCase("echo")]
        [TestCase("unsupported")]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public async Task NativeOnlyDatagramPeerCompletesWithoutRuntimeQuicInitialization(string mode)
        {
            var python = Environment.GetEnvironmentVariable("EMBEDIO_DATAGRAM_PEER_PYTHON");
            if (string.IsNullOrWhiteSpace(python))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_DATAGRAM_PEER") == "1") Assert.Fail("The independent peer is required.");
                Assert.Ignore("No independent aioquic peer was configured.");
            }
            // QuicListener.IsSupported initializes System.Net.Quic's native
            // module reference and would mask standalone unloading failures.
            await NativeAioquicCore(python ?? throw new AssertionException("Missing peer."), mode);
        }
    }
}
