using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        private static readonly string[] LockFileNames = ["store.lock"];

        [TestCase(false)]
        [TestCase(true)]
        public async Task ReopeningTruncatesUncommittedTailAndResumes(bool pendingRecord)
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path);
            string path;

            using (var first = new DiskTusUploadStore(options))
            {
                await using var host = new Host(first);
                path = await Create(host.Server.Client);
                using var response = await Patch(host.Server.Client, path, 0, "abc");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            }

            var id = path
                .Split('/')
                .Last();

            using (var data = new FileStream(Path.Combine(directory.Path, id + ".data"), FileMode.Append))
            {
                data.Write(Encoding.ASCII.GetBytes("uncommitted"));
                data.Flush(true);
            }

            if (pendingRecord)
                File.Copy(Path.Combine(directory.Path, id + ".info"), Path.Combine(directory.Path, id + ".next"));

            using var recovered = new DiskTusUploadStore(options);
            await using var second = new Host(recovered);
            using var head = await second.Server.Client.SendAsync(Request("HEAD", path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Header(head, "Upload-Offset"), Is.EqualTo("3"));
                Assert.That(new FileInfo(Path.Combine(directory.Path, id + ".data")).Length, Is.EqualTo(3));
            }

            using var resumed = await Patch(second.Server.Client, path, 3, "def");
            Assert.That(resumed.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var bytes = new MemoryStream();
            await recovered.CopyCompletedToAsync(id, "alice", bytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Encoding.UTF8.GetString(bytes.ToArray()), Is.EqualTo("abcdef"));
                Assert.That(File.Exists(Path.Combine(directory.Path, id + ".next")), Is.False);
            }
        }

        [Test]
        public async Task CompleteUploadsRemainOwnerScopedAfterRestartWithoutRepeatingNotification()
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path);
            string path;
            var notifications = 0;

            using (var store = new DiskTusUploadStore(options))
            {
                await using var host = new Host(store, (_, _) =>
                {
                    notifications++;
                    return Task.CompletedTask;
                });
                path = await Create(host.Server.Client, 0);
            }

            using var recovered = new DiskTusUploadStore(options);
            await using var second = new Host(recovered, (_, _) =>
            {
                notifications++;
                return Task.CompletedTask;
            });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    recovered
                        .GetUploads("alice")
                        .Single().IsComplete,
                    Is.True
                );
                Assert.That(recovered.GetUploads("bob"), Is.Empty);
            }

            using var response = await second.Server.Client.SendAsync(Request("HEAD", path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Header(response, "Upload-Offset"), Is.EqualTo("0"));
                Assert.That(notifications, Is.EqualTo(1));
            }
        }

        [TestCase("truncated-record")]
        [TestCase("short-data")]
        [TestCase("missing-data")]
        [TestCase("foreign-file")]
        [TestCase("smaller-budget")]
        public async Task InvalidRecoveryFailsClosedAndReleasesDirectoryLock(string mode)
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path);
            string id;

            using (var store = new DiskTusUploadStore(options))
            {
                await using var host = new Host(store);
                var path = await Create(host.Server.Client);
                id = path
                    .Split('/')
                    .Last();
                using var part = await Patch(host.Server.Client, path, 0, "abc");
            }

            if (mode == "truncated-record")
                File.WriteAllBytes(Path.Combine(directory.Path, id + ".info"), [1]);

            if (mode == "short-data") File.WriteAllBytes(Path.Combine(directory.Path, id + ".data"), [1]);

            if (mode == "missing-data") File.Delete(Path.Combine(directory.Path, id + ".data"));

            if (mode == "foreign-file")
                File.WriteAllText(Path.Combine(directory.Path, "application.txt"), "must not delete");

            var recoveryOptions = mode == "smaller-budget" ? Options(directory.Path, storage: 4, upload: 4) : options;

            if (mode == "short-data" || mode == "smaller-budget")
                Assert.That(() => new DiskTusUploadStore(recoveryOptions), Throws.TypeOf<InvalidDataException>());

            else
                Assert.That(() => new DiskTusUploadStore(recoveryOptions), Throws.InstanceOf<IOException>());

            using var ownership = new FileStream(
                Path.Combine(directory.Path, "store.lock"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

            if (mode == "foreign-file")
                Assert.That(
                    File.ReadAllText(Path.Combine(directory.Path, "application.txt")),
                    Is.EqualTo("must not delete")
                );
        }

        [Test]
        public void UncommittedCreationArtifactsAreReclaimed()
        {
            using var directory = new DirectoryScope();
            Directory.CreateDirectory(directory.Path);
            var id = Guid
                .NewGuid()
                .ToString("N");
            File.WriteAllText(Path.Combine(directory.Path, id + ".data"), "partial");
            File.WriteAllText(Path.Combine(directory.Path, id + ".next"), "partial");
            using var store = new DiskTusUploadStore(Options(directory.Path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(store.Count, Is.Zero);
                Assert.That(
                    Directory
                        .GetFiles(directory.Path)
                        .Select(Path.GetFileName),
                    Is.EqualTo(LockFileNames)
                );
            }
        }

        [Test]
        public void DirectoryCannotBeOwnedByTwoStores()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            Assert.That(() => new DiskTusUploadStore(Options(directory.Path)), Throws.InstanceOf<IOException>());
            store.Dispose();
            using var next = new DiskTusUploadStore(Options(directory.Path));
            Assert.That(next.Count, Is.Zero);
        }

        [Test]
        public async Task FixedExpiryReclaimsCompleteAndPartialUploadsAcrossRestart()
        {
            using var directory = new DirectoryScope();
            var options = Options(directory.Path, lifetime: TimeSpan.FromMilliseconds(150));

            using (var store = new DiskTusUploadStore(options))
            {
                await using var host = new Host(store);
                await Create(host.Server.Client, 0);
                var path = await Create(host.Server.Client);
                await Task.Delay(200);
                using var response = await host.Server.Client.SendAsync(Request("HEAD", path));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            }

            using var recovered = new DiskTusUploadStore(options);
            Assert.That(recovered.Count, Is.Zero);
            Assert.That(recovered.ReservedBytes, Is.Zero);
        }

        [TestCase("upload")]
        [TestCase("storage")]
        [TestCase("count")]
        [TestCase("patch")]
        [TestCase("lifetime")]
        [TestCase("concurrent")]
        public void InvalidBudgetsRejected(string option)
        {
            using var directory = new DirectoryScope();
            Assert.That(
                () => new TusUploadOptions(
                    directory.Path,
                    maximumUploadBytes: option == "upload" ? -1 : 1,
                    maximumStorageBytes: option == "storage" ? 0 : 1,
                    maximumUploads: option == "count" ? 0 : 1,
                    maximumPatchBytes: option == "patch" ? 0 : 1,
                    lifetime: option == "lifetime" ? TimeSpan.Zero : TimeSpan.FromSeconds(1),
                    maximumConcurrentRequests: option == "concurrent" ? 0 : 1
                ),
                Throws.InstanceOf<ArgumentOutOfRangeException>()
            );
        }

        [Test]
        public async Task IncompleteCopiesAndForeignCopiesAreRejected()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            var id = path
                .Split('/')
                .Last();
            using var output = new MemoryStream();
            await Assert.ThatAsync(
                () => store.CopyCompletedToAsync(id, "alice", output),
                Throws.InstanceOf<HttpException>()
            );
            await Assert.ThatAsync(
                () => store.CopyCompletedToAsync(id, "bob", output),
                Throws.InstanceOf<HttpException>()
            );
            Assert.That(output.Length, Is.Zero);
        }
    }
}
