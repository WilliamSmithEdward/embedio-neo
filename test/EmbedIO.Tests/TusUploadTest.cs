using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Testing;
using EmbedIO.Tus;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class TusUploadTest
    {
        private sealed class DirectoryScope : IDisposable
        {
            internal string Path { get; } = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "embedio-tus-"
                    + Guid
                        .NewGuid()
                        .ToString("N")
            );

            public void Dispose()
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, true);
            }
        }

        private sealed class Host : IAsyncDisposable
        {
            internal readonly TestWebServer Server = new TestWebServer();
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Task _running;

            internal Host(
                DiskTusUploadStore store,
                Func<IHttpContext, TusUpload, Task>? complete = null,
                Func<IHttpContext, string?>? owner = null
            )
            {
                var uploads = new TusUploadModule(
                    "/uploads/",
                    store,
                    (c, _) => owner == null ? c.User.Identity?.Name : owner(c)
                );
                uploads.OnCompleted = complete;
                Server
                    .WithModule(Authentication())
                    .WithModule(uploads);
                _running = Server.RunAsync(_stop.Token);
            }

            public async ValueTask DisposeAsync()
            {
                _stop.Cancel();
                await _running;
                Server.Dispose();
                _stop.Dispose();
            }
        }

        private static BearerAuthenticationModule Authentication() =>
            new BearerAuthenticationModule(
                "/",
                (credential, _) => Task.FromResult<ClaimsPrincipal?>(
                    credential == "invalid"
                        ? null
                        : new ClaimsPrincipal(
                            new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, credential) }, "test")
                        )
                )
            );

        private static TusUploadOptions Options(
            string path,
            int count = 8,
            long storage = 1024,
            long upload = 256,
            int patch = 64,
            TimeSpan? lifetime = null,
            int concurrent = 4
        ) => new TusUploadOptions(path, upload, storage, count, patch, lifetime, concurrent);

        private static HttpRequestMessage Request(
            string method,
            string path = "/uploads/",
            string? body = null,
            string? owner = "alice",
            string? version = "1.0.0"
        )
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);

            if (body != null)
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/offset+octet-stream");
            }

            if (owner != null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + owner);

            if (version != null) request.Headers.TryAddWithoutValidation("Tus-Resumable", version);

            return request;
        }

        private static string Header(HttpResponseMessage response, string name) =>
            response.Headers
                .GetValues(name)
                .Single();

        private static async Task<string> Create(
            HttpClient client,
            int length = 6,
            string owner = "alice",
            string? metadata = null
        )
        {
            using var request = Request("POST", owner: owner);
            request.Headers.Add("Upload-Length", length.ToString(CultureInfo.InvariantCulture));

            if (metadata != null) request.Headers.Add("Upload-Metadata", metadata);

            using var response = await client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                Assert.That(Header(response, "Upload-Offset"), Is.EqualTo("0"));
            }

            return Header(response, "Location");
        }

        private static async Task<HttpResponseMessage> Patch(
            HttpClient client,
            string path,
            long offset,
            string body,
            string owner = "alice",
            string? checksum = null
        )
        {
            using var request = Request("PATCH", path, body, owner);
            request.Headers.Add("Upload-Offset", offset.ToString(CultureInfo.InvariantCulture));

            if (checksum != null) request.Headers.Add("Upload-Checksum", checksum);

            return await client.SendAsync(request);
        }

        [Test]
        public async Task AdvertisesOnlyImplementedExtensionsAndLimits()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("OPTIONS", version: "ignored");
            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Header(response, "Tus-Version"), Is.EqualTo("1.0.0"));
                Assert.That(Header(response, "Tus-Extension"), Is.EqualTo("creation,expiration,termination,checksum"));
                Assert.That(Header(response, "Tus-Max-Size"), Is.EqualTo("256"));
                Assert.That(Header(response, "Tus-Checksum-Algorithm"), Is.EqualTo("sha1,sha256"));
            }
        }

        [Test]
        public async Task UploadsResumesCopiesAndTerminatesWithoutPublishingClientFilename()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            var completed = 0;
            await using var host = new Host(store, (_, upload) =>
            {
                Assert.That(upload.IsComplete, Is.True);
                completed++;
                return Task.CompletedTask;
            });
            var path = await Create(host.Server.Client, metadata: "filename Li4vZXNjYXBl,flag");
            using var first = await Patch(host.Server.Client, path, 0, "abc");
            Assert.That(Header(first, "Upload-Offset"), Is.EqualTo("3"));
            using var head = Request("HEAD", path);
            using var progress = await host.Server.Client.SendAsync(head);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Header(progress, "Upload-Offset"), Is.EqualTo("3"));
                Assert.That(Header(progress, "Upload-Length"), Is.EqualTo("6"));
                Assert.That(Header(progress, "Upload-Metadata"), Is.EqualTo("filename Li4vZXNjYXBl,flag"));
                Assert.That(Header(progress, "Cache-Control"), Is.EqualTo("no-store"));
                Assert.That(await progress.Content.ReadAsByteArrayAsync(), Is.Empty);
            }

            using var second = await Patch(host.Server.Client, path, 3, "def");
            Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var retry = await Patch(host.Server.Client, path, 6, "");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(completed, Is.EqualTo(1));
            }

            using var bytes = new MemoryStream();
            await store.CopyCompletedToAsync(
                path
                    .Split('/')
                    .Last(),
                "alice",
                bytes
            );
            Assert.That(Encoding.UTF8.GetString(bytes.ToArray()), Is.EqualTo("abcdef"));
            using var delete = Request("DELETE", path);
            using var deleted = await host.Server.Client.SendAsync(delete);
            Assert.That(deleted.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var missing = await host.Server.Client.SendAsync(Request("HEAD", path));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(missing.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(store.ReservedBytes, Is.Zero);
            }
        }

        [TestCase(null, 401)]
        [TestCase("invalid", 401)]
        [TestCase("bob", 404)]
        public async Task OwnershipIsRequiredForEveryResourceOperation(string? owner, int status)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);

            foreach (var method in new[] { "HEAD", "PATCH", "DELETE" })
            {
                using var request = Request(method, path, method == "PATCH" ? "x" : null, owner);
                request.Headers.Add("Upload-Offset", "0");
                using var response = await host.Server.Client.SendAsync(request);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That((int)response.StatusCode, Is.EqualTo(status), method);
                    Assert.That(response.Headers.Contains("Upload-Offset"), Is.False);
                }
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    store
                        .GetUploads("alice")
                        .Single().Offset,
                    Is.Zero
                );
                Assert.That(store.GetUploads("bob"), Is.Empty);
            }
        }

        [Test]
        public async Task AuthorizationSelectorRunsAgainAfterCreation()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            var allowed = true;
            await using var host = new Host(store, owner: c => allowed ? c.User.Identity?.Name : null);
            var path = await Create(host.Server.Client);
            allowed = false;
            using var response = await host.Server.Client.SendAsync(Request("HEAD", path));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        }

        [TestCase(null)]
        [TestCase("0.2.2")]
        public async Task WrongOrMissingVersionIsPreconditionFailure(string? version)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("POST", version: version);
            request.Headers.Add("Upload-Length", "1");
            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(412));
                Assert.That(Header(response, "Tus-Version"), Is.EqualTo("1.0.0"));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [TestCase("")]
        [TestCase("-1")]
        [TestCase("+1")]
        [TestCase("1,2")]
        [TestCase("9223372036854775808")]
        [TestCase("1.5")]
        public async Task InvalidLengthsDoNotReserveStorage(string value)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("POST");
            request.Headers.TryAddWithoutValidation("Upload-Length", value);
            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [TestCase("a !!!")]
        [TestCase("a YQ==,a Yg==")]
        [TestCase(",a YQ==")]
        [TestCase("a Y Q==")]
        [TestCase("a YQ==,")]
        public async Task InvalidMetadataIsRejected(string metadata)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("POST");
            request.Headers.Add("Upload-Length", "1");
            request.Headers.TryAddWithoutValidation("Upload-Metadata", metadata);
            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [TestCase("Upload-Defer-Length", "1")]
        [TestCase("Upload-Concat", "partial")]
        [TestCase("Content-Encoding", "gzip")]
        public async Task UnsupportedCreationModesAreRejected(string name, string value)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            using var request = Request("POST", body: "");
            request.Headers.Add("Upload-Length", "1");

            if (!request.Headers.TryAddWithoutValidation(name, value))
                (request.Content
                    ?? throw new InvalidOperationException("Missing request content.")).Headers.TryAddWithoutValidation(
                    name,
                    value
                );

            using var response = await host.Server.Client.SendAsync(request);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(store.Count, Is.Zero);
            }
        }

        [Test]
        public async Task ByteAndSessionBudgetsAreReservedUntilTermination()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path, count: 2, storage: 6, upload: 6));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            Assert.That(store.ReservedBytes, Is.EqualTo(6));
            using var request = Request("POST");
            request.Headers.Add("Upload-Length", "1");
            using var full = await host.Server.Client.SendAsync(request);
            Assert.That((int)full.StatusCode, Is.EqualTo(507));
            using var done = await Patch(host.Server.Client, path, 0, "abcdef");
            Assert.That(store.ReservedBytes, Is.EqualTo(6));
            using var delete = await host.Server.Client.SendAsync(Request("DELETE", path));
            Assert.That(store.ReservedBytes, Is.Zero);
            await Create(host.Server.Client, 0);
            await Create(host.Server.Client, 0);
            using var another = Request("POST");
            another.Headers.Add("Upload-Length", "0");
            using var countFull = await host.Server.Client.SendAsync(another);
            Assert.That((int)countFull.StatusCode, Is.EqualTo(507));
        }

        [TestCase("sha1")]
        [TestCase("sha256")]
        public async Task ChecksumFailureRollsBackAndCorrectRetryCompletes(string algorithm)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client, 3);
            using var hash = algorithm == "sha1" ? (HashAlgorithm)SHA1.Create() : SHA256.Create();
            var digest = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes("abc")));
            using var bad = await Patch(host.Server.Client, path, 0, "bad", checksum: algorithm + " " + digest);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)bad.StatusCode, Is.EqualTo(460));
                Assert.That(
                    store
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

            using var good = await Patch(host.Server.Client, path, 0, "abc", checksum: algorithm + " " + digest);
            Assert.That(good.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }

        [TestCase("md5 AAAA", 400)]
        [TestCase("sha256 !!!", 400)]
        [TestCase("sha256 YQ==", 400)]
        [TestCase("sha256", 400)]
        public async Task MalformedChecksumsDoNotChangeOffset(string checksum, int status)
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            using var response = await Patch(host.Server.Client, path, 0, "abc", checksum: checksum);

            using (Assert.EnterMultipleScope())
            {
                Assert.That((int)response.StatusCode, Is.EqualTo(status));
                Assert.That(
                    store
                        .GetUploads("alice")
                        .Single().Offset,
                    Is.Zero
                );
            }
        }

        [Test]
        public async Task OversizeAndConflictingPatchesPreserveExistingBytes()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path, patch: 3));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client);
            using var first = await Patch(host.Server.Client, path, 0, "abc");
            using var stale = await Patch(host.Server.Client, path, 0, "abc");
            Assert.That(stale.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
            using var big = await Patch(host.Server.Client, path, 3, "defg");
            Assert.That((int)big.StatusCode, Is.EqualTo(413));
            using var final = await Patch(host.Server.Client, path, 3, "def");
            using var bytes = new MemoryStream();
            await store.CopyCompletedToAsync(
                path
                    .Split('/')
                    .Last(),
                "alice",
                bytes
            );
            Assert.That(Encoding.UTF8.GetString(bytes.ToArray()), Is.EqualTo("abcdef"));
        }

        [Test]
        public async Task CompletionFailureKeepsCommittedBytesAndEmptyRetryDoesNotNotifyAgain()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            var calls = 0;
            await using var host = new Host(store, (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("Application publication failed.");
            });
            var path = await Create(host.Server.Client, 3);
            using var failed = await Patch(host.Server.Client, path, 0, "abc");
            Assert.That(failed.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            using var retry = await Patch(host.Server.Client, path, 3, "");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(
                    store
                        .GetUploads("alice")
                        .Single().IsComplete,
                    Is.True
                );
            }
        }

        [Test]
        public async Task ModuleAdmissionIncludesSlowCompletionAndReleasesAfterward()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path, concurrent: 1));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var host = new Host(store, async (_, _) =>
            {
                entered.TrySetResult();
                await release.Task;
            });
            var path = await Create(host.Server.Client, 1);
            var patch = Patch(host.Server.Client, path, 0, "x");

            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var rejected = await host.Server.Client.SendAsync(Request("HEAD", path));
                Assert.That((int)rejected.StatusCode, Is.EqualTo(503));
            }

            finally
            {
                release.TrySetResult();
            }

            using var completed = await patch;
            using var accepted = await host.Server.Client.SendAsync(Request("HEAD", path));
            Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        [Test]
        public async Task MethodOverrideUsesSameAuthenticationAndOwnerChecks()
        {
            using var directory = new DirectoryScope();
            using var store = new DiskTusUploadStore(Options(directory.Path));
            await using var host = new Host(store);
            var path = await Create(host.Server.Client, 1);
            using var request = Request("POST", path, "x");
            request.Headers.Add("X-HTTP-Method-Override", "PATCH");
            request.Headers.Add("Upload-Offset", "0");
            using var response = await host.Server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
            using var foreign = Request("POST", path, owner: "bob");
            foreign.Headers.Add("X-HTTP-Method-Override", "DELETE");
            using var denied = await host.Server.Client.SendAsync(foreign);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(store.Count, Is.EqualTo(1));
            }
        }
    }
}
