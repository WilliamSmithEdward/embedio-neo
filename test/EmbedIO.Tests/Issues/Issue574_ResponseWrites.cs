using System;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue574_ResponseWrites
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task AsyncWritesUseTransportAsyncIoAndPreserveFraming(bool chunked)
        {
            using var fixture = await Fixture.Create(chunked);
            var first = Encoding.ASCII.GetBytes("FIRST");
            await fixture.Stream.WriteAsync(first, 1, 3);
            await fixture.Stream.WriteAsync(Array.Empty<byte>(), 0, 0);
            await fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("END"));
            Assert.That(fixture.Transport.SynchronousWrites, Is.Zero, "Async response writes must not occupy a worker with synchronous transport I/O.");
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            var body = wire[(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
            Assert.That(body, Is.EqualTo(chunked ? "3\r\nIRS\r\n3\r\nEND\r\n0\r\n\r\n" : "IRSEND"));
        }

        [Test]
        public async Task BackpressuredWriteRemainsAsyncAndHonorsMidWriteCancellation()
        {
            using var fixture = await Fixture.Create(false, ignoreErrors: true);
            fixture.Transport.BlockWrites = true;
            using var cancel = new CancellationTokenSource();
            var pending = fixture.Stream.WriteAsync(new byte[1024 * 1024], 0, 1024 * 1024, cancel.Token);
            // A synchronous fallback completes/faults without ever entering transport async I/O.
            Assert.That(fixture.Transport.AsyncEntered.Task.IsCompleted, Is.True);
            Assert.That(pending.IsCompleted, Is.False);
            cancel.Cancel();
            var error = await Assert.CatchAsync<OperationCanceledException>(async () => await pending);
            Assert.That(error.CancellationToken, Is.EqualTo(cancel.Token));
            Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task OverlappingAsyncWritesStayOrderedAndQueuedCancellationSendsNoBytes(bool ignoreErrors)
        {
            using var fixture = await Fixture.Create(true, ignoreErrors);
            fixture.Transport.BlockWrites = true;
            var first = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("AAA"), 0, 3);
            var second = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("BBB"), 0, 3);
            using var cancel = new CancellationTokenSource();
            var canceled = fixture.Stream.WriteAsync(Encoding.ASCII.GetBytes("CCC"), 0, 3, cancel.Token);
            try
            {
                Assert.That(fixture.Transport.AsyncWrites, Is.EqualTo(1), "The base Stream fallback previously serialized async writes; keep that ordering.");
                cancel.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(async () => await canceled);
            }
            finally
            {
                cancel.Cancel();
                fixture.Transport.ReleaseWrites.TrySetResult();
                await Task.WhenAll(first, second);
                fixture.Transport.BlockWrites = false;
            }
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.EndWith("\r\n\r\n3\r\nAAA\r\n3\r\nBBB\r\n0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AsyncTransportErrorsRespectIgnoreWriteExceptions(bool ignoreErrors)
        {
            using var fixture = await Fixture.Create(false, ignoreErrors);
            fixture.Transport.Error = new IOException("controlled transport failure");
            var writing = fixture.Stream.WriteAsync(new byte[8], 0, 8);
            if (ignoreErrors) await writing;
            else await Assert.ThrowsAsync<IOException>(async () => await writing);
            Assert.That(fixture.Transport.AsyncWrites, Is.GreaterThan(0));
            Assert.That(fixture.Transport.SynchronousWrites, Is.Zero);
        }

        [Test]
        public async Task PreCanceledWriteAndFlushDoNotCommitHeaders()
        {
            using var fixture = await Fixture.Create(false);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await Assert.CatchAsync<OperationCanceledException>(async () => await fixture.Stream.WriteAsync(new byte[1], 0, 1, cancel.Token));
            await Assert.CatchAsync<OperationCanceledException>(async () => await fixture.Stream.FlushAsync(cancel.Token));
            Assert.That(fixture.Transport.Length, Is.Zero);
            fixture.Response.Headers.Set("X-Still-Mutable", "yes");
            await fixture.Stream.WriteAsync(new byte[1], 0, 1);
            Assert.That(Encoding.ASCII.GetString(fixture.Transport.ToArray()), Does.Contain("X-Still-Mutable: yes"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task EmptyFirstWriteDoesNotTerminateChunkedBody(bool asynchronous)
        {
            using var fixture = await Fixture.Create(true);
            if (asynchronous)
            {
                await fixture.Stream.WriteAsync(Array.Empty<byte>(), 0, 0);
                await fixture.Stream.WriteAsync(new byte[] { 65 }, 0, 1);
            }
            else
            {
                fixture.Stream.Write(Array.Empty<byte>(), 0, 0);
                fixture.Stream.Write(new byte[] { 65 }, 0, 1);
            }
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            var body = wire[(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
            Assert.That(body, Is.EqualTo("1\r\nA\r\n0\r\n\r\n"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task LargeHeadersDoNotProduceNegativeBodyPrefixLength(bool asynchronous)
        {
            using var fixture = await Fixture.Create(false);
            fixture.Response.Headers.Set("X-Large", new string('x', 17000));
            var payload = Encoding.ASCII.GetBytes("ABCDEF");
            if (asynchronous) await fixture.Stream.WriteAsync(payload, 0, payload.Length);
            else fixture.Stream.Write(payload, 0, payload.Length);
            fixture.Stream.Dispose();
            var wire = Encoding.ASCII.GetString(fixture.Transport.ToArray());
            Assert.That(wire, Does.EndWith("\r\n\r\nABCDEF"));
        }

        [Test]
        public async Task InvalidBuffersDoNotCommitHeadersAndDisposedWritesAreRejected()
        {
            using var fixture = await Fixture.Create(false);
            Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Func<byte[], int, int, Task>)fixture.Stream.WriteAsync, null, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Stream.WriteAsync(new byte[1], -1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Stream.WriteAsync(new byte[1], 0, -1));
            Assert.Throws<ArgumentException>(() => fixture.Stream.WriteAsync(new byte[1], int.MaxValue, 1));
            Assert.That(fixture.Transport.Length, Is.Zero);
            fixture.Stream.Dispose();
            Assert.Throws<ObjectDisposedException>(() => fixture.Stream.WriteAsync(new byte[1], 0, 1));
        }

        internal sealed class Fixture : IDisposable
        {
            private readonly Net.HttpListener _listener;
            private readonly TcpClient _client;
            public IHttpResponse Response { get; }
            public RecordingTransport Transport { get; } = new();
            public Stream Stream { get; }
            private Fixture(Net.HttpListener listener, TcpClient client, IHttpResponse response, bool ignoreErrors)
            {
                _listener = listener;
                _client = client;
                Response = response;
                var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ResponseStream");
                Stream = (Stream)(Activator.CreateInstance((type ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { Transport, response, ignoreErrors }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            }
            public static async Task<Fixture> Create(bool chunked, bool ignoreErrors = false)
            {
                var url = Resources.GetServerAddress();
                var listener = new Net.HttpListener();
                listener.AddPrefix(url);
                listener.Start();
                var client = new TcpClient();
                try
                {
                    var uri = new Uri(url);
                    await client.ConnectAsync(uri.Host, uri.Port);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"));
                    var context = await listener.GetContextAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
                    context.Response.SendChunked = chunked;
                    if (!chunked) context.Response.ContentLength64 = 6;
                    return new Fixture(listener, client, context.Response, ignoreErrors);
                }
                catch { client.Dispose(); listener.Dispose(); throw; }
            }
            public void Dispose()
            {
                Transport.BlockWrites = false;
                Transport.ReleaseWrites.TrySetResult();
                Stream.Dispose();
                _client.Dispose();
                _listener.Dispose();
                Transport.Dispose();
            }
        }

        internal sealed class RecordingTransport : MemoryStream
        {
            public int SynchronousWrites { get; private set; }
            public int AsyncWrites { get; private set; }
            public bool BlockWrites { get; set; }
            public Exception? Error { get; set; }
            public TaskCompletionSource AsyncEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ReleaseWrites { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override void Write(byte[] buffer, int offset, int count)
            {
                SynchronousWrites++;
                // Fail immediately rather than risking a hung worker in the old fallback.
                if (BlockWrites) throw new IOException("Synchronous I/O attempted under backpressure");
                if (Error != null) throw Error;
                base.Write(buffer, offset, count);
            }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                AsyncWrites++;
                AsyncEntered.TrySetResult();
                if (BlockWrites) await ReleaseWrites.Task.WaitAsync(cancellationToken);
                if (Error != null) throw Error;
                cancellationToken.ThrowIfCancellationRequested();
                base.Write(buffer, offset, count);
            }
        }
    }
}
