using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue394_ControllerCompression
    {
        public static IEnumerable ControllerCases()
        {
            foreach (var transport in new[] { "http1", "https1", "https2" })
                foreach (var buffered in new[] { false, true })
                    foreach (var coding in new[] { "identity", "gzip", "deflate" })
                        yield return new object[] { transport, buffered, coding };
        }

        [TestCaseSource(nameof(ControllerCases))]
        public async Task ControllerBodiesFinishCompressionAndPreserveSubsequentRequests(string transport, bool buffered, string coding)
        {
            await using var host = new Host(transport, buffered, false);
            using var client = host.Client(false);
            foreach (var path in new[] { "number", "empty", "text", "large", "number" })
            {
                using var request = host.Request(HttpMethod.Get, "api/" + path);
                request.Headers.TryAddWithoutValidation("Accept-Encoding", coding);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, host.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Version.Major, Is.EqualTo(host.Major));
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo(MimeType.Json));
                Assert.That(response.Headers.Vary, Does.Contain("Accept-Encoding"));
                var length = response.Content.Headers.ContentLength;
                Assert.That(length, Is.Not.EqualTo(0));
                if (buffered) Assert.That(length, Is.GreaterThan(0));
                else Assert.That(length, Is.Null);
                Assert.That(response.Headers.TransferEncodingChunked == true, Is.EqualTo(!buffered && host.Major == 1));
                Assert.That(response.Content.Headers.ContentEncoding, coding == "identity" ? Is.Empty : Is.EqualTo(new[] { coding }));
                var wire = await response.Content.ReadAsByteArrayAsync(host.Token);
                if (length.HasValue) Assert.That(wire.LongLength, Is.EqualTo(length.Value));
                using var input = new MemoryStream(wire);
                using Stream decoded = coding switch
                {
                    "gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                    _ => input,
                };
                using var output = new MemoryStream();
                await decoded.CopyToAsync(output, host.Token);
                Assert.That(Encoding.UTF8.GetString(output.ToArray()), Is.EqualTo(Expected(path)));
            }
            using var automatic = host.Client(true);
            using var automaticRequest = host.Request(HttpMethod.Get, "api/number");
            automaticRequest.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
            using var automaticResponse = await automatic.SendAsync(automaticRequest, host.Token);
            Assert.That(automaticResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await automaticResponse.Content.ReadAsStringAsync(host.Token), Is.EqualTo("12345"));
        }

        [TestCase("http1", false)]
        [TestCase("http1", true)]
        [TestCase("https1", false)]
        [TestCase("https1", true)]
        [TestCase("https2", false)]
        [TestCase("https2", true)]
        public async Task SeparatePostSampleWithAndWithoutReadingBodyLeavesNextResponseUsable(string transport, bool readBody)
        {
            await using var host = new Host(transport, false, readBody);
            using var client = host.Client(false);
            for (var repeat = 0; repeat < 3; repeat++)
            {
                using var request = host.Request(HttpMethod.Post, "post");
                request.Content = new StringContent("{123}", Encoding.UTF8, MimeType.Json);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, host.Token);
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsByteArrayAsync(host.Token), Is.Empty);
                Assert.That(host.Posts, Is.EqualTo(repeat + 1));
                using var nextRequest = host.Request(HttpMethod.Get, "api/number");
                using var next = await client.SendAsync(nextRequest, host.Token);
                Assert.That(next.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await next.Content.ReadAsStringAsync(host.Token), Is.EqualTo("12345"));
            }
        }

        private static string Expected(string path) => path switch
        {
            "empty" => "\"\"",
            "text" => "\"hello\"",
            "large" => "\"" + new string('x', 70000) + "\"",
            _ => "12345",
        };

        public sealed class Controller : WebApiController
        {
            [Route(HttpVerbs.Get, "/number")]
            public int Number() => 12345;
            [Route(HttpVerbs.Get, "/empty")]
            public string Empty() => string.Empty;
            [Route(HttpVerbs.Get, "/text")]
            public string Text() => "hello";
            [Route(HttpVerbs.Get, "/large")]
            public string Large() => new string('x', 70000);
        }

        private sealed class Host : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly Task _running;
            private readonly CancellationTokenSource _stop = new();
            private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
            private readonly X509Certificate2? _certificate;
            private readonly string _url;
            private int _posts;
            internal Host(string transport, bool buffered, bool readBody)
            {
                Major = transport == "https2" ? 2 : 1;
                var secure = transport != "http1";
                _url = secure ? Resources.GetServerAddress().Replace("http://", "https://", StringComparison.Ordinal) : Resources.GetServerAddress();
                _certificate = secure ? HttpsSmoke.CreateCertificate() : null;
                var options = new WebServerOptions().WithUrlPrefix(_url).WithMode(HttpListenerMode.EmbedIO);
                if (_certificate != null) options.WithCertificate(_certificate);
                _server = new WebServer(options)
                    .WithLocalSessionManager()
                    .WithWebApi("/api", ResponseSerializer.Json(buffered), module => module.WithController<Controller>())
                    .WithAction("/post", HttpVerbs.Post, async context =>
                    {
                        Interlocked.Increment(ref _posts);
                        if (readBody)
                        {
                            using var text = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                            Assert.That(await text.ReadToEndAsync(Token), Is.EqualTo("{123}"));
                        }
                    });
                _running = _server.RunAsync(_stop.Token);
            }
            internal int Major { get; }
            internal int Posts => Volatile.Read(ref _posts);
            internal CancellationToken Token => _deadline.Token;
            internal HttpRequestMessage Request(HttpMethod method, string path) => new(method, _url + path)
            {
                Version = Major == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            internal HttpClient Client(bool automatic)
            {
                var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    AutomaticDecompression = automatic ? DecompressionMethods.GZip | DecompressionMethods.Deflate : DecompressionMethods.None,
                };
                if (_certificate != null)
                    handler.SslOptions.RemoteCertificateValidationCallback = (_, c, _, _) => c != null && c.GetRawCertData().AsSpan().SequenceEqual(_certificate.RawData);
                return new HttpClient(handler);
            }
            public async ValueTask DisposeAsync()
            {
                try { _stop.Cancel(); await _running.WaitAsync(TimeSpan.FromSeconds(10)); }
                finally { _server.Dispose(); _certificate?.Dispose(); _stop.Dispose(); _deadline.Dispose(); }
            }
        }
    }
}

