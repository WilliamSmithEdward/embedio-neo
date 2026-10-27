using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

public static partial class Program
{
    private static readonly string?[] FieldNames =
    [
        "Content-Type", "content-type", "CONTENT-LENGTH", "Content-Length", "Date", "Server", "Keep-Alive",
        "Alt-Svc", "Connection", "Transfer-Encoding", "Trailer", "Upgrade", "Vary", "Set-Cookie", "Cache-Control",
        "X-Custom", "x-custom", "Capsule-Protocol", "Content-Encoding", "set-cookie", "Bad Name", "a'b", "", "é",
        "Na:me", "Tab\tName", null
    ];

    private static readonly string?[] FieldValues =
    [
        "text/plain", "text/html; charset=utf-8", "0", "13", "keep-alive", "close", "chunked", "a, b",
        "x=1, y=2", "Accept-Encoding, Origin", "\"a,b\", c", "timeout=15, max=100", " lead", "trail ", "",
        "tab\there", "ctl\u0001", "line\r\n next", "line\r\nbad", "été", "\u0001", "  ", "Accept-Encoding", "gzip",
        "a=1", "\"quoted\"", null
    ];

    // The engine keeps a response's fields in a list until Headers is read. On .NET Framework the
    // collection it stands in for is the framework's WebHeaderCollection, so random operations,
    // valid and invalid, must give the same results, exceptions and collection as that one.
    private static void VerifyResponseFieldsMatchFrameworkCollection()
    {
        var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.ResponseHeaderFields", true)
            ?? throw new InvalidOperationException("Missing response field list.");

        for (var seed = 0; seed < 2000; seed++)
        {
            var random = new Random(seed);
            // Starting sizes below and above what a sequence needs, so the list also grows.
            var fields = Activator.CreateInstance(
                    type,
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    [random.Next(0, 10)],
                    null
                )
                ?? throw new InvalidOperationException("No field list.");
            var reference = new WebHeaderCollection();
            NameValueCollection view = reference;
            var operations = random.Next(1, 40);

            for (var step = 0; step < operations; step++)
            {
                var name = FieldNames[random.Next(FieldNames.Length)];
                var value = FieldValues[random.Next(FieldValues.Length)];
                var label = $"seed {seed}, step {step}";

                switch (random.Next(7))
                {
                    case 0:
                        SameOutcome(() => view.Set(name, value), () => Call(fields, "Set", name, value), label + " Set");
                        break;
                    case 1:
                        SameOutcome(() => view.Add(name, value), () => Call(fields, "Add", name, value), label + " Add");
                        break;
                    case 2:
                        SameOutcome(() => view.Remove(name), () => Call(fields, "Remove", name), label + " Remove");
                        break;
                    case 3:
                        SameResult(() => view.Get(name), () => Call(fields, "Value", name), label + " Value");
                        break;
                    case 4:
                        SameResult(() => view.GetValues(name), () => Call(fields, "Values", name), label + " Values");
                        break;
                    case 5:
                        // A simple list's values are what the collection's GetValues(name) gives.
                        if (Read(fields, "IsSimple") is true)
                            for (var index = 0; index < reference.Count; index++)
                                Expect(
                                    Same(reference.GetValues(reference.GetKey(index)), new[] { reference.Get(index) }),
                                    label + " IsSimple"
                                );
                        break;
                    default:
                        if (random.Next(4) == 0) _ = Read(fields, "Collection");
                        break;
                }

                Expect(Equals(Read(fields, "Count"), reference.Count), label + " Count");

                for (var index = 0; index < reference.Count; index++)
                {
                    Expect(Equals(Call(fields, "NameAt", index), reference.GetKey(index)), label + " NameAt");
                    Expect(Equals(Call(fields, "ValueAt", index), reference.Get(index)), label + " ValueAt");
                }
            }

            var collection = Read(fields, "Collection") as WebHeaderCollection
                ?? throw new InvalidOperationException("Missing collection.");
            Check(Same(collection.AllKeys, reference.AllKeys), $"seed {seed} keys");

            for (var index = 0; index < reference.Count; index++)
                Expect(Same(collection.GetValues(index), reference.GetValues(index)), $"seed {seed} values {index}");
        }
    }

