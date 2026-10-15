using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Authentication;
using EmbedIO.Files;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Modules are selected on the undecoded path, because WebServer builds the route from
    // Url.AbsolutePath and .NET's Uri keeps %2F encoded. A provider that decoded a whole
    // path therefore turned %2F into a separator that routing never matched, so a request
    // could reach a resource under a prefix whose modules, authentication included, never
    // ran. RFC 3986 Section 2.2: a reserved character in its encoded form is not equivalent
    // to the character itself, so an encoded separator stays inside its segment.
    public class EncodedSlashRoutingTest
    {
        private const string Secret = "protected-content-marker";

        private DirectoryInfo? _root;

        private DirectoryInfo Root => _root ?? throw new InvalidOperationException("The fixture root was not created.");

        [SetUp]
        public void CreateRoot()
        {
            _root = Directory.CreateTempSubdirectory("embedio-encoded-slash-");
            Directory.CreateDirectory(Path.Combine(Root.FullName, "admin"));
            File.WriteAllText(Path.Combine(Root.FullName, "admin", "secret.txt"), Secret);
            File.WriteAllText(Path.Combine(Root.FullName, "public.txt"), "public");
            File.WriteAllText(Path.Combine(Root.FullName, "two words.txt"), "spaced");
        }

        [TearDown]
        public void DeleteRoot() => _root?.Delete(true);

        // Each of these decodes to a path with a separator the request never sent, which
        // would reach admin/secret.txt under a prefix whose modules were never consulted.
        [TestCase("/admin%2fsecret.txt")]
        [TestCase("/admin%2Fsecret.txt")]
        [TestCase("/x%2f..%2fadmin%2fsecret.txt")]
        [TestCase("/x/..%2fadmin/secret.txt")]
        [TestCase("/admin%5csecret.txt")]
        public void TheFileProviderRefusesAnEncodedSeparator(string path)
        {
            using var provider = new FileSystemProvider(Root.FullName, false);
            Assert.That(provider.MapUrlPath(path, new MockMimeTypeProvider()), Is.Null);
        }

        // An escape that stays inside one segment still names its file, so ordinary
        // percent-encoded names keep working.
        [TestCase("/public.txt", "public.txt")]
        [TestCase("/two%20words.txt", "two words.txt")]
        [TestCase("/admin/secret.txt", "secret.txt")]
        public void TheFileProviderStillMapsNamesThatDecodeWithinTheirSegment(string path, string expected)
        {
            using var provider = new FileSystemProvider(Root.FullName, false);
            var info = provider.MapUrlPath(path, new MockMimeTypeProvider());
            Assert.That(info, Is.Not.Null);
            Assert.That(info.Name, Is.EqualTo(expected));
        }

        // Already refused before this change, by the containment check rather than by
        // segment identity; kept so both reasons stay covered.
        [TestCase("/%2e%2e/outside.txt")]
        [TestCase("/..%2foutside.txt")]
        public void TheFileProviderStillRefusesAnEscapeFromTheRoot(string path)
        {
            using var provider = new FileSystemProvider(Root.FullName, false);
            Assert.That(provider.MapUrlPath(path, new MockMimeTypeProvider()), Is.Null);
        }

        [TestCase("/admin%2fsecret.txt")]
        [TestCase("/sub%2fhello%20world.txt")]
        public void TheZipProviderRefusesAnEncodedSeparator(string path)
        {
            using var archive = CreateArchive();
            using var provider = new ZipFileProvider(archive, leaveOpen: true);
            Assert.That(provider.MapUrlPath(path, new MockMimeTypeProvider()), Is.Null);
        }

        [Test]
        public void TheZipProviderStillMapsEntriesThroughRealSeparators()
        {
            using var archive = CreateArchive();
            using var provider = new ZipFileProvider(archive, leaveOpen: true);
            var info = provider.MapUrlPath("/sub/hello%20world.txt", new MockMimeTypeProvider());
            Assert.That(info, Is.Not.Null);
            Assert.That(info.Path, Is.EqualTo("sub/hello world.txt"));
        }

        // The whole point, end to end: the shipped authentication module guards /admin, a
        // file module serves the tree that contains it, and an encoded separator must not
        // hand the protected file to a client that sent no credentials.
        [TestCase("/admin%2fsecret.txt")]
        [TestCase("/x%2f..%2fadmin%2fsecret.txt")]
        public async Task AnEncodedSeparatorDoesNotBypassAuthentication(string path)
        {
            await TestWebServer.UseAsync(
                server => server.WithModule(Guard()).WithModule(Files()),
                async client =>
                {
                    using var response = await client.GetAsync(path).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    Assert.That(response.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK));
                    Assert.That(body, Does.Not.Contain(Secret));
                }).ConfigureAwait(false);
        }

        // Controls for the case above: the guarded path is still guarded, credentials still
        // work, and an unguarded file is still served.
        [Test]
        public async Task TheGuardedPathAndTheOpenPathBothStillBehave()
        {
            await TestWebServer.UseAsync(
                server => server.WithModule(Guard()).WithModule(Files()),
                async client =>
                {
                    using var denied = await client.GetAsync("/admin/secret.txt").ConfigureAwait(false);
                    Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

                    client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("operator:s3cret")));
                    using var allowed = await client.GetAsync("/admin/secret.txt").ConfigureAwait(false);
                    Assert.That(allowed.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                    Assert.That(await allowed.Content.ReadAsStringAsync().ConfigureAwait(false), Is.EqualTo(Secret));

                    using var open = await client.GetAsync("/public.txt").ConfigureAwait(false);
                    Assert.That(open.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                }).ConfigureAwait(false);
        }

        private static BasicAuthenticationModule Guard()
        {
            var module = new BasicAuthenticationModule("/admin");
            module.Accounts["operator"] = "s3cret";
            return module;
        }

        private FileModule Files() => new("/", new FileSystemProvider(Root.FullName, false));

        private static MemoryStream CreateArchive()
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("sub/hello world.txt").Open(), new UTF8Encoding(false)))
                    writer.Write("Hello ZIP");
                using var secret = new StreamWriter(archive.CreateEntry("admin/secret.txt").Open(), new UTF8Encoding(false));
                secret.Write(Secret);
            }

            stream.Position = 0;
            return stream;
        }
    }
}
