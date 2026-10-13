using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1IdleTlsWriteOwnershipTest
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false)]
        [TestCase(true)]
        public async Task DisposalSettlesActualBlockedPublicTlsShutdown(bool cancel)
        {
            var core = typeof(WebServer).Assembly;
            TestContext.Out.WriteLine($"Loaded core: {core.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName}; SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core.Location)))}");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var certificate = HttpsSmoke.CreateCertificate();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            var accepting = listener.AcceptSocketAsync(deadline.Token);
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using var socket = await accepting;
            using var blocked = new InterruptedWriteStream(new NetworkStream(socket, false), cancel);
            using var serverTls = new SslStream(blocked);
            using var clientTls = new SslStream(client.GetStream(), false, (_, remote, _, _) =>
                remote != null && remote.GetCertHashString() == certificate.GetCertHashString());
            var serverAuthentication = serverTls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate }, deadline.Token);
            var clientAuthentication = clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                { TargetHost = "localhost" }, deadline.Token);
            await Task.WhenAll(serverAuthentication, clientAuthentication);
            await serverTls.WriteAsync(new byte[] { 42 }, deadline.Token);
            var response = new byte[1];
            await clientTls.ReadExactlyAsync(response, deadline.Token);
            Assert.That(response[0], Is.EqualTo(42));

            blocked.BlockWrites = true;
            var writing = serverTls.ShutdownAsync();
            Assert.That(writing.IsCompleted, Is.False, "The actual public TLS shutdown must be suspended in its transport write.");
            var ownerType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.TcpSessionLifetime", true)
                ?? throw new AssertionException("Missing lifetime owner.");
            var owner = Activator.CreateInstance(ownerType, Hidden, null,
                new object[] { new object(), socket, serverTls, (Action)(() => { }) }, null)
                ?? throw new AssertionException("Missing owner.");
            Assert.That(ownerType.GetMethod("TryRetire", Hidden)?.CreateDelegate<Func<bool, bool>>(owner)(false), Is.True);
            var completion = ownerType.GetProperty("Completion", Hidden)?.GetValue(owner) as Task
                ?? throw new AssertionException("Missing completion.");
            var detached = 0;
            var finish = ownerType.GetMethod("FinishRetirementAfterWriteAsync", Hidden)
                ?.CreateDelegate<Func<Task, IDisposable?, Action, Task>>(owner)(writing, null, () =>
                {
                    Assert.That(writing.IsCompleted, Is.True, "Actual TLS completion must precede listener detach.");
                    detached++;
                }) ?? throw new AssertionException("Missing pending-write retirement.");
            try
            {
                await finish.WaitAsync(deadline.Token);
                await completion.WaitAsync(deadline.Token);
                Assert.Multiple(() =>
                {
                    Assert.That(writing.IsCompleted, Is.True);
                    Assert.That(writing.IsCompletedSuccessfully, Is.False, "Retirement interrupted the blocked alert rather than claiming successful send.");
                    Assert.That(blocked.Disposals, Is.EqualTo(1));
                    Assert.That(detached, Is.EqualTo(1));
                    Assert.That(ownerType.GetProperty("CloseFinished", Hidden)?.GetValue(owner), Is.EqualTo(true));
                });
            }
            finally
            {
                blocked.Interrupt();
                await finish.WaitAsync(deadline.Token);
                try { await writing.WaitAsync(deadline.Token); }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
                { TestContext.Out.WriteLine("Observed actual interrupted TLS task: " + error.GetType().Name); }
            }
        }

        private sealed class InterruptedWriteStream(Stream inner, bool cancel) : Stream
        {
            private readonly TaskCompletionSource<bool> _interruption = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool BlockWrites { get; set; }
            internal int Disposals { get; private set; }
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => inner.ReadAsync(buffer, offset, count, token);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
                => inner.ReadAsync(buffer, token);
            public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
                => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                if (BlockWrites)
                {
                    await _interruption.Task.WaitAsync(token);
                    if (cancel) throw new OperationCanceledException("Controlled transport disposal.");
                    throw new IOException("Controlled blocked TLS transport disposal.");
                }
                await inner.WriteAsync(buffer, token);
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            internal void Interrupt()
            {
                _interruption.TrySetResult(true);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing && Disposals == 0)
                {
                    Disposals++;
                    Interrupt();
                    inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
