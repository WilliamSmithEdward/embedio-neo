using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicNativeDatagramTest
    {
        [Test]
        public async Task NativeHttp3ApplicationContextEchoesNegotiatedDatagrams()
        {
            var python = Environment.GetEnvironmentVariable("EMBEDIO_DATAGRAM_PEER_PYTHON");
            if (string.IsNullOrWhiteSpace(python))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_DATAGRAM_PEER") == "1") Assert.Fail("The independent peer is required.");
                Assert.Ignore("No independent aioquic peer was configured.");
            }
            using var certificate = HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable);
            using var listener = (IHttpListener)ConstructApplicationType("Http3Listener", certificate, true);
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
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in new[] { "-I", DriverPath(), "http-echo", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                var exchange = context.GetType().GetField("_exchange", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context)
                    ?? throw new AssertionException("Missing application exchange.");
                var channel = Get<object>(exchange, "Datagrams");
                var distinct = new HashSet<string>(StringComparer.Ordinal);
                while (await ApplicationReceive(channel, deadline.Token) is { } packet)
                {
                    using (packet)
                    {
                        var payload = Get<ReadOnlyMemory<byte>>(packet, "Payload");
                        distinct.Add(Convert.ToHexString(payload.Span));
                        Assert.That(Call(channel, "TrySend", payload).ToString(), Is.EqualTo("Queued"));
                    }
                }
                await exited;
                var lines = await output;
                TestContext.Out.WriteLine(lines);
                TestContext.Out.WriteLine(await errors);
                Assert.That(peer.ExitCode, Is.Zero, lines);
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
    }
}
