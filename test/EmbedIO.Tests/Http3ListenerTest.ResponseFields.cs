using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        // The engine keeps a response's fields in a list until something reads Headers, which
        // builds the WebHeaderCollection. Each protocol must send the same head, and leave the same
        // Headers after the response, whether the handler read Headers first or not; a handler
        // that never touches Headers leaves the collection unbuilt until then. The list keeps the
        // size its response gave it: the compressed string, with Alt-Svc, Vary, Content-Encoding
        // and on HTTP/1.1 chunked framing and keep-alive, is the largest head the engine writes.
        [Test]
        public async Task ResponseFieldsMatchWhetherOrNotHeadersWasRead()
        {
            using var certificate = Certificate();
            var prefix = CombinedPrefix();
            var after = new ConcurrentDictionary<string, TaskCompletionSource<(bool Built, int Room, string Headers)>>();
            using var server = new WebServer(HttpListenerMode.EmbedIOCombined, certificate, prefix).WithAction(
                "/",
                HttpVerbs.Any,
                async context =>
                {
                    var query = context.Request.QueryString;
                    var recorded = Recorded(after, query["id"] ?? "");
                    context.OnClose(closed =>
                    {
                        var fields = EngineInternals.Property(closed.Response, "Fields")
                            ?? throw new AssertionException("Missing field list.");
                        var built = EngineInternals.Field(fields, "_collection") != null;
                        var room = EngineInternals.Field(fields, "_fields") is Array list ? list.Length : -1;
                        recorded.TrySetResult((built, room, Snapshot(closed.Response.Headers)));
                    });

                    if (query["read"] == "1") Assert.That(context.Response.Headers.Count, Is.Zero);

                    switch (query["shape"])
                    {
                        case "length":
                            context.Response.ContentType = "application/json";
                            context.Response.ContentLength64 = 2;
                            await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("{}"));
                            break;
                        case "chunked":
                            context.Response.SendChunked = true;
                            await context.Response.OutputStream.WriteAsync(Encoding.ASCII.GetBytes("chunk"));
                            break;
                        case "empty":
                            context.Response.StatusCode = 204;
                            break;
                        case "custom":
                            context.Response.Headers["X-Custom"] = "1";
                            await context.SendStringAsync("custom", "text/plain", WebServer.Utf8NoBomEncoding);
                            break;
                        case "cookies":
                            context.Response.SetCookie(new Cookie("one", "1", "/") { HttpOnly = true });
                            context.Response.SetCookie(new Cookie("two", "2"));
                            await context.SendStringAsync("cookies", "text/plain", WebServer.Utf8NoBomEncoding);
                            break;
                        case "json":
                            await ResponseSerializer.Json(context, new { value = 1 });
                            break;
                        default:
                            await context.SendStringAsync("hello", "text/plain", WebServer.Utf8NoBomEncoding);
                            break;
                    }
                }
            );
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = server.RunAsync(stop.Token);

            try
            {
                using var client = Client(certificate);
                var request = 0;

                foreach (var version in new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 })
                {
                    foreach (var shape in new[] { "length", "chunked", "empty", "custom", "cookies", "json", "string", "gzip" })
                    {
                        var results = new string[2];

                        for (var read = 0; read < 2; read++)
                        {
                            var id = (++request).ToString(CultureInfo.InvariantCulture);
                            using var message = new HttpRequestMessage(
                                HttpMethod.Get,
                                $"{prefix}?id={id}&read={read}&shape={shape}"
                            )
                            {
                                Version = version,
                                VersionPolicy = HttpVersionPolicy.RequestVersionExact
                            };

                            // The string shape again, compressed.
                            if (shape == "gzip") message.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");

                            using var response = await client.SendAsync(message, stop.Token);
                            var body = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync(stop.Token));
                            var (built, room, headers) = await Recorded(after, id).Task.WaitAsync(TimeSpan.FromSeconds(10));
                            Assert.That(built, Is.EqualTo(read == 1 || shape == "custom"), $"{version} {shape} read={read}");
                            Assert.That(room, Is.EqualTo(version == HttpVersion.Version11 ? 9 : 7), $"{version} {shape} read={read} room");
                            results[read] =
                                $"{(int)response.StatusCode} {response.Version}\n{Received(response)}\n{body}\n---\n{headers}";
                        }

                        Assert.That(results[0], Is.EqualTo(results[1]), $"{version} {shape}");
                        Assert.That(results[0], Does.Contain("Server: "), $"{version} {shape}: Headers holds the engine's fields");
                    }
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static TaskCompletionSource<(bool Built, int Room, string Headers)> Recorded(
            ConcurrentDictionary<string, TaskCompletionSource<(bool Built, int Room, string Headers)>> after,
            string id
        ) =>
            after.GetOrAdd(
                id,
                _ => new TaskCompletionSource<(bool Built, int Room, string Headers)>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )
            );

        // The Date field differs from one second to the next and is left out.
        private static string Received(HttpResponseMessage response) =>
            string.Join(
                "\n",
                response.Headers
                    .Concat(response.Content.Headers)
                    .Where(field => field.Key != "Date")
                    .Select(field => field.Key + ": " + string.Join("|", field.Value))
            );

        private static string Snapshot(WebHeaderCollection headers)
        {
            var text = new StringBuilder();

            for (var index = 0; index < headers.Count; index++)
            {
                var name = headers.GetKey(index);
                text.Append(name).Append(": ");

                if (name != "Date") text.AppendJoin("|", headers.GetValues(index) ?? []);

                text.Append('\n');
            }

            return text.ToString();
        }
    }
}
