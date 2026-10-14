using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.PlatformTests;
using EmbedIO.Problems;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // The engine takes its reason phrases from HttpStatusDescription. These final statuses used
    // to go out without a phrase; 499 has no standard phrase and still goes out without one.
    public sealed class StatusPhraseTest
    {
        private static readonly int[] AddedFinalStatuses = { 208, 226, 308, 421, 426, 428, 429, 431, 451, 506, 508, 510, 511 };

        // HttpStatusDescription's public table before it began delegating to the engine's table.
        private static readonly (int Code, string Phrase)[] PublicTable =
        {
            (100, "Continue"),
            (101, "Switching Protocols"),
            (102, "Processing"),
            (103, "Early Hints"),
            (200, "OK"),
            (201, "Created"),
            (202, "Accepted"),
            (203, "Non-Authoritative Information"),
            (204, "No Content"),
            (205, "Reset Content"),
            (206, "Partial Content"),
            (207, "Multi-Status"),
            (208, "Already Reported"),
            (226, "IM Used"),
            (300, "Multiple Choices"),
            (301, "Moved Permanently"),
            (302, "Found"),
            (303, "See Other"),
            (304, "Not Modified"),
            (305, "Use Proxy"),
            (307, "Temporary Redirect"),
            (308, "Permanent Redirect"),
            (400, "Bad Request"),
            (401, "Unauthorized"),
            (402, "Payment Required"),
            (403, "Forbidden"),
            (404, "Not Found"),
            (405, "Method Not Allowed"),
            (406, "Not Acceptable"),
            (407, "Proxy Authentication Required"),
            (408, "Request Timeout"),
            (409, "Conflict"),
            (410, "Gone"),
            (411, "Length Required"),
            (412, "Precondition Failed"),
            (413, "Request Entity Too Large"),
            (414, "Request-Uri Too Long"),
            (415, "Unsupported Media Type"),
            (416, "Requested Range Not Satisfiable"),
            (417, "Expectation Failed"),
            (421, "Misdirected Request"),
            (422, "Unprocessable Entity"),
            (423, "Locked"),
            (424, "Failed Dependency"),
            (426, "Upgrade Required"),
            (428, "Precondition Required"),
            (429, "Too Many Requests"),
            (431, "Request Header Fields Too Large"),
            (451, "Unavailable For Legal Reasons"),
            (500, "Internal Server Error"),
            (501, "Not Implemented"),
            (502, "Bad Gateway"),
            (503, "Service Unavailable"),
            (504, "Gateway Timeout"),
            (505, "Http Version Not Supported"),
            (506, "Variant Also Negotiates"),
            (507, "Insufficient Storage"),
            (508, "Loop Detected"),
            (510, "Not Extended"),
            (511, "Network Authentication Required"),
        };

        [Test]
        public void EveryPublicTableEntryRoundTripsAndNoOtherCodeIsKnown()
        {
            var expected = new Dictionary<int, string>();
            foreach (var (code, phrase) in PublicTable)
                expected.Add(code, phrase);
            var mismatches = new List<string>();
            foreach (var code in new[] { int.MinValue, -1, int.MaxValue }.Concat(Enumerable.Range(0, 1000)))
            {
                expected.TryGetValue(code, out var phrase);
                var found = HttpStatusDescription.TryGet(code, out var described);
                var typedFound = HttpStatusDescription.TryGet((HttpStatusCode)code, out var typed);
                if (found != (phrase != null) || typedFound != found || described != phrase || typed != phrase
                    || HttpStatusDescription.Get(code) != phrase || HttpStatusDescription.Get((HttpStatusCode)code) != phrase)
                    mismatches.Add($"{code}: expected {phrase ?? "<none>"}, got {described ?? "<none>"}");
            }

            Assert.That(expected, Has.Count.EqualTo(60));
            Assert.That(mismatches, Is.Empty);
        }

        [Test]
        public async Task Http1StatusLinesCarryTheStandardReasonPhrase()
        {
            var statuses = new List<int> { 200, 404 };
            statuses.AddRange(AddedFinalStatuses);
            statuses.Add(499);
            var url = HttpsSmoke.GetUrl().Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .OnGet("/", context =>
                {
                    context.Response.StatusCode = int.Parse(context.Request.QueryString["status"] ?? "0", CultureInfo.InvariantCulture);
                    context.Response.ContentLength64 = 0;
                    return Task.CompletedTask;
                });
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.Listener.IsListening, Is.True, $"The fixture must own its endpoint: {url}");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var uri = new Uri(url);
                var lines = new List<string>();
                foreach (var status in statuses)
                {
                    using var socket = new TcpClient();
                    await socket.ConnectAsync(uri.Host, uri.Port, timeout.Token);
                    var stream = socket.GetStream();
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /?status={status} HTTP/1.1\r\nHost: {uri.Authority}\r\nConnection: close\r\n\r\n"), timeout.Token);
                    using var bytes = new MemoryStream();
                    await stream.CopyToAsync(bytes, timeout.Token);
                    var text = Encoding.ASCII.GetString(bytes.ToArray());
                    lines.Add(text.Substring(0, Math.Max(0, text.IndexOf("\r\n", StringComparison.Ordinal))));
                }

                Assert.That(lines, Does.Contain("HTTP/1.1 429 Too Many Requests"));
                Assert.That(lines, Is.EqualTo(statuses.ConvertAll(status => $"HTTP/1.1 {status} {HttpStatusDescription.Get(status)}")));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [Test]
        public void ProblemTitlesUseTheStandardReasonPhrase()
        {
            var statuses = Array.FindAll(AddedFinalStatuses, status => status >= 400);
            var titles = Array.ConvertAll(statuses, status => JsonNode.Parse(new ProblemDetails(status).ToJson())?["title"]?.GetValue<string>());
            Assert.That(titles, Is.EqualTo(Array.ConvertAll(statuses, status => HttpStatusDescription.Get(status))));
            Assert.That(JsonNode.Parse(new ProblemDetails(499).ToJson())?["title"], Is.Null);
        }

        [Test]
        public void OpenApiResponsesDefaultToTheStandardReasonPhrase()
        {
            var document = new OpenApiDocumentBuilder("Phrases", "1.0")
                .AddOperation(HttpVerbs.Get, "/limited", operation => operation.Response(200).Response(429).Response(499))
                .Build(EmbedIO.Serialization.Json.CreateOptions());
            var responses = document.Root["paths"]?["/limited"]?["get"]?["responses"];
            Assert.That(responses?["429"]?["description"]?.GetValue<string>(), Is.EqualTo("Too Many Requests"));
            Assert.That(responses?["499"]?["description"]?.GetValue<string>(), Is.EqualTo("Status 499"));
        }
    }
}
