using EmbedIO.Net.Internal.Http3;
using EmbedIO.Net.Internal.WebTransport;

namespace EmbedIO.Quic
{
    internal static class WebTransportNegotiationTests
    {
        internal static void Run()
        {
            foreach (var hex in new[] { "08023301ac7cf00001", "3302ac7cf00001", "3301ac7cf00002" })
            {
                var rejected = false;

                try
                {
                    WebTransportSettings.Parse(Convert.FromHexString(hex));
                }

                catch (Http3ProtocolException error) when (error.ErrorCode == 0x109)
                {
                    rejected = true;
                }

                Check(rejected, "invalid boolean SETTINGS remain connection errors");
            }

            // Draft 16 section 3.1 distinguishes the client's SETTINGS from the
            // server's advertisement of extended CONNECT support.
            (
                string Hex,
                bool Valid
            )[] cases =
            [
                ("3301ac7cf00001", true),
                ("08003301ac7cf00001", true),
                ("08013301ac7cf00001", true),
                ("0801ac7cf00001", false),
                ("3300ac7cf00001", false),
                ("08013301", false),
                ("3301ac7cf00000", false),
                ("", false)
            ];

            foreach (var (hex, valid) in cases)
            {
                var peer = WebTransportSettings.Parse(Convert.FromHexString(hex));
                Check(peer.SettingsRequirementsMet == valid, "client SETTINGS requirements");

                foreach (var enabled in new[] { false, true })
                {
                    foreach (var transportParametersMet in new[] { false, true })
                    {
                        var registry = new WebTransportSessionRegistry(
                            WebTransportSettings.Local(enabled, 0, 0, 0),
                            peer,
                            transportParametersMet,
                            new WebTransportRegistryLimits(),
                            static (_, _) => { },
                            static (_, _, _, _) => { }
                        );
                        var expected = valid && enabled && transportParametersMet;
                        var admission = registry.TryCreateSession(0, out var session);
                        Check(registry.Negotiated == expected, "complete negotiation requirements");
                        Check(
                            admission
                                == (expected ? WebTransportAdmission.Accepted : WebTransportAdmission.NotNegotiated),
                            "session admission"
                        );
                        Check((session != null) == expected, "session creation follows admission");
                        Check(registry.ActiveSessionCount == (expected ? 1 : 0), "active session accounting");
                        registry.Shutdown();
                        Check(registry.ActiveSessionCount == 0, "registry shutdown");
                    }
                }
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("WebTransport negotiation: " + message);
        }
    }
}
