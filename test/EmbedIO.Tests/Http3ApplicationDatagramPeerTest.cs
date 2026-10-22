using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // HTTP datagrams (RFC 9297) between an application context and the independent
    // aioquic peer, through the HTTP/3 listener and the EmbedIO.Quic engine.
    public sealed class Http3ApplicationDatagramPeerTest
    {
        [Test]
        public async Task Http3ApplicationContextEchoesNegotiatedDatagrams()
        {
            var python = Environment.GetEnvironmentVariable("EMBEDIO_DATAGRAM_PEER_PYTHON");
            if (string.IsNullOrWhiteSpace(python))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_DATAGRAM_PEER") == "1") Assert.Fail("The independent peer is required.");
                Assert.Ignore("No independent aioquic peer was configured.");
            }
            if (EngineInternals.Find("EmbedIO.Net.Internal.Http3.Http3Listener") == null)
            { Assert.Ignore("This build of the library has no HTTP/3 listener."); return; }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = (IHttpListener)EngineInternals.Create("EmbedIO.Net.Internal.Http3.Http3Listener", certificate);
            var port = new Uri(Resources.GetServerAddress()).Port;
            listener.AddPrefix("https://localhost:" + port + "/");
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var certificatePath = Path.GetTempFileName();
            Process? peer = null;
            try
            {
                await File.WriteAllTextAsync(certificatePath, certificate.ExportCertificatePem(), deadline.Token);
                var start = new ProcessStartInfo(python ?? throw new AssertionException("Missing independent peer."))
                { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
                foreach (var argument in new[] { "-I", DriverPath(), "http-echo", "--defer-http-settings", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--cafile", certificatePath, "--sha256", certificate.GetCertHashString(HashAlgorithmName.SHA256) })
                    start.ArgumentList.Add(argument);
                peer = Process.Start(start) ?? throw new AssertionException("The independent application peer did not start.");
                var output = peer.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = peer.StandardError.ReadToEndAsync(deadline.Token);
                var pending = listener.GetContextAsync(deadline.Token);
                var exited = peer.WaitForExitAsync(deadline.Token);
                if (await Task.WhenAny(pending, exited) == exited)
                    Assert.Fail("Independent peer ended before dispatch: " + await output + await errors);
                var context = await pending;
                Assert.That(context.Request.ProtocolVersion, Is.EqualTo(System.Net.HttpVersion.Version30));
                Assert.That(context.Request.Headers["capsule-protocol"], Is.EqualTo("?1"));
                using var tunnel = await ((IHttpTunnelContext)context).AcceptTunnelAsync("neo-datagram-test", true, deadline.Token);
                var channel = tunnel.Datagrams ?? throw new AssertionException("Missing public datagram channel.");
                Assert.That(channel.CanSend, Is.False, "The peer has not sent HTTP/3 SETTINGS yet.");
                await peer.StandardInput.WriteLineAsync("settings");
                await peer.StandardInput.FlushAsync(deadline.Token);
                while (!channel.CanSend)
                {
                    if (exited.IsCompleted) Assert.Fail("Independent peer ended before datagram negotiation: " + await output + await errors);
                    await Task.Delay(1, deadline.Token);
                }
                Assert.That(channel.CanSend, Is.True);
                Assert.That(channel.MaximumPayloadLength, Is.GreaterThanOrEqualTo(1000));
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                while (await channel.ReceiveAsync(deadline.Token) is { } packet)
                {
                    using (packet)
                    {
                        var payload = packet.Payload;
                        distinct.Add(Convert.ToHexString(payload.Span));
                        Assert.That(channel.TrySend(payload), Is.EqualTo(HttpDatagramSendStatus.Queued));
                    }
                }
                await exited;
                var lines = await output;
                TestContext.Out.WriteLine(lines);
                TestContext.Out.WriteLine(await errors);
                Assert.That(peer.ExitCode, Is.Zero, lines);
                Assert.That(lines, Does.Contain("\"event\": \"http-settings-released\""));
                Assert.That(lines, Does.Contain("\"event\": \"http-echo-complete\""));
                Assert.That(distinct.Count, Is.EqualTo(7));
                context.Close();
            }
            finally
            {
                if (peer != null)
                {
                    try { if (!peer.HasExited) { peer.Kill(true); await peer.WaitForExitAsync(); } }
                    finally { peer.Dispose(); }
                }
                listener.Stop();
                File.Delete(certificatePath);
            }
        }

        private static string DriverPath()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "test", "EmbedIO.Conformance", "drivers", "quic_datagram_peer.py");
                if (File.Exists(candidate)) return candidate;
            }
            throw new AssertionException("Missing test/EmbedIO.Conformance/drivers/quic_datagram_peer.py.");
        }
    }
}
