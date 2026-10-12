using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebApi;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    // A generated controller binding must behave exactly like reflection-based registration:
    // every request below goes to a server of each kind and the responses are compared.
    [TestFixture]
    public sealed class Issue138_GeneratedBinding
    {
        private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";

        private RunningServer? _reflection;
        private RunningServer? _generated;
        private HttpClient? _client;

        [OneTimeSetUp]
        public void Start()
        {
            _reflection = RunningServer.Start(new WebApiModule("/api").WithController<Issue138ParityController>());
            _generated = RunningServer.Start(new WebApiModule("/api").WithController(Issue138Bindings.Issue138ParityController));
            _client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
        }

        [OneTimeTearDown]
        public async Task Stop()
        {
            _client?.Dispose();
            if (_reflection != null) await _reflection.DisposeAsync();
            if (_generated != null) await _generated.DisposeAsync();
        }

        private static IEnumerable<TestCaseData> Requests()
        {
            TestCaseData Get(string path, int? status, string? headers = null) => new TestCaseData("GET", path, null, null, headers, status).SetName("GET " + path + (headers == null ? string.Empty : " [" + headers + "]"));
            TestCaseData Post(string path, string body, string contentType, int? status) => new TestCaseData("POST", path, body, contentType, null, status).SetName("POST " + path + " " + body);

            // Route parameters, converted with the same type converters.
            yield return Get("/api/route/int/42", 200);
            yield return Get("/api/route/int/0x2A", 200);
            yield return Get("/api/route/int/-7", 200);
            yield return Get("/api/route/int/abc", 400);
            yield return Get("/api/route/int/99999999999", 400);
            yield return Get($"/api/route/types/{Guid1}/monday/12.5/2024-01-02T03:04:05Z/true/9", 200);
            yield return Get($"/api/route/types/{Guid1}/3/-1/2024-01-02/False/0", 200);
            yield return Get($"/api/route/types/{Guid1}/Funday/1/2024-01-02/true/1", 400);
            yield return Get("/api/route/types/not-a-guid/Monday/1/2024-01-02/true/1", 400);
            yield return Get($"/api/route/types/{Guid1}/Monday/1/2024-13-45/true/1", 400);
            yield return Get($"/api/route/types/{Guid1}/Monday/1/2024-01-02/maybe/1", 400);
            yield return Get($"/api/route/types/{Guid1}/Monday/x/2024-01-02/true/1", 400);
            yield return Get($"/api/route/types/{Guid1}/Monday/1/2024-01-02/true/", null);
            yield return Get("/api/route/more/x/9000000000/1.5/01:02:03/relative/1.2.3/2024-01-02T03:04:05Z/-5/65535", 200);
            yield return Get("/api/route/more/xy/1/1/00:00:01/r/1.0/2024-01-02/1/1", 400);
            yield return Get("/api/route/more/x/1/1/00:00:01/r/1.0/2024-01-02/200/1", 400);
            yield return Get("/api/route/more/x/1/1/00:00:01/r/not-a-version/2024-01-02/1/1", null);
            yield return Get("/api/route/default/hello", 200);
            yield return Get("/api/route/point/1,2?q=3,4", 200);
            yield return Get("/api/route/point/1,2", 200);
            yield return Get("/api/route/point/1;2", 400);
            yield return Get("/api/route/point/1,2?q=bad", 400);
            yield return Get("/api/route/keyword/abc", 200);
            yield return Get("/api/over/int/5", 200);
            yield return Get("/api/over/text/5", 200);

            // Query fields: names, arrays, nullable values, enumerations and missing fields.
            yield return Get($"/api/query?s=x&alias=3&arr=1&arr=2&opt=4&day=tuesday&many=a&many=b&required=r&g={Guid1}", 200);
            yield return Get("/api/query?required=r", 200);
            yield return Get("/api/query?required=r&alias=0x10&opt=", 200);
            yield return Get("/api/query?required=r&s=1&s=2", 200);
            yield return Get("/api/query", 400);
            yield return Get("/api/query?required=r&alias=zz", 400);
            yield return Get("/api/query?required=r&arr=1&arr=x", 400);
            yield return Get("/api/query?required=r&day=Funday", 400);
            yield return Get("/api/query?required=r&g=bad", 400);

            // Form fields.
            yield return Post("/api/form", "s=x&alias=1.5&arr=1&arr=2&flag=true&many=a&many=b&required=r", "application/x-www-form-urlencoded", 200);
            yield return Post("/api/form", "required=r", "application/x-www-form-urlencoded", 200);
            yield return Post("/api/form", string.Empty, "application/x-www-form-urlencoded", 400);
            yield return Post("/api/form", "required=r&alias=bad", "application/x-www-form-urlencoded", 400);
            yield return Post("/api/form", "required=r&flag=notbool", "application/x-www-form-urlencoded", 400);
            yield return Post("/api/form", "required=r&arr=9223372036854775808", "application/x-www-form-urlencoded", 400);

            // JSON bodies with EmbedIO's default options.
            yield return Post("/api/json", "{\"Name\":\"ann\",\"Age\":3}", "application/json", 200);
            yield return Post("/api/json", "{\"name\":\"bob\",\"age\":\"4\",}", "application/json", 200);
            yield return Post("/api/json", "{bad", "application/json", 400);
            yield return Post("/api/json", "null", "application/json", 200);
            yield return Post("/api/json", string.Empty, "application/json", 200);
            yield return Post("/api/json/int", "5", "application/json", 200);
            yield return Post("/api/json/int", "\"x\"", "application/json", 400);
            yield return Post("/api/json/int", "null", "application/json", null);

            // Custom request data attributes, typed and untyped.
            yield return Get("/api/custom", 200, "X-Text=hi;number=5;optional=6");
            yield return Get("/api/custom", 200, "X-Text=hi;number=5");
            yield return Get("/api/custom", 500, "X-Text=hi");
            yield return Get("/api/custom", 500, "number=wrong-type");

            // Return types, errors, base routes, inheritance and verbs.
            yield return Get("/api/void", 200);
            yield return Get("/api/task", 200);
            yield return Get("/api/null", 200);
            yield return Get("/api/error/http", 404);
            yield return Get("/api/error/plain", 500);
            yield return Get("/api/error/async", 403);
            yield return Get("/api/base/one/two", 200);
            yield return Get("/api/base/", 200);
            yield return Post("/api/any", "x", "text/plain", 200);
            yield return Get("/api/any-alias", 200);
            yield return Get("/api/inherited", 200);
            yield return Get("/api/base-method", 200);
            yield return Get("/api/cancel", 200);
            yield return Get("/api/nothing-here", 404);
            yield return Post("/api/route/int/1", string.Empty, "text/plain", null);
        }

        [TestCaseSource(nameof(Requests))]
        public async Task GeneratedBindingMatchesReflection(string method, string path, string? body, string? contentType, string? headers, int? status)
        {
            ArgumentNullException.ThrowIfNull(path);
            var client = _client ?? throw new InvalidOperationException("Not started.");
            var expected = await SendAsync(client, (_reflection ?? throw new InvalidOperationException("Not started.")).Url, method, path, body, contentType, headers);
            var actual = await SendAsync(client, (_generated ?? throw new InvalidOperationException("Not started.")).Url, method, path, body, contentType, headers);

            if (status.HasValue)
                Assert.That(expected.Status, Is.EqualTo(status.Value), "The reflection registration did not take the intended path: " + expected);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [TestCase("{\"Name\":\"ann\",\"Age\":3}", 200, "{\"Name\":\"ann!\",\"Age\":3}")]
        [TestCase("{\"Name\":\"ann\",\"Age\":\"3\"}", 400, null)]
        [TestCase("{bad", 400, null)]
        public async Task JsonSerializerContextDeserializesWithItsOwnMetadata(string body, int status, string? response)
        {
            await using var server = RunningServer.Start(new WebApiModule("/api", ResponseSerializer.Json(Issue138Json.Default.Options))
                .WithController(Issue138Bindings.Issue138TypedJsonController));
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var result = await (_client ?? throw new InvalidOperationException("Not started.")).PostAsync(server.Url + "api/typed-json", content);
            Assert.That((int)result.StatusCode, Is.EqualTo(status));
            if (response != null)
                Assert.That(await result.Content.ReadAsStringAsync(), Is.EqualTo(response));
        }

        private static async Task<Snapshot> SendAsync(HttpClient client, string url, string method, string path, string? body, string? contentType, string? headers)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url + path.TrimStart('/'));
            if (body != null)
                request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");

            foreach (var header in (headers ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = header.Split('=', 2);
                request.Headers.TryAddWithoutValidation(parts[0], parts[1]);
            }

            using var response = await client.SendAsync(request);
            static string? Header(HttpResponseMessage message, string name)
                => message.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

            return new Snapshot(
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(),
                await response.Content.ReadAsStringAsync(),
                Header(response, "X-Before"),
                Header(response, "X-Void"),
                response.Headers.CacheControl?.ToString(),
                response.Content.Headers.Allow.Count == 0 ? null : string.Join(",", response.Content.Headers.Allow));
        }

        private sealed record Snapshot(int Status, string? ContentType, string Body, string? Before, string? Void, string? CacheControl, string? Allow);

        internal sealed class RunningServer : IAsyncDisposable
        {
            private readonly WebServer _server;
            private readonly CancellationTokenSource _stop;
            private readonly Task _running;

            private RunningServer(WebServer server, string url, CancellationTokenSource stop)
            {
                _server = server;
                Url = url;
                _stop = stop;
                _running = server.RunAsync(stop.Token);
            }

            public string Url { get; }

            public static RunningServer Start(params IWebModule[] modules)
            {
                var url = Resources.GetServerAddress();
                var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO));
                foreach (var module in modules)
                    server.WithModule(module);

                return new RunningServer(server, url, new CancellationTokenSource());
            }

            public async ValueTask DisposeAsync()
            {
                await _stop.CancelAsync();
                await _running.WaitAsync(TimeSpan.FromSeconds(10));
                _server.Dispose();
                _stop.Dispose();
            }
        }
    }
}
