using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Files;
using EmbedIO.PlatformTests;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke
{
    // Serves real HTTP from a Native AOT executable and checks it with a client in the same
    // process: typed JSON in and out, invalid input, a static file, a streamed response, the
    // reflection-based default JSON path failing with its explicit message, telemetry and
    // shutdown. ProtocolChecks then covers HTTPS, HTTP/1.1, HTTP/2, HTTP/3, WebSockets,
    // sessions and dependency injection. Writes result.json to the folder given as the first argument. With
    // --jit-comparison the same checks run on a JIT build for startup and size comparison,
    // where the reflection-based default JSON path is expected to work.
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var output = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "aot-smoke");
            var jitComparison = args.Contains("--jit-comparison");
            Directory.CreateDirectory(output);
            var checks = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var started = Stopwatch.GetTimestamp();
            string? failure = null;

            try
            {
                await RunAsync(checks, started, jitComparison);
            }

            catch (Exception error) when (
                error is InvalidOperationException
                    or HttpRequestException
                    or IOException
                    or TimeoutException
                    or OperationCanceledException
                    or JsonException
                    or HttpListenerException
                    or System.Net.WebSockets.WebSocketException
                    or System.Security.Authentication.AuthenticationException
            )
            {
                failure = error.ToString();
            }

            var report = new SmokeReport
            {
                Passed = failure == null,
                Failure = failure,
                Checks = checks,
                Runtime = Environment.Version.ToString(),
                Os = Environment.OSVersion.ToString(),
                DynamicCode = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                ExecutableBytes = new FileInfo(Environment.ProcessPath ?? string.Empty).Length,
                WorkingSetBytes = Environment.WorkingSet
            };
            await File.WriteAllTextAsync(
                Path.Combine(output, "result.json"),
                JsonSerializer.Serialize(report, SmokeJson.Default.SmokeReport)
            );
            Console.WriteLine(JsonSerializer.Serialize(report, SmokeJson.Default.SmokeReport));
            return report.Passed ? 0 : 1;
        }

        private static async Task RunAsync(SortedDictionary<string, string> checks, long started, bool jitComparison)
        {
            Require(
                jitComparison != !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,
                jitComparison ? "The comparison build must run with the JIT." : "The host must run as Native AOT."
            );
            var root = Path.Combine(
                Path.GetTempPath(),
                "embedio-aot-"
                    + Guid
                        .NewGuid()
                        .ToString("N")
            );
            Directory.CreateDirectory(root);
            using var stop = new CancellationTokenSource();
            Task? running = null;
            Exception? primary = null;

            try
            {
                await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<p>static</p>");
                var url = $"http://127.0.0.1:{LoopbackPorts.Free()}/";
                var authorization = AuthorizationChecks.CreateModule();
                using var eventHub = new ServerSentEventHub();
                using var browserProtection = new EmbedIO.Security.BrowserRequestProtection(
                    new EmbedIO.Security.BrowserRequestProtectionOptions([url])
                );

                using var tusUploads = new EmbedIO.PlatformTests.TusUploadSmoke.State();
                using var server = new WebServer(o => o
                    .WithUrlPrefix(url)
                    .WithMode(HttpListenerMode.EmbedIO)
                    .WithTelemetry("aot-smoke"))
                    .WithSessionManager(new EmbedIO.Sessions.LocalSessionManager())
                    .WithModule(SecurityHeaderChecks.CreateModule())
                    .WithModule(HttpResultChecks.CreateModule())
                    .WithModule(HttpErrorChecks.CreateModule())
                    .WithModule(RequestBindingChecks.CreateModule())
                    .WithModule(MinimalEndpointChecks.CreateModule())
                    .WithModule(BrowserChecks.CreateModule(browserProtection))
                    .WithModule(AuthorizationChecks.CreateBearer())
                    .WithModule(authorization)
                    .WithModule(ValidationChecks.CreateModule())
                    .WithModule(StreamingChecks.CreateModule())
                    .WithModule(EventHubChecks.CreateModule(eventHub))
                    .WithModule(
                        new RoutingModule("/api")
                            .OnGet(
                                "/items/{id}",
                                (context, route) => context.SendDataWithMetadataAsync(
                                    new Item
                                    {
                                        Id = int.Parse(route["id"], System.Globalization.CultureInfo.InvariantCulture),
                                        Name = "item"
                                    },
                                    SmokeJson.Default.Item
                                )
                            )
                            .OnPost("/items", async (context, _) =>
                            {
                                var item = await context.GetRequestDataWithMetadataAsync(SmokeJson.Default.Item) ?? throw HttpException.BadRequest("Missing item.");
                                item.Name = item.Name.ToUpperInvariant();
                                await context.SendDataWithMetadataAsync(item, SmokeJson.Default.Item);
                            })
                            .OnGet("/reflection", (context, _) => context.SendDataAsync(new Item()))
                    )
                    .WithModule(
                        new WebApiModule("/ctl", ResponseSerializer.Json(SmokeJson.Default.Options)).WithController(
                            SmokeControllers.SmokeController
                        )
                    )
                    .WithModule(new ActionModule("/stream", HttpVerbs.Get, async context =>
                        {
                            using var stream = context.OpenResponseStream();

                            for (var i = 0; i < 3; i++)
                            {
                                await stream.WriteAsync(Encoding.ASCII.GetBytes("chunk" + i + ";"), context.CancellationToken);
                                await stream.FlushAsync(context.CancellationToken);
                            }
                        }));
                var outputCaching = EmbedIO.PlatformTests.OutputCacheSmoke.Configure(server);
                var idempotency = EmbedIO.PlatformTests.IdempotencySmoke.Configure(server);
                EmbedIO.PlatformTests.TusUploadSmoke.Configure(server, tusUploads);
                EmbedIO.PlatformTests.RequestAbortSmoke.Configure(server);
                using var health = HealthChecks.Create(server);
                server.WithStaticFolder("/", root, false);
                server.OnUnhandledException = (context, error) =>
                {
                    context.Response.StatusCode = 500;
                    return context.SendStringAsync(
                        error.GetType().Name + ": " + error.Message,
                        "text/plain",
                        Encoding.UTF8
                    );
                };
                running = server.RunAsync(stop.Token);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                await AuthorizationChecks.RunAsync(client, url, checks);
                await ValidationChecks.RunAsync(client, url, checks);
                await StreamingChecks.RunAsync(client, url, checks);
                await EventHubChecks.RunAsync(client, url, checks, eventHub);
                await BrowserChecks.RunAsync(client, url, checks);
                await SecurityHeaderChecks.RunAsync(client, url, checks);
                await HttpResultChecks.RunAsync(client, url, checks);
                await HttpErrorChecks.RunAsync(client, url, checks);
                await RequestBindingChecks.RunAsync(client, url, checks);
                await MinimalEndpointChecks.RunAsync(client, url, checks);
                await HealthChecks.RunAsync(client, url, checks, health);
                await EmbedIO.PlatformTests.OutputCacheSmoke.RunAsync(url, outputCaching);
                await EmbedIO.PlatformTests.IdempotencySmoke.RunAsync(url, idempotency);
                await EmbedIO.PlatformTests.TusUploadSmoke.RunAsync(url, tusUploads);
                checks["tus_uploads"] = "owner isolation, resume offsets and exact completed bytes";
                checks["idempotency"] =
                    "generated finite write replay, payload isolation and unconfigured no-store behavior";
                checks["output_caching"] =
                    "generated async hooks, query binding, cache hits, invalidation and uncached defaults";
                await EmbedIO.PlatformTests.RequestAbortSmoke.RunAsync(client, url);
                checks["request_abort"] =
                    "generated token binding, real HTTP/1 client cancellation and healthy successor";
                var first = await client.GetStringAsync(url + "api/items/7");
                checks["startup_to_first_response_ms"] = Stopwatch
                    .GetElapsedTime(started)
                    .TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                var item = JsonSerializer.Deserialize(first, SmokeJson.Default.Item);
                Require(item?.Id == 7 && item.Name == "item", "Typed GET returned " + first);
                checks["typed_get"] = first;

                using (var posted = await client.PostAsync(
                    url + "api/items",
                    new StringContent("{\"Id\":3,\"Name\":\"posted\"}", Encoding.UTF8, "application/json")
                ))
                {
                    var body = await posted.Content.ReadAsStringAsync();
                    Require(
                        posted.StatusCode == HttpStatusCode.OK && body.Contains("POSTED", StringComparison.Ordinal),
                        "Typed POST returned " + (int)posted.StatusCode + " " + body
                    );
                    checks["typed_post"] = body;
                }

                using (var invalid = await client.PostAsync(
                    url + "api/items",
                    new StringContent("{not json", Encoding.UTF8, "application/json")
                ))
                {
                    Require(
                        invalid.StatusCode == HttpStatusCode.BadRequest,
                        "Invalid JSON returned " + (int)invalid.StatusCode
                    );
                    checks["invalid_json"] = "400";
                }

                using (var reflection = await client.GetAsync(url + "api/reflection"))
                {
                    var body = await reflection.Content.ReadAsStringAsync();

                    if (jitComparison)
                        Require(
                            reflection.StatusCode == HttpStatusCode.OK,
                            "The reflection path returned " + (int)reflection.StatusCode + " " + body
                        );

                    else
                        Require(
                            reflection.StatusCode == HttpStatusCode.InternalServerError
                                && body.StartsWith(
                                    "NotSupportedException: Reflection-based JSON serialization is disabled",
                                    StringComparison.Ordinal
                                ),
                            "The reflection path returned " + (int)reflection.StatusCode + " " + body
                        );

                    checks["reflection_default_rejected"] = body;
                }

                // A Web API controller registered through its generated binding: route, query and
                // form values, a JSON body through the JsonSerializerContext, and a rejected route value.
                var controllerGet = await client.GetStringAsync(url + "ctl/items/5?name=gen");
                Require(
                    controllerGet.Contains("\"Id\": 5", StringComparison.Ordinal)
                        && controllerGet.Contains("\"Name\": \"gen\"", StringComparison.Ordinal),
                    "Controller GET returned " + controllerGet
                );
                checks["controller_get"] = controllerGet;

                using (var posted = await client.PostAsync(
                    url + "ctl/items",
                    new StringContent("{\"Id\":4,\"Name\":\"json\"}", Encoding.UTF8, "application/json")
                ))
                {
                    var body = await posted.Content.ReadAsStringAsync();
                    Require(
                        posted.StatusCode == HttpStatusCode.OK
                            && body.Contains("\"Name\": \"JSON\"", StringComparison.Ordinal),
                        "Controller POST returned " + (int)posted.StatusCode + " " + body
                    );
                    checks["controller_json"] = body;
                }

                var formFields = new[]
                {
                    new KeyValuePair<string, string>("count", "2"),
                    new KeyValuePair<string, string>("tags", "a"),
                    new KeyValuePair<string, string>("tags", "b")
                };

                using (var form = await client.PostAsync(url + "ctl/form", new FormUrlEncodedContent(formFields)))
                {
                    var body = await form.Content.ReadAsStringAsync();
                    Require(
                        form.StatusCode == HttpStatusCode.OK && body == "\"2:a,b\"",
                        "Controller form returned " + (int)form.StatusCode + " " + body
                    );
                    checks["controller_form"] = body;
                }

                using (var invalid = await client.GetAsync(url + "ctl/items/not-a-number"))
                {
                    Require(
                        invalid.StatusCode == HttpStatusCode.BadRequest,
                        "Invalid controller route value returned " + (int)invalid.StatusCode
                    );
                    checks["controller_invalid_route"] = "400";
                }

                var page = await client.GetStringAsync(url);
                Require(page == "<p>static</p>", "Static file returned " + page);
                checks["static_file"] = page;

                var streamed = await client.GetStringAsync(url + "stream");
                Require(streamed == "chunk0;chunk1;chunk2;", "Streaming returned " + streamed);
                checks["streaming"] = streamed;

                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
                checks["shutdown"] = "completed";
            }

            catch (Exception error)
            {
                primary = error;
                throw;
            }

            finally
            {
                try
                {
                    try
                    {
                        await stop.CancelAsync();

                        if (running != null) await running.WaitAsync(TimeSpan.FromSeconds(10));
                    }

                    finally
                    {
                        File.Delete(Path.Combine(root, "index.html"));
                        Directory.Delete(root);
                    }
                }

                catch (Exception cleanupError) when (
                    primary != null && EmbedIO.Internal.ExceptionPolicy.IsRecoverable(cleanupError)
                )
                {
                    Console.Error.WriteLine("Cleanup after smoke failure: " + cleanupError);
                }
            }

            await ProtocolChecks.RunAsync(checks);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    [GeneratedControllerBinding(typeof(SmokeController), JsonSerializerContext = typeof(SmokeJson))]
    [GeneratedControllerBinding(typeof(GreetingController))]
    internal static partial class SmokeControllers;

    internal sealed class SmokeController : WebApiController
    {
        [Route(HttpVerbs.Get, "/items/{id}")]
        public Item Get(int id, [QueryField] string? name)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return new Item
            {
                Id = id,
                Name = name ?? string.Empty
            };
        }

        [Route(HttpVerbs.Post, "/items")]
        public Item Post([JsonData] Item item)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return new Item
            {
                Id = item.Id,
                Name = item.Name.ToUpperInvariant()
            };
        }

        [Route(HttpVerbs.Post, "/form")]
        public string Form([FormField] int count, [FormField] string[] tags)
        {
            HttpContext.CancellationToken.ThrowIfCancellationRequested();
            return count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + string.Join(",", tags);
        }
    }

    internal sealed class Item
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    internal sealed class SmokeReport
    {
        public bool Passed { get; set; }
        public string? Failure { get; set; }
        public SortedDictionary<string, string> Checks { get; set; } = new(StringComparer.Ordinal);
        public string Runtime { get; set; } = string.Empty;
        public string Os { get; set; } = string.Empty;
        public bool DynamicCode { get; set; }
        public long ExecutableBytes { get; set; }
        public long WorkingSetBytes { get; set; }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Item))]
    [JsonSerializable(typeof(ValidatedSmokeInput))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(SmokeReport))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
