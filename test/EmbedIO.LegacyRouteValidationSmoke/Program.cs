using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Security;
using EmbedIO.Sessions;
using EmbedIO.Actions;
using EmbedIO.WebApi;

public static class Program
{
    private static int _checks;

    public static void Main()
    {
        RunAsync().GetAwaiter().GetResult();
        var report = new
        {
            passed = true,
            assertions = _checks,
            target = "net472",
            clr = Environment.Version.ToString(),
            osReportedByRuntime = Environment.OSVersion.ToString(),
            frameworkRelease = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null),
            assembly = typeof(WebServer).Assembly.FullName,
        };
        Directory.CreateDirectory("TestResults");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("TestResults/legacy-route-validation.json", json);
        Console.WriteLine(json);
    }

    private static async Task RunAsync()
    {
        _checks += await LegacyHealthChecks.RunAsync();
        var cases = new (string Path, int Status)[]
        {
            ("u/oops", 400), ("u/65536", 400), ("u/-1", 400), ("u/65535", 200), ("u/0xFFFF", 200),
            ("u/%20", 400), ("optional", 200), ("optional/9", 200), ("choice/oNe", 200), ("choice/99", 200),
            ("u", 404), ("throws/7", 500), ("required-optional", 500), ("custom/generic-wrapper", 500),
            ("validated?value=0", 400), ("validated?value=1", 200),
            ("custom/bad-format", 400), ("custom/not-supported", 500), ("query?value=oops", 500),
        };
        foreach (var mode in new[] { HttpListenerMode.EmbedIO })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var url = "http://127.0.0.1:" + port + "/";
            using var stop = new CancellationTokenSource();
            using var protection = new BrowserRequestProtection(new BrowserRequestProtectionOptions(new[] { url }));
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithSessionManager(new LocalSessionManager())
                .WithModule(new ActionModule("/token", HttpVerbs.Get, context => context.SendStringAsync(protection.GetToken(context), "text/plain", WebServer.Utf8NoBomEncoding)))
                .WithModule(new BrowserRequestProtectionModule("/browser", protection))
                .WithModule(new ActionModule("/browser", HttpVerbs.Post, context => context.SendStringAsync("accepted", "text/plain", WebServer.Utf8NoBomEncoding)))
                .WithWebApi("/api", m => m.WithController<Controller>());
            var outputCaching = EmbedIO.PlatformTests.OutputCacheSmoke.Configure(server);
            var idempotency = EmbedIO.PlatformTests.IdempotencySmoke.Configure(server);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var running = server.RunAsync(stop.Token);
            try
            {
                await EmbedIO.PlatformTests.OutputCacheSmoke.RunAsync(url, outputCaching);
                _checks += 5;
                _checks += await EmbedIO.PlatformTests.IdempotencySmoke.RunAsync(url, idempotency);
                foreach (var test in cases)
                {
                    using var response = await client.GetAsync(url + "api/" + test.Path);
                    var expected = test.Status;
                    if ((int)response.StatusCode != expected)
                        throw new InvalidOperationException(mode + " " + test.Path + ": expected " + expected
                            + ", received " + (int)response.StatusCode + ".");
                    _checks++;
                }
                foreach (var (path, expected) in new[] { ("stream-array", "[1\n,2\n]"), ("stream-lines", "1\n2\n") })
                {
                    using var response = await client.GetAsync(url + "api/" + path);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.StatusCode != HttpStatusCode.OK || body != expected)
                        throw new InvalidOperationException("Legacy streaming mismatch: " + path + ": " + body);
                    _checks++;
                }
                var token = await client.GetStringAsync(url + "token");
                using (var missing = await client.PostAsync(url + "browser", new StringContent("payload")))
                {
                    if (missing.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("Legacy missing CSRF token was accepted.");
                    _checks++;
                }
                using (var request = new HttpRequestMessage(HttpMethod.Post, url + "browser"))
                {
                    request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token);
                    using var accepted = await client.SendAsync(request);
                    if (accepted.StatusCode != HttpStatusCode.OK) throw new InvalidOperationException("Legacy session-bound CSRF token failed.");
                    _checks++;
                }
                using (var request = new HttpRequestMessage(HttpMethod.Post, url + "browser"))
                {
                    request.Headers.Add(BrowserRequestProtection.TokenHeaderName, token); request.Headers.Add("Origin", "http://evil.example");
                    using var denied = await client.SendAsync(request);
                    if (denied.StatusCode != HttpStatusCode.Forbidden) throw new InvalidOperationException("Legacy foreign Origin was accepted.");
                    _checks++;
                }
            }
            finally
            {
                stop.Cancel();
                if (await Task.WhenAny(running, Task.Delay(10000)) != running)
                    throw new TimeoutException("Listener shutdown exceeded ten seconds.");
                await running;
            }
        }
        using (var hub = new ServerSentEventHub())
        {
            var cursor = hub.Publish("before"); var id = hub.Publish("after");
            using var subscriber = hub.Subscribe(cursor);
            var item = await subscriber.ReadAsync(); hub.Complete();
            if (item == null || item.Id != id || item.Data != "after" || await subscriber.ReadAsync() != null) throw new InvalidOperationException("Legacy SSE replay mismatch.");
            _checks++;
        }
        if (_checks != 42) throw new InvalidOperationException("Legacy route and health check count changed.");
    }

    public enum Choice { One = 1 }

    [TypeConverter(typeof(CustomConverter))]
    public sealed class CustomValue
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class CustomConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string);

        public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if ((string)value == "generic-wrapper")
                throw new Exception("Application wrapper.", new FormatException("Application failure."));
            if ((string)value == "bad-format") throw new FormatException("Malformed custom input.");
            if ((string)value == "not-supported") throw new NotSupportedException("Unimplemented converter.");
            return new CustomValue { Value = (string)value };
        }
    }

    public sealed class Controller : WebApiController
    {
        private static readonly JsonTypeInfo<int> ItemInfo = (JsonTypeInfo<int>)new JsonSerializerOptions
        { TypeInfoResolver = new DefaultJsonTypeInfoResolver() }.GetTypeInfo(typeof(int));
        [Route(HttpVerbs.Get, "/stream-array")]
        public JsonArrayStream<int> StreamArray() => JsonStream.Array(StreamValues(), ItemInfo);
        [Route(HttpVerbs.Get, "/stream-lines")]
        public Task<NdjsonStream<int>> StreamLines() => Task.FromResult(JsonStream.Ndjson(StreamValues(), ItemInfo));
        private static async IAsyncEnumerable<int> StreamValues() { yield return 1; await Task.Yield(); yield return 2; }

        [Route(HttpVerbs.Get, "/validated")]
        public string Validated([QueryField, Validate, System.ComponentModel.DataAnnotations.Range(1, 10)] int value)
            => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/u/{value}")]
        public string U(ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/optional/{value?}")]
        public string Optional(ushort? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

        [Route(HttpVerbs.Get, "/choice/{value}")]
        public string EnumValue(Choice value) => value.ToString();

        [Route(HttpVerbs.Get, "/required-optional/{value?}")]
        public string RequiredOptional(ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/custom/{value}")]
        public string Custom(CustomValue value) => (value ?? throw new ArgumentNullException(nameof(value))).Value;

        [Route(HttpVerbs.Get, "/query")]
        public string Query([QueryField] ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/throws/{value}")]
        public string Throws(ushort value) => throw new FormatException("Application failure.");
    }
}
