using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        private sealed class PausedBody : Stream
        {
            internal readonly TaskCompletionSource Entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            internal readonly TaskCompletionSource Release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            private bool _sent;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken
            ) => ReadBodyAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default
            ) => ReadBodyAsync(buffer, cancellationToken);

            private async ValueTask<int> ReadBodyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (buffer.Length == 0) return 0;

                if (!_sent)
                {
                    _sent = true;
                    buffer.Span[0] = (byte)'a';
                    return 1;
                }

                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                return 0;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override void Flush() => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        [Test]
        public async Task CancellationRollsBackWhileOverlappingWritersAndDeletionAreRejected()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client, 1);
            using var body = new PausedBody();
            using var cancel = new CancellationTokenSource();
            using var request = Request("PATCH", path);
            request.Content = new StreamContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            request.Headers.Add("Upload-Offset", "0");
            var pending = host.Server.Client.SendAsync(request, cancel.Token);

            try
            {
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var conflict = await Patch(host.Server.Client, path, 0, "b");
                Assert.That(conflict.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                using var deletion = Request("DELETE", path);
                using var busy = await host.Server.Client.SendAsync(deletion);
                Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                using var head = Request("HEAD", path);
                using var progress = await host.Server.Client.SendAsync(head);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(Header(progress, "Upload-Offset"), Is.EqualTo("0"));
                    Assert.That(store.RemoveExpired(), Is.Zero);
                }
            }

            finally
            {
                cancel.Cancel();
                body.Release.TrySetResult();
            }

            await Assert.ThatAsync(
                async () =>
                {
                    using var response = await pending;
                },
                Throws.InstanceOf<OperationCanceledException>()
            );
            using var retry = await Patch(host.Server.Client, path, 0, "b");
            Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var output = new MemoryStream();
            await store.CopyCompletedToAsync(
                path
                    .Split('/')
                    .Last(),
                "alice",
                output
            );
            Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo("b"));
        }

        [Test]
        public async Task DisposingStoreRetainsDirectoryOwnershipUntilActiveIoFinishes()
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path);
            using var store = new DiskTusUploadStore(options);
            await using var host = new Host(store);
            var path = await Create(host.Server.Client, 1);
            using var body = new PausedBody();
            using var request = Request("PATCH", path);
            request.Content = new StreamContent(body);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            request.Headers.Add("Upload-Offset", "0");
            var pending = host.Server.Client.SendAsync(request);

            try
            {
                await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                store.Dispose();
                Assert.That(() => new DiskTusUploadStore(options), Throws.InstanceOf<IOException>());
                Assert.That(() => store.GetUploads("alice"), Throws.TypeOf<ObjectDisposedException>());
            }

            finally
            {
                body.Release.TrySetResult();
            }

            using var response = await pending;
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var reopened = new DiskTusUploadStore(options);
            Assert.That(
                reopened
                    .GetUploads("alice")
                    .Single().Offset,
                Is.EqualTo(1)
            );
        }

        [Test]
        public async Task UnknownLengthOversizePatchRollsBack()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path, patch: 2));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            using var request = Request("PATCH", path);
            request.Content = new StreamContent(new NonSeekableBody());
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            request.Headers.Add("Upload-Offset", "0");
            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(413));
                Assert.That(
                    store
                        .GetUploads("alice")
                        .Single().Offset,
                    Is.Zero
                );
            }
        }

        private sealed class NonSeekableBody : MemoryStream
        {
            internal NonSeekableBody() : base(Encoding.ASCII.GetBytes("abc")) { }

            public override bool CanSeek => false;
        }

        [Test]
        public async Task MetadataWriteFailureRequiresReopenAndRecoveryKeepsCommittedOffset()
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path);
            string path;

            using (var store = new DiskTusUploadStore(options))
            {
                await using var host = new Host(store);
                path = await Create(host.Server.Client);
                var id = path
                    .Split('/')
                    .Last();
                File.WriteAllText(Path.Combine(directory.Path, id + ".next"), "interrupted journal");
                using var failed = await Patch(host.Server.Client, path, 0, "abc");
                Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                Assert.That(() => store.GetUploads("alice"), Throws.InstanceOf<IOException>());
            }

            using var recovered = new DiskTusUploadStore(options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    recovered
                        .GetUploads("alice")
                        .Single().Offset,
                    Is.Zero
                );
                Assert.That(
                    new FileInfo(
                        Path.Combine(
                            directory.Path,
                            path
                                .Split('/')
                                .Last()
                                + ".data"
                        )
                    ).Length,
                    Is.Zero
                );
            }
        }
    }
}
