using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class MsQuicNativeDatagramTest
    {
        [TestCase(0)]
        [TestCase(1024)]
        [TestCase(1048576)]
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        public async Task NativeOnlyStreamPeerPreservesBytesFinAndOwnerRetirement(int length)
        {
            var python = Environment.GetEnvironmentVariable("EMBEDIO_DATAGRAM_PEER_PYTHON");
            if (string.IsNullOrWhiteSpace(python))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_DATAGRAM_PEER") == "1") Assert.Fail("The independent peer is required.");
                Assert.Ignore("No independent aioquic peer was configured.");
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var server = await NativeServer.StartAsync(true);
            var certificate = Path.GetTempFileName();
            Process? process = null;
            try
            {
                await File.WriteAllTextAsync(certificate, server.Certificate.ExportCertificatePem(), deadline.Token);
                var driver = Path.Combine(Path.GetDirectoryName(DriverPath()) ?? throw new AssertionException("Missing driver directory."),
                    "quic_stream_lifetime_peer.py");
                var start = new ProcessStartInfo(python ?? throw new AssertionException("Missing peer."))
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in new[] { "-I", driver, "--port", server.Endpoint.Port.ToString(CultureInfo.InvariantCulture),
                    "--length", length.ToString(CultureInfo.InvariantCulture), "--cafile", certificate,
                    "--sha256", server.Certificate.GetCertHashString(HashAlgorithmName.SHA256) }) start.ArgumentList.Add(argument);
                process = Process.Start(start) ?? throw new AssertionException("The stream peer did not start.");
                var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
                var errors = process.StandardError.ReadToEndAsync(deadline.Token);
                using var connection = await server.AcceptAsync(deadline.Token);
                Call(connection, "EnableStreamAcceptance");
                Call(connection, "Configure", server.Configuration);
                await Connected(connection).WaitAsync(deadline.Token);
                var accepting = (Task)Call(connection, "AcceptStreamAsync", deadline.Token);
                await accepting.WaitAsync(deadline.Token);
                using var stream = (SafeHandle)(TaskResult(accepting) ?? throw new AssertionException("Missing native stream."));
                using var received = new MemoryStream();
                var buffer = new byte[8192];
                while (true)
                {
                    var count = await (ValueTask<int>)Call(stream, "ReadAsync", buffer.AsMemory(), deadline.Token);
                    if (count == 0) break;
                    received.Write(buffer, 0, count);
                    Assert.That(received.Length, Is.LessThanOrEqualTo(length));
                }
                var bytes = received.ToArray();
                Assert.That(bytes.Length, Is.EqualTo(length));
                var expected = new byte[length];
                for (var index = 0; index < expected.Length; index++) expected[index] = (byte)((index * 7) & 255);
                Assert.That(bytes.AsSpan().SequenceEqual(expected), Is.True);
                await (ValueTask)Call(stream, "WriteAsync", new ReadOnlyMemory<byte>(bytes), true, deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                var lines = await output;
                var stderr = await errors;
                TestContext.Out.WriteLine(lines);
                TestContext.Out.WriteLine(stderr);
                Assert.That(process.ExitCode, Is.Zero, lines + stderr);
                Assert.That(lines, Does.Contain("stream-complete"));
                stream.Dispose();
                connection.Dispose();
                Assert.That(stream.IsClosed, Is.True);
                Assert.That(connection.IsClosed, Is.True);
            }
            finally
            {
                if (process != null)
                {
                    try { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                    finally { process.Dispose(); }
                }
                File.Delete(certificate);
                await server.DisposeAsync();
            }
            Assert.That(server.OwnersClosed, Is.True);
        }
    }
}
