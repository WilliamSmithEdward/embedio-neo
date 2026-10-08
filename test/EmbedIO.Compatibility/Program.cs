using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Authentication;
using EmbedIO.Routing;
using EmbedIO.Utilities;
using EmbedIO.WebApi;
using EmbedIO.WebSockets;

// This exact source is compiled separately against upstream and both Neo assets.
// System.Text.Json below writes the evidence; endpoint serialization belongs to EmbedIO.
internal static class Program
{
    private static readonly SortedDictionary<string, object?> Cases = new(StringComparer.Ordinal);

    private static async Task Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        Utilities();
        var windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
        await Http(HttpListenerMode.EmbedIO);
        if (windows) await Http(HttpListenerMode.Microsoft);
        else await NativeProbe();
        await Https();

        var assembly = typeof(WebServer).Assembly;
        var evidence = new
        {
            assembly = assembly.FullName,
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            targetFramework = assembly.GetCustomAttributes(false)
                .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>().Single().FrameworkName,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            profile = windows ? "windows" : "unix",
            cases = Cases,
            api = Api(assembly),
        };
        await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    }

    private static SortedDictionary<string, string[]> Api(Assembly assembly)
    {
        var api = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var type in assembly.GetExportedTypes())
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var lines = new List<string> { "base: " + (type.BaseType == null ? "" : TypeName(type.BaseType)) };
            lines.AddRange(type.GetInterfaces().Select(i => "interface: " + TypeName(i)));
            foreach (var member in type.GetMembers(flags))
            {
                if (member is MethodBase method && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
                {
                    lines.Add(member.MemberType + ": " + member + " | " + string.Join(";", method.GetParameters().Select(p =>
                        p.Name + ":optional=" + p.IsOptional + ":default=" + (p.HasDefaultValue ? Convert.ToString(p.DefaultValue, CultureInfo.InvariantCulture) : "<none>"))));
                }
                else if (member is FieldInfo field && (field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
                    lines.Add("Field: " + field + (field.IsLiteral ? " = " + Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture) : ""));
            }
            api.Add(type.FullName!, lines.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }
        return api;
    }

    private static string TypeName(Type type)
        => type.IsGenericType
            ? type.GetGenericTypeDefinition().FullName + "[" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + "]"
            : type.FullName ?? type.Name;

    private static void Capture(string name, Func<object?> action)
    {
        try { Cases.Add(name, new { value = action() }); }
        catch (ArgumentException ex) { Cases.Add(name, new { error = ex.GetType().FullName, parameter = ex.ParamName }); }
    }

    private static async Task NativeProbe()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var lifetime = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.Microsoft))
            .WithWebApi("/api", m => m.WithController<ConsumerController>());
        var running = server.RunAsync(lifetime.Token);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/api/dto");
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync());
        string? runError = null;
        string? cancelError = null;
        int? secondStatus = null;
        JsonElement? secondBody = null;
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(1));
            runError = "UnexpectedCompletion";
        }
        catch (TimeoutException) { } // A live accept loop is expected to stay pending.
        catch (Exception ex) { runError = ex.GetType().FullName; Console.Error.WriteLine($"Native baseline observation: {ex}"); }
        if (runError == null)
        {
            using var next = await client.GetAsync($"http://127.0.0.1:{port}/api/dto");
            secondStatus = (int)next.StatusCode;
            secondBody = JsonSerializer.Deserialize<JsonElement>(await next.Content.ReadAsByteArrayAsync());
        }
        try { lifetime.Cancel(); }
        catch (Exception ex) { cancelError = ex.GetType().FullName; Console.Error.WriteLine($"Native cancellation observation: {ex}"); }
        if (!running.IsCompleted) await running.WaitAsync(TimeSpan.FromSeconds(10));
        Cases.Add("native/unix-response-lifetime", new { status = (int)response.StatusCode, body, runError, cancelError, secondStatus, secondBody, state = server.State.ToString() });
    }

    private static async Task Https()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Windows Schannel needs a key container, as in the maintained HTTPS smoke.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var prefix = $"https://127.0.0.1:{port}/";
        using var lifetime = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIO).WithCertificate(certificate))
            .WithModule(new ActionModule("/", HttpVerbs.Get, c => c.SendStringAsync("encrypted", "text/plain", new UTF8Encoding(false))));
        var running = server.RunAsync(lifetime.Token);
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (server.State != WebServerState.Listening)
            {
                if (running.IsCompleted) await running;
                await Task.Delay(10, start.Token);
            }
            using var trustedHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, errors) => presented != null
                    && presented.RawData.SequenceEqual(certificate.RawData)
                    && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0,
            };
            using var trusted = new HttpClient(trustedHandler) { Timeout = TimeSpan.FromSeconds(10) };
            Cases.Add("https/pinned-certificate", await trusted.GetStringAsync(prefix));
            using var untrusted = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                await untrusted.GetStringAsync(prefix);
                throw new InvalidOperationException("Self-signed certificate was unexpectedly trusted");
            }
            catch (HttpRequestException) { Cases.Add("https/untrusted-rejected", true); }
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var observed = SslPolicyErrors.None;
            using var stream = new SslStream(tcp.GetStream(), false, (_, _, _, errors) => { observed = errors; return false; });
            try
            {
                await stream.AuthenticateAsClientAsync("wrong.example").WaitAsync(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("Incorrect hostname was unexpectedly trusted");
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                Cases.Add("https/hostname-rejected", (observed & SslPolicyErrors.RemoteCertificateNameMismatch) != 0);
            }
            Cases.Add("https/healthy-after-rejections", await trusted.GetStringAsync(prefix));
        }
        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Cases.Add("https/cancel", server.State.ToString());
        }
    }

    private static void Utilities()
    {
        string?[] paths = [null, "", "invalid", "/", "/api", "/api/", "//api///items//", "/caf\u00e9", "/a%2Fb", "/a/../b", "/a?x=1", "/a\\b"];
        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i]!;
            Capture($"utility/valid/{i}", () => UrlPath.IsValid(path));
            Capture($"utility/split/{i}", () => UrlPath.Split(path));
            foreach (var basePath in new[] { false, true })
                Capture($"utility/normalize/{i}/{basePath}", () => UrlPath.Normalize(path, basePath));
        }
        string?[] values = [null, "", " ", "\t\r\n", "value", "caf\u00e9"];
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            Capture($"utility/not-null/{i}", () => Validate.NotNull("consumerArgument", value));
            Capture($"utility/not-empty/{i}", () => Validate.NotNullOrEmpty("consumerArgument", value));
        }
        foreach (var path in new[] { "/api", "/api/", "/api/item", "/apix/item", "/API/item", "/api//item" })
        {
            Capture($"utility/prefix/{path}", () => UrlPath.HasPrefix(path, "/api"));
            Capture($"utility/strip/{path}", () => UrlPath.StripPrefix(path, "/api"));
        }
    }

    private static async Task Http(HttpListenerMode mode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "embedio-compatibility-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "<h1>compatibility</h1>", new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(directory, "file.txt"), "0123456789", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(Path.Combine(directory, "file.txt"), new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var prefix = $"http://127.0.0.1:{port}/";
        using var lifetime = new CancellationTokenSource();
        using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(mode))
            .WithLocalSessionManager()
            .WithCors("https://client.example", "content-type", "GET,POST,PUT,DELETE")
            .WithModule(new BasicAuthenticationModule("/protected").WithAccount("consumer", "fixture-password"))
            .WithModule(new ActionModule("/protected", HttpVerbs.Any, c => c.SendStringAsync("authorized", "text/plain", Encoding.UTF8)))
            .WithModule(new ActionModule("/session", HttpVerbs.Get, c =>
            {
                var count = (c.Session["visits"] is int previous ? previous : 0) + 1;
                c.Session["visits"] = count;
                return c.SendStringAsync(count.ToString(CultureInfo.InvariantCulture), "text/plain", Encoding.UTF8);
            }))
            .WithModule(new EchoSocket())
            .WithWebApi("/api", m => m.WithController<ConsumerController>())
            .WithStaticFolder("/", directory, false);
        var running = server.RunAsync(lifetime.Token);
        try
        {
            using var start = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (server.State != WebServerState.Listening)
            {
                if (running.IsCompleted) await running;
                await Task.Delay(10, start.Token);
            }
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(10) };
            var label = mode.ToString();
            async Task Request(string name, HttpMethod method, string path, string? body = null, string? contentType = null, Action<HttpRequestMessage>? configure = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
                configure?.Invoke(request);
                using var response = await client.SendAsync(request);
                var bytes = await response.Content.ReadAsByteArrayAsync();
                object? payload = Convert.ToBase64String(bytes);
                if ((int)response.StatusCode >= 400)
                    payload = null; // Engine-specific error-page prose is not a consumer contract here.
                else if (response.Content.Headers.ContentType?.MediaType == "application/json" && bytes.Length > 0)
                    payload = JsonSerializer.Deserialize<JsonElement>(bytes);
                Cases.Add($"http/{label}/{name}", new
                {
                    status = (int)response.StatusCode,
                    body = payload,
                    contentType = response.Content.Headers.ContentType?.ToString(),
                    allow = response.Content.Headers.Allow.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                    location = response.Headers.Location?.ToString(),
                    range = response.Content.Headers.ContentRange?.ToString(),
                    authentication = response.Headers.WwwAuthenticate.ToString(),
                    corsOrigin = Header(response, "Access-Control-Allow-Origin"),
                    corsMethods = Header(response, "Access-Control-Allow-Methods"),
                    corsHeaders = Header(response, "Access-Control-Allow-Headers"),
                    encoding = response.Content.Headers.ContentEncoding.ToArray(),
                    vary = response.Headers.Vary.ToArray(),
                });
            }
            await Request("dto", HttpMethod.Get, "api/dto");
            await Request("unicode-route", HttpMethod.Get, "api/text/caf%C3%A9");
            await Request("escaped-route", HttpMethod.Get, "api/text/a%20b");
            await Request("integer", HttpMethod.Get, "api/number/42");
            await Request("integer-negative", HttpMethod.Get, "api/number/-42");
            await Request("integer-invalid", HttpMethod.Get, "api/number/no");
            await Request("integer-overflow", HttpMethod.Get, "api/number/2147483648");
            await Request("optional-missing", HttpMethod.Get, "api/optional");
            await Request("optional-present", HttpMethod.Get, "api/optional/7");
            await Request("base-route", HttpMethod.Get, "api/base/a/b");
            await Request("exact-extra", HttpMethod.Get, "api/dto/extra");
            await Request("query", HttpMethod.Get, "api/query?a=one&a=two&a[]=three&blank=&flag&text=caf%C3%A9+space");
            await Request("query-field", HttpMethod.Get, "api/field?id=abc%20xyz");
            await Request("query-field-missing", HttpMethod.Get, "api/field");
            await Request("post-json", HttpMethod.Post, "api/body", "{\"Id\":42,\"Name\":\"caf\u00e9\",\"Amount\":12.50}");
            await Request("post-json-case", HttpMethod.Post, "api/body", "{\"id\":42,\"name\":\"value\",\"amount\":12.50}");
            await Request("post-json-invalid", HttpMethod.Post, "api/body", "{bad");
            await Request("post-json-controls", HttpMethod.Post, "api/body", "{\"Id\":1,\"Name\":\"a\r\nb\"}");
            await Request("post-json-trailing-comma", HttpMethod.Post, "api/body", "{\"Id\":1,}");
            await Request("post-json-quoted-number", HttpMethod.Post, "api/body", "{\"Id\":\"42\"}");
            await Request("post-json-number-syntax", HttpMethod.Post, "api/body", "{\"Id\":+001,\"Amount\":.5}");
            await Request("post-json-empty", HttpMethod.Post, "api/body", "");
            await Request("post-json-null", HttpMethod.Post, "api/body", "null");
            await Request("post-json-invalid-number", HttpMethod.Post, "api/body", "{\"Id\":\"oops\"}");
            await Request("post-json-trailing-garbage", HttpMethod.Post, "api/body", "{\"Id\":1}garbage");
            await Request("post-json-duplicate", HttpMethod.Post, "api/body", "{\"Id\":1,\"Id\":2}");
            foreach (var fixture in new[] { "fields", "derived", "shared", "cycle", "timestamp", "nonfinite" })
                await Request("json-" + fixture, HttpMethod.Get, "api/json/" + fixture);
            await Request("post-form", HttpMethod.Post, "api/form", "a=one&a=two&text=caf%C3%A9+space&empty=", "application/x-www-form-urlencoded");
            await Request("put", HttpMethod.Put, "api/number/8");
            await Request("delete", HttpMethod.Delete, "api/number/8");
            await Request("method-not-allowed", HttpMethod.Post, "api/dto", "{}");
            await Request("controller-not-found", HttpMethod.Get, "api/not-found");
            await Request("controller-error", HttpMethod.Get, "api/error");
            await Request("controller-head", HttpMethod.Head, "api/dto");
            await Request("file", HttpMethod.Get, "file.txt");
            await Request("file-gzip", HttpMethod.Get, "file.txt", configure: r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
            await Request("file-deflate", HttpMethod.Get, "file.txt", configure: r => r.Headers.AcceptEncoding.ParseAdd("deflate"));
            await Request("index", HttpMethod.Get, "");
            await Request("file-head", HttpMethod.Head, "file.txt");
            await Request("file-missing", HttpMethod.Get, "missing.txt");
            await Request("file-range", HttpMethod.Get, "file.txt", configure: r => r.Headers.Range = new RangeHeaderValue(2, 5));
            await Request("file-suffix", HttpMethod.Get, "file.txt", configure: r => r.Headers.Range = new RangeHeaderValue(null, 3));
            await Request("file-invalid-range", HttpMethod.Get, "file.txt", configure: r => r.Headers.Range = new RangeHeaderValue(50, 60));
            await Request("file-not-modified", HttpMethod.Get, "file.txt", configure: r => r.Headers.IfModifiedSince = new DateTimeOffset(2020, 1, 3, 0, 0, 0, TimeSpan.Zero));
            await Request("auth-missing", HttpMethod.Get, "protected");
            await Request("auth-valid", HttpMethod.Get, "protected", configure: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("consumer:fixture-password"))));
            await Request("auth-wrong", HttpMethod.Get, "protected", configure: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("consumer:wrong"))));
            await Request("auth-malformed", HttpMethod.Get, "protected", configure: r => r.Headers.Authorization = new AuthenticationHeaderValue("Basic", "invalid-base64!"));
            await Request("session-first", HttpMethod.Get, "session");
            await Request("session-second", HttpMethod.Get, "session");
            using (var fresh = new HttpClient { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(10) })
                Cases.Add($"http/{label}/session-independent", await fresh.GetStringAsync("session"));
            await Request("cors-preflight", HttpMethod.Options, "api/dto", configure: r =>
            {
                r.Headers.Add("Origin", "https://client.example");
                r.Headers.Add("Access-Control-Request-Method", "GET");
                r.Headers.Add("Access-Control-Request-Headers", "content-type");
            });
            await Request("cors-origin", HttpMethod.Get, "api/dto", configure: r => r.Headers.Add("Origin", "https://client.example"));
            await Request("cors-denied", HttpMethod.Get, "api/dto", configure: r => r.Headers.Add("Origin", "https://other.example"));
            foreach (var binary in new[] { false, true })
                foreach (var fragmented in new[] { false, true })
                    await WebSocket(prefix, label, binary, fragmented);
            await Request("healthy-after-errors", HttpMethod.Get, "api/number/3");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Original {mode} fixture failure before cleanup: {ex}");
            throw;
        }
        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Cases.Add($"lifecycle/{mode}/cancel", server.State.ToString());
            Directory.Delete(directory, true);
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static async Task WebSocket(string prefix, string label, bool binary, bool fragmented)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(prefix.Replace("http:", "ws:") + "ws"), deadline.Token);
        // Wait for an application-owned greeting, avoiding upstream's known early-message bug.
        var buffer = new byte[1024];
        var greeting = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
        if (Encoding.UTF8.GetString(buffer, 0, greeting.Count) != "ready") throw new InvalidOperationException("Missing readiness greeting");
        var payload = binary ? new byte[] { 0, 1, 127, 128, 255 } : Encoding.UTF8.GetBytes("caf\u00e9 \ud83d\ude00");
        var type = binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text;
        if (fragmented)
        {
            await socket.SendAsync(new ArraySegment<byte>(payload, 0, 2), type, false, deadline.Token);
            await socket.SendAsync(new ArraySegment<byte>(payload, 2, payload.Length - 2), type, true, deadline.Token);
        }
        else await socket.SendAsync(new ArraySegment<byte>(payload), type, true, deadline.Token);
        using var output = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            output.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        Cases.Add($"websocket/{label}/{binary}/{fragmented}", new { type = result.MessageType.ToString(), payload = Convert.ToBase64String(output.ToArray()) });
        // Abort avoids the old Windows native full-handshake close deadlock; it is not close parity evidence.
        socket.Abort();
    }
}