    // Over HTTP/1 on .NET Framework, a response sends the same head and leaves the same Headers
    // whether or not the handler read Headers first, and one that never reads Headers leaves the
    // collection unbuilt until then.
    private static async Task VerifyResponseFieldsOverHttp1Async()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var after = new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        using var server = new WebServer(o => o.WithUrlPrefix("http://127.0.0.1:" + port + "/").WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/fields", HttpVerbs.Get, async context =>
                {
                    var query = context.Request.QueryString;
                    var read = query["read"] == "1";
                    var recorded = after.GetOrAdd(query["id"] ?? "", _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
                    context.OnClose(closed =>
                    {
                        var fields = closed.Response.GetType()
                            .GetProperty("Fields", BindingFlags.Instance | BindingFlags.NonPublic)?
                            .GetValue(closed.Response)
                            ?? throw new InvalidOperationException("Missing field list.");
                        var built = fields.GetType()
                            .GetField("_collection", BindingFlags.Instance | BindingFlags.NonPublic)?
                            .GetValue(fields) != null;
                        recorded.TrySetResult((built == read ? "" : "unexpected collection state\n") + Snapshot(closed.Response.Headers));
                    });

                    if (read) Check(context.Response.Headers.Count == 0, "A new response has no fields.");

                    switch (query["shape"])
                    {
                        case "length":
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = 2;
                            var body = Encoding.ASCII.GetBytes("{}");
                            await context.Response.OutputStream
                                .WriteAsync(body, 0, body.Length)
                                .ConfigureAwait(false);
                            break;
                        case "json":
                            await ResponseSerializer
                                .Json(context, new { value = 1 })
                                .ConfigureAwait(false);
                            break;
                        default:
                            await context
                                .SendStringAsync("fields", "text/plain", WebServer.Utf8NoBomEncoding)
                                .ConfigureAwait(false);
                            break;
                    }
                }));
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);

        try
        {
            var request = 0;

            foreach (var shape in new[] { "length", "json", "string" })
            {
                var results = new string[2];

                for (var read = 0; read < 2; read++)
                {
                    var id = (++request).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var wire = RawGet(port, $"/fields?id={id}&read={read}&shape={shape}");
                    var headers = await after
                        .GetOrAdd(id, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously))
                        .Task
                        .ConfigureAwait(false);
                    results[read] = WithoutDate(wire) + "\n---\n" + headers;
                }

                Check(results[0] == results[1], shape + ": the head and Headers match whether or not Headers was read.");
                Check(results[0].Contains("Server: "), shape + ": Headers holds the engine's fields.");
            }
        }

        finally
        {
            stop.Cancel();

            await AwaitShutdownAsync(running).ConfigureAwait(false);
        }
    }

    private static string RawGet(int port, string target)
    {
        using var client = Connect(port);
        using var wire = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            "GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\nConnection: close\r\n\r\n"
        );
        wire.Write(request, 0, request.Length);
        using var response = new MemoryStream();
        wire.CopyTo(response);
        return Encoding.ASCII.GetString(response.ToArray());
    }

    // The Date field differs from one second to the next.
    private static string WithoutDate(string text)
    {
        var start = text.IndexOf("Date: ", StringComparison.Ordinal);

        if (start < 0) return text;

        var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
        return text.Substring(0, start + 6) + text.Substring(end);
    }

    private static string Snapshot(WebHeaderCollection headers)
    {
        var text = new StringBuilder();

        for (var index = 0; index < headers.Count; index++)
        {
            var name = headers.GetKey(index);
            text.Append(name).Append(": ");

            if (name != "Date") text.Append(string.Join("|", headers.GetValues(index) ?? []));

            text.Append('\n');
        }

        return text.ToString();
    }

    private static object? Call(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(target.GetType().Name, name);

        try
        {
            return method.Invoke(target, arguments);
        }

        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static object? Read(object target, string name) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(target.GetType().Name, name)).GetValue(target);

    // Both sides refuse invalid input with ArgumentException or a subclass; anything else ends
    // the smoke where it is thrown.
    private static void SameOutcome(Action reference, Action candidate, string label)
    {
        var expected = Refusal(reference);
        var actual = Refusal(candidate);
        Expect(actual?.GetType() == expected?.GetType(), label + ": " + (actual ?? expected)?.Message);
    }

    private static void SameResult(Func<object?> reference, Func<object?> candidate, string label)
    {
        object? expected = null, actual = null;
        var refused = Refusal(() => expected = reference());
        var failed = Refusal(() => actual = candidate());
        Expect(failed?.GetType() == refused?.GetType(), label + ": " + (failed ?? refused)?.Message);
        Expect(
            expected is string[] list ? Same(actual as string[], list) : Equals(actual, expected),
            label + " result"
        );
    }

    private static ArgumentException? Refusal(Action action)
    {
        try
        {
            action();
            return null;
        }

        catch (ArgumentException error)
        {
            return error;
        }
    }

    // A comparison inside a counted check; it throws without counting.
    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool Same(string?[]? first, string?[]? second) =>
        first == null ? second == null : second != null && first.SequenceEqual(second);
}
