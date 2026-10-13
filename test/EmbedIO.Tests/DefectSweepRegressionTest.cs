using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Cors;
using EmbedIO.Files;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.Utilities;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // Regressions for defects found by a review sweep of the engine branch.
    // Each case fails on the code it fixes and goes through a public entry point.
    [TestFixture]
    public class DefectSweepRegressionTest
    {
        private static readonly IPAddress Localhost = IPAddress.Parse("127.0.0.1");

        private static Task Text(IHttpContext context, string text) => context.SendStringAsync(text, MimeType.PlainText, WebServer.DefaultEncoding);

        private static TestWebServer SessionServer(LocalSessionManager manager)
        {
            var server = new TestWebServer()
                .WithSessionManager(manager)
                .WithAction("/set", HttpVerbs.Get, context =>
                {
                    context.Session["user"] = "alice";
                    return Text(context, "set");
                })
                .WithAction("/logout", HttpVerbs.Get, context =>
                {
                    context.Session.Delete();
                    return Text(context, "out");
                })
                .WithAction("/get", HttpVerbs.Get, context => Text(context, context.Session["user"] as string ?? "none"));
            server.Start();
            return server;
        }

        private static string SessionId(TestHttpClient client, TestWebServer server)
            => client.CookieContainer.GetCookies(server.BaseUrl)[LocalSessionManager.DefaultCookieName]?.Value
               ?? throw new AssertionException("No session cookie.");

        [Test]
        public async Task DeletingASessionWithDataInvalidatesItsCookie()
        {
            using var server = SessionServer(new LocalSessionManager());
            using var owner = TestHttpClient.Create(server);
            Assert.That(await owner.GetStringAsync("/set"), Is.EqualTo("set"));
            var id = SessionId(owner, server);
            Assert.That(await owner.GetStringAsync("/logout"), Is.EqualTo("out"));

            // A copy of the cookie taken before logout must not resume the deleted session.
            using var replay = TestHttpClient.Create(server);
            using var request = new HttpRequestMessage(HttpMethod.Get, "/get");
            request.Headers.Add("Cookie", LocalSessionManager.DefaultCookieName + "=" + id);
            using var response = await replay.SendAsync(request);
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("none"));
        }

        [Test]
        public async Task AnExpiredSessionIsNotRevivedBeforeThePurgeRuns()
        {
            var manager = new LocalSessionManager
            {
                SessionDuration = TimeSpan.FromMilliseconds(200),
                PurgeInterval = TimeSpan.FromHours(1),
            };
            using var server = SessionServer(manager);
            using var client = TestHttpClient.Create(server);
            Assert.That(await client.GetStringAsync("/set"), Is.EqualTo("set"));
            var id = SessionId(client, server);

            await Task.Delay(600);
            Assert.That(await client.GetStringAsync("/get"), Is.EqualTo("none"));
            Assert.That(SessionId(client, server), Is.Not.EqualTo(id));
        }

        [Test]
        public async Task AnAutomaticBanDoesNotShortenAnExplicitOne()
        {
            var module = new IPBanningModule("/sweep-ban", banMinutes: 1).WithMaxRequestsPerSecond(1);
            using var server = new TestWebServer()
                .WithModule(module)
                .WithAction("/", HttpVerbs.Any, context => Text(context, "ok"));
            server.Start();
            Assert.That(module.TryBanClient(Localhost, TimeSpan.FromDays(1)), Is.True);

            // Each request still runs the rate criterion, which bans for one minute.
            for (var i = 0; i < 5; i++)
            {
                using var response = await server.Client.GetAsync("/sweep-ban/x");
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            }

            var ban = module.BannedIPs.Single(info => Localhost.Equals(info.IPAddress));
            Assert.That(ban.ExpiresAt, Is.GreaterThan(DateTime.Now.AddHours(23).Ticks));
            Assert.That(ban.IsExplicit, Is.True);
        }

        [TestCase(Opcode.Text)]
        [TestCase(Opcode.Binary)]
        public void CompleteWebSocketMessagesReportEndOfMessage(Opcode opcode)
        {
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.WebSockets.Internal.WebSocketReceiveResult", true)
                ?? throw new AssertionException("Missing receive result type.");
            var result = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { 3, opcode }, null) as IWebSocketReceiveResult
                ?? throw new AssertionException("Missing receive result.");
            Assert.That(result.EndOfMessage, Is.True);
        }

        [Test]
        public async Task ACompressedDirectoryListingIsCompleteWhenTheListerLeavesTheStreamOpen()
        {
            using var server = new TestWebServer().WithModule(new FileModule("/", new MockFileProvider())
            {
                DefaultDocument = null,
                DirectoryLister = new OpenStreamLister(),
            });
            server.Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sub/");
            request.Headers.Add("Accept-Encoding", "gzip");
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(response.Content.Headers.ContentEncoding, Does.Contain("gzip"));
            using var compressed = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            Assert.That(await reader.ReadToEndAsync(), Is.EqualTo(OpenStreamLister.Listing));
        }

        // A lister that writes its listing and returns without closing the stream,
        // which IDirectoryLister permits.
        private sealed class OpenStreamLister : IDirectoryLister
        {
            public const string Listing = "<html><body>listing</body></html>";

            public string ContentType => MimeType.Html;

            public async Task ListDirectoryAsync(MappedResourceInfo info, string absolutePath, IEnumerable<MappedResourceInfo> entries, Stream stream, CancellationToken cancellationToken)
            {
                var bytes = Encoding.UTF8.GetBytes(Listing);
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        [TestCase("/a{{b", "/a{b")]
        [TestCase("/{{literal", "/{literal")]
        [TestCase("/x{{y}}", "/x{y}}")]
        public void ADoubledOpenBracketMatchesOneLiteralBracket(string route, string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            var matcher = RouteMatcher.Parse(route, false);
            Assert.That(matcher.Match(path), Is.Not.Null);
            Assert.That(matcher.Match(path.Replace("{", "\\{", StringComparison.Ordinal)), Is.Null);
        }

        [Test]
        public void ADoubledOpenBracketCanPrecedeAParameter()
        {
            var match = RouteMatcher.Parse("/p{{{id}", false).Match("/p{42") ?? throw new AssertionException("No match.");
            Assert.That(match["id"], Is.EqualTo("42"));
        }

        [Test]
        public void ZipEntriesReportTheirModificationTimeInUtc()
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("file.txt");
                entry.LastWriteTime = new DateTimeOffset(2024, 5, 6, 7, 8, 10, TimeSpan.Zero);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }

            DateTime expected;
            archive.Position = 0;
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, true))
                expected = (zip.GetEntry("file.txt") ?? throw new AssertionException("Missing entry.")).LastWriteTime.UtcDateTime;

            archive.Position = 0;
            using var provider = new ZipFileProvider(archive, true);
            var info = provider.MapUrlPath("/file.txt", new MockMimeTypeProvider()) ?? throw new AssertionException("Missing resource.");
            Assert.That(info.LastModifiedUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(info.LastModifiedUtc, Is.EqualTo(expected));
        }

        [Test]
        public async Task EchoingLastModifiedRevalidatesASubSecondModificationTime()
        {
            using var server = new TestWebServer().WithModule(new FileModule("/", new MockFileProvider()));
            server.Start();
            using var first = await server.Client.GetAsync("/random.dat");
            var lastModified = first.Content.Headers.LastModified ?? throw new AssertionException("Missing Last-Modified.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "/random.dat");
            request.Headers.IfModifiedSince = lastModified;
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        }

        [Test]
        public async Task ADateIfRangeMatchesASubSecondModificationTime()
        {
            using var server = new TestWebServer().WithModule(new FileModule("/", new MockFileProvider()));
            server.Start();
            using var first = await server.Client.GetAsync("/random.dat");
            var lastModified = first.Content.Headers.LastModified ?? throw new AssertionException("Missing Last-Modified.");

            using var request = new HttpRequestMessage(HttpMethod.Get, "/random.dat");
            request.Headers.Range = new RangeHeaderValue(0, 3);
            request.Headers.IfRange = new RangeConditionHeaderValue(lastModified);
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            Assert.That((await response.Content.ReadAsByteArrayAsync()).Length, Is.EqualTo(4));
        }

        // HttpClient joins several entity tags into one If-None-Match line, as browsers and caches do.
        [TestCase(false)]
        [TestCase(true)]
        public async Task AnIfNoneMatchListMatchesAnyOfItsTags(bool wildcard)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new WebServer(HttpListenerMode.EmbedIO, url).WithModule(new FileModule("/", new MockFileProvider()));
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(url) };
                using var first = await client.GetAsync("random.dat", stop.Token);
                var tag = first.Headers.ETag ?? throw new AssertionException("Missing ETag.");

                using var request = new HttpRequestMessage(HttpMethod.Get, "random.dat");
                if (wildcard)
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                }
                else
                {
                    request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"other\""));
                    request.Headers.IfNoneMatch.Add(tag);
                }

                using var response = await client.SendAsync(request, stop.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task AModuleExceptionHandlerAnswersTheRequest(bool httpException)
        {
            var laterCalls = 0;
            var failing = new ActionModule("/", HttpVerbs.Any, _ => httpException
                ? throw HttpException.Unauthorized()
                : throw new InvalidOperationException("synthetic test failure"));
            failing.HandleHttpException(HttpExceptionHandler.EmptyResponse);
            failing.HandleUnhandledException(ExceptionHandler.EmptyResponse);
            using var server = new TestWebServer()
                .WithModule(failing)
                .WithAction("/", HttpVerbs.Any, context =>
                {
                    Interlocked.Increment(ref laterCalls);
                    return Text(context, "later");
                });
            server.Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(httpException ? HttpStatusCode.Unauthorized : HttpStatusCode.InternalServerError));
            Assert.That(laterCalls, Is.Zero);
        }

        [Test]
        public async Task AnHttpExceptionWithANonStandardStatusKeepsItsStatus()
        {
            Assert.That(HttpStatusDescription.TryGet(499, out _), Is.False);
            using var server = new TestWebServer().WithAction("/", HttpVerbs.Any, _ => throw new HttpException(499));
            server.Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That((int)response.StatusCode, Is.EqualTo(499));
        }

        [Test]
        public async Task TheBaseSerializerAcceptsAContentTypeWithParameters()
        {
            using var server = new TestWebServer().WithAction("/", HttpVerbs.Any, context =>
            {
                context.Response.ContentType = "application/xml; charset=utf-8";
                return context.SendDataAsync(ResponseSerializer.None(false), "<a/>");
            });
            server.Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("<a/>"));
        }

        [Test]
        public async Task ARedirectThrownByTheUnhandledExceptionHandlerKeepsItsLocation()
        {
            using var server = new TestWebServer();
            server
                .WithAction("/", HttpVerbs.Any, _ => throw new InvalidOperationException("synthetic test failure"))
                .HandleUnhandledException((_, _) => throw HttpException.Redirect("/login"));
            server.Start();
            using var response = await server.Client.GetAsync("/");
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));
            Assert.That((response.Headers.Location ?? throw new AssertionException("Missing Location.")).AbsolutePath, Is.EqualTo("/login"));
        }

        [Test]
        public void QualityValueNamesCompareCaseInsensitively()
        {
            var list = new QValueList(true, "GZIP;q=0.5, Identity;q=0");
            Assert.That(list.TryGetWeight("gzip", out var weight), Is.True);
            Assert.That(weight, Is.EqualTo(500));
            Assert.That(list.TryGetWeight("identity", out weight), Is.True);
            Assert.That(weight, Is.Zero);
        }

        // Approved behavior corrections.

        [TestCase("https://a.example")]
        [TestCase("https://other.example")]
        public async Task ARestrictedOriginListVariesOnOrigin(string origin)
        {
            using var server = new TestWebServer()
                .WithModule(new CorsModule("/", "https://a.example,https://b.example"))
                .WithAction("/", HttpVerbs.Any, context => Text(context, "ok"));
            server.Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/data");
            request.Headers.Add("Origin", origin);
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.Headers.Vary, Does.Contain("Origin"));
        }

        public sealed class RequiredFieldController : WebApiController
        {
            [Route(HttpVerbs.Get, "/required")]
            public int Required([QueryField(true)] int id) => id;
        }

        [TestCase("/api/required?id=5", HttpStatusCode.OK)]
        [TestCase("/api/required?Id=5", HttpStatusCode.OK)]
        [TestCase("/api/required?ID=5", HttpStatusCode.OK)]
        [TestCase("/api/required?other=5", HttpStatusCode.BadRequest)]
        public async Task ARequiredQueryFieldIsFoundAsBindingFindsIt(string target, HttpStatusCode expected)
        {
            using var server = new TestWebServer().WithWebApi("/api", module => module.WithController<RequiredFieldController>());
            server.Start();
            using var response = await server.Client.GetAsync(target);
            Assert.That(response.StatusCode, Is.EqualTo(expected));
            if (expected == HttpStatusCode.OK)
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("5"));
        }

        [Test]
        public async Task ARangeEndingPastTheEndSelectsTheRemainder()
        {
            var provider = new MockFileProvider();
            var length = provider.GetRandomDataLength();
            using var server = new TestWebServer().WithModule(new FileModule("/", provider));
            server.Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/random.dat");
            request.Headers.Range = new RangeHeaderValue(length - 10, length + 100_000);
            using var response = await server.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
            var range = response.Content.Headers.ContentRange ?? throw new AssertionException("Missing Content-Range.");
            Assert.That((range.From, range.To, range.Length), Is.EqualTo(((long?)(length - 10), (long?)(length - 1), (long?)length)));
            Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(provider.GetRandomData().Skip(length - 10).ToArray()));
        }

        [TestCase("/users/a+b", "a+b")]
        [TestCase("/users/a%2Bb", "a+b")]
        [TestCase("/users/a%20b", "a b")]
        public void RouteParametersDecodePercentEscapesOnly(string path, string expected)
        {
            var match = RouteMatcher.Parse("/users/{name}", false).Match(path) ?? throw new AssertionException("No match.");
            Assert.That(match["name"], Is.EqualTo(expected));
        }

        [TestCase("/sub")]
        [TestCase("/sub/")]
        public async Task DirectoryListingLinksResolveWithOrWithoutATrailingSlash(string target)
        {
            using var server = new TestWebServer().WithModule(new FileModule("/", new MockFileProvider())
            {
                DefaultDocument = null,
                DirectoryLister = DirectoryLister.Html,
            });
            server.Start();
            var body = await server.Client.GetStringAsync(target);
            var page = new Uri(server.BaseUrl, target);
            var links = Regex.Matches(body, "href=[\"']([^\"']*)[\"']")
                .Select(link => new Uri(page, WebUtility.HtmlDecode(link.Groups[1].Value)).AbsolutePath)
                .ToArray();
            Assert.That(links, Does.Contain("/sub/index.html"));
            Assert.That(links, Does.Contain("/"));
        }
    }
}
