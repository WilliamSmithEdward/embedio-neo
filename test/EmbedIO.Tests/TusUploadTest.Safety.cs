using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        [TestCase("count")]
        [TestCase("bytes")]
        public async Task OwnerQuotasDoNotConsumeAnotherOwnersAllowance(string mode)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(
                new TusUploadOptions(
                    directory.Path,
                    6,
                    12,
                    4,
                    6,
                    maximumUploadsPerOwner: mode == "count" ? 1 : 4,
                    maximumStorageBytesPerOwner: mode == "bytes" ? 6 : 12
                )
            );
            await using var host = new Host(store);
            await Create(host.Server.Client);
            using var request = Request("POST");
            request.Headers.Add("Upload-Length", "1");
            using var rejected = await host.Server.Client.SendAsync(request);
            Assert.That((int)rejected.StatusCode, Is.EqualTo(507));
            await Create(host.Server.Client, owner: "bob");
            Assert.That(store.ReservedBytes, Is.EqualTo(12));
        }

        [TestCase("Tus-Resumable")]
        [TestCase("Upload-Length")]
        [TestCase("Upload-Metadata")]
        [TestCase("X-HTTP-Method-Override")]
        public async Task DuplicateProtocolHeadersCannotCreateAnUpload(string header)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("POST");
            request.Headers.Add("Upload-Length", "1");

            if (header == "Upload-Metadata")
            {
                request.Headers.TryAddWithoutValidation(header, "a YQ==");
                request.Headers.TryAddWithoutValidation(header, "a Yg==");
            }

            else if (header == "X-HTTP-Method-Override")
            {
                request.Headers.Add(header, "POST");
                request.Headers.Add(header, "DELETE");
            }

            else request.Headers.TryAddWithoutValidation(header, header == "Tus-Resumable" ? "1.0.0" : "1");

            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [Test]
        public async Task AuthorizationReceivesTheEffectiveMethodBeforeOverrideExecution()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await TestWebServer.UseAsync(
                server => server
                    .WithModule(Authentication())
                    .WithModule(
                        new TusUploadModule(
                            "/uploads/",
                            store,
                            (c, method) => method == "DELETE" ? null : c.User.Identity?.Name
                        )
                    ),
                async client =>
                {
                    var path = await Create(client);
                    using var request = Request("POST", path);
                    request.Headers.Add("X-HTTP-Method-Override", "DELETE");
                    using var denied = await client.SendAsync(request);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                        Assert.That(store.Count, Is.EqualTo(1));
                    }
                }
            );
        }

        [TestCase("Content-Type", "application/json", 415)]
        [TestCase("Content-Encoding", "gzip", 400)]
        [TestCase("Upload-Offset", "-1", 400)]
        [TestCase("Upload-Offset", "999999999999999999999999", 400)]
        [TestCase("Upload-Length", "6", 400)]
        public async Task InvalidPatchHeadersPreserveTheOffset(string name, string value, int status)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            using var request = Request("PATCH", path, "abc");

            if (name != "Upload-Offset") request.Headers.Add("Upload-Offset", "0");

            if (name == "Content-Type")
            {
                (request.Content ?? throw new InvalidOperationException("Missing request content.")).Headers.Remove(
                    name
                );
                request.Content.Headers.Add(name, value);
            }

            else if (name == "Content-Encoding")
                (request.Content ?? throw new InvalidOperationException("Missing request content.")).Headers.Add(
                    name,
                    value
                );

            else request.Headers.Add(name, value);

            using var rejected = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)rejected.StatusCode, Is.EqualTo(status));
                Assert.That(
                    store
                        .GetUploads("alice")
                        .Single().Offset,
                    Is.Zero
                );
            }
        }

        [Test]
        public async Task CreationBodyAndOversizeLengthDoNotCreateSession()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var body = Request("POST", body: "x");
            body.Headers.Add("Upload-Length", "1");
            using var denied = await host.Server.Client.SendAsync(body);
            Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            using var big = Request("POST");
            big.Headers.Add("Upload-Length", "257");
            using var large = await host.Server.Client.SendAsync(big);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)large.StatusCode, Is.EqualTo(413));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [Test]
        public void OversizeCorruptStringPrefixIsRejectedBeforeAllocation()
        {
            using var directory = new DirectoryScope();
            Directory.CreateDirectory(directory.Path);
            var id = Guid
                .NewGuid()
                .ToString("N");

            using (var file = new BinaryWriter(File.Create(Path.Combine(directory.Path, id + ".info"))))
            {
                file.Write(0x54555301);
                file.Write(new byte[] { 255, 255, 255, 255, 7 });
            }

            Assert.That(() => new DiskTusUploadStore(Options(directory.Path)), Throws.TypeOf<InvalidDataException>());
        }

        [Test]
        public async Task CopyCancellationDoesNotCloseDestinationOrDeleteCompletedUpload()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client, 1);
            using var done = await Patch(host.Server.Client, path, 0, "a");
            using var output = new MemoryStream();
            using var cancel = new System.Threading.CancellationTokenSource();
            cancel.Cancel();
            await Assert.ThatAsync(
                () => store.CopyCompletedToAsync(
                    path
                        .Split('/')
                        .Last(),
                    "alice",
                    output,
                    cancel.Token
                ),
                Throws.InstanceOf<OperationCanceledException>()
            );

            using (Assert.EnterMultipleScope())
            {
                Assert.That(output.CanWrite, Is.True);
                Assert.That(store.Count, Is.EqualTo(1));
            }

            store.Delete(
                path
                    .Split('/')
                    .Last(),
                "alice"
            );
            Assert.That(store.Count, Is.Zero);
        }
    }
}