public sealed class ConsumerRow
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ConsumerController : WebApiController
{
    [Route(HttpVerbs.Get, "/json/{fixture}")]
    public object JsonFixture(string fixture)
    {
        var shared = new ConsumerRow { Id = 1, Name = "same" };
        return fixture switch
        {
            "fields" => new FieldRow(),
            "derived" => new List<BaseRow> { new DerivedRow { Id = 1, Extra = "derived" } },
            "shared" => new[] { shared, shared },
            "cycle" => new CycleRow(),
            "timestamp" => new { At = new DateTime(2026, 10, 7, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567) },
            "nonfinite" => new { Value = double.NaN },
            _ => throw HttpException.NotFound(),
        };
    }
    [Route(HttpVerbs.Get, "/dto")]
    public ConsumerRow Dto() => new() { Id = 42, Name = "ordinary", Amount = 12.50m };
    [Route(HttpVerbs.Get, "/text/{value}")]
    public string Text(string value) => value;
    [Route(HttpVerbs.Get, "/number/{value}")]
    public int Number(int value) => value;
    [Route(HttpVerbs.Put, "/number/{value}")]
    public int Put(int value) => value + 1;
    [Route(HttpVerbs.Delete, "/number/{value}")]
    public int Delete(int value) => value;
    [Route(HttpVerbs.Get, "/optional/{value?}")]
    public int Optional(int? value = null) => value ?? -1;
    [BaseRoute(HttpVerbs.Get, "/base/")]
    public string? Base() => Route.SubPath;
    [Route(HttpVerbs.Get, "/query")]
    public Dictionary<string, object?> Query([QueryData] NameValueCollection data) => data.ToDictionary();
    [Route(HttpVerbs.Get, "/field")]
    public string? Field([QueryField] string? id) => id;
    [Route(HttpVerbs.Post, "/body")]
    public ConsumerRow Body([JsonData] ConsumerRow row) => row;
    [Route(HttpVerbs.Post, "/form")]
    public Dictionary<string, object?> Form([FormData] NameValueCollection data) => data.ToDictionary();
    [Route(HttpVerbs.Get, "/not-found")]
    public void NotFound() => throw HttpException.NotFound();
    [Route(HttpVerbs.Get, "/error")]
    public void Error() => throw new InvalidOperationException("Consumer failure");
}

public sealed class FieldRow { public int Value = 42; }
public class BaseRow { public int Id { get; set; } }
public sealed class DerivedRow : BaseRow { public string? Extra { get; set; } }
public sealed class CycleRow { public CycleRow Next => this; }

public sealed class EchoSocket : WebSocketModule
{
    public EchoSocket() : base("/ws", true) { }
    protected override Task OnClientConnectedAsync(IWebSocketContext context) => SendAsync(context, "ready");
    protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
        => result.MessageType == (int)WebSocketMessageType.Binary
            ? SendAsync(context, buffer)
            : SendAsync(context, Encoding.UTF8.GetString(buffer));
}
