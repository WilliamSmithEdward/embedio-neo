using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Sessions;
using System.Net.WebSockets;
using EmbedIO.WebSockets;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue59_CookieAttributes
    {
        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.Microsoft, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.Microsoft, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true)]
        [TestCase(HttpListenerMode.Microsoft, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        [TestCase(HttpListenerMode.Microsoft, true, true)]
        public Task CookieFlagsAndMetadataSurviveLateConfiguration(HttpListenerMode mode, bool httpOnly, bool secure)
            => UseServerAsync(mode, server => server.OnAny(async context =>
            {
                // Acquiring the stream must not commit headers or snapshot cookies.
                var output = context.Response.OutputStream;
                await output.FlushAsync();
                context.Response.Cookies.Add(new Cookie("primary", "old", "/area"));
                context.Response.Cookies.Add(new Cookie("primary", "new", "/area")
                {
                    HttpOnly = httpOnly,
                    Secure = secure,
                    Expires = DateTime.UtcNow.AddHours(1)
                });
                context.Response.SetCookie(new Cookie("secondary", "two", "/") { HttpOnly = true });
                // Mutation after adding also remains visible until the first write.
                context.Response.Cookies["primary"]!.Domain = "localhost";
                context.Response.ContentLength64 = 2;
                await output.WriteAsync(Encoding.ASCII.GetBytes("ok"));
            }), async (client, url) =>
            {
                using var response = await client.GetAsync(url);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                var headers = response.Headers.GetValues("Set-Cookie").ToArray();
                var parsed = new CookieContainer();
                foreach (var header in headers)
                    parsed.SetCookies(new Uri(url.Replace("http://", "https://") + "area/"), header);
                var primary = parsed.GetCookies(new Uri(url.Replace("http://", "https://") + "area/"))["primary"]!;
                Assert.That(primary, Is.Not.Null);
                Assert.That(primary.Value, Is.EqualTo("new"));
                Assert.That(primary.Path, Is.EqualTo("/area"));
                Assert.That(primary.Domain.TrimStart('.'), Is.EqualTo("localhost"));
                Assert.That(primary.Expires.ToUniversalTime(), Is.InRange(DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(61)));
                // Raw headers verify Secure even when the test transport is HTTP.
                var primaryHeader = headers.Single(h => h.StartsWith("primary=", StringComparison.Ordinal));
                Assert.That(HasAttribute(primaryHeader, "HttpOnly"), Is.EqualTo(httpOnly));
                Assert.That(HasAttribute(primaryHeader, "Secure"), Is.EqualTo(secure));
                Assert.That(headers.Count(h => h.StartsWith("primary=", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(headers.Count(h => h.StartsWith("secondary=", StringComparison.Ordinal)), Is.EqualTo(1));
                Assert.That(HasAttribute(headers.Single(h => h.StartsWith("secondary=", StringComparison.Ordinal)), "HttpOnly"), Is.True);
            });

        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        public Task SessionPolicyAndPersistenceSurviveSerialization(HttpListenerMode mode, bool httpOnly)
            => UseServerAsync(mode, server => server.WithSessionManager(new LocalSessionManager { CookieHttpOnly = httpOnly })
                .OnAny(context =>
                {
                    context.Session["count"] = context.Session.GetValue<int>("count") + 1;
                    return context.SendStringAsync(context.Session.GetValue<int>("count").ToString(), "text/plain", Encoding.UTF8);
                }), async (client, url) =>
                {
                    using var response = await client.GetAsync(url);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("1"));
                    var header = response.Headers.GetValues("Set-Cookie").Single();
                    Assert.That(HasAttribute(header, "HttpOnly"), Is.EqualTo(httpOnly));
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("2"));
                });

        [TestCase(HttpListenerMode.EmbedIO, "GET")]
        [TestCase(HttpListenerMode.Microsoft, "GET")]
        [TestCase(HttpListenerMode.EmbedIO, "HEAD")]
        [TestCase(HttpListenerMode.Microsoft, "HEAD")]
        public Task EmptyResponsesEmitCookieAttributes(HttpListenerMode mode, string method)
            => UseServerAsync(mode, server => server.OnAny(context =>
            {
                context.Response.SetCookie(new Cookie("empty", "yes", "/") { HttpOnly = true, Secure = true });
                context.Response.ContentLength64 = 0;
                return Task.CompletedTask;
            }), async (client, url) =>
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                using var response = await client.SendAsync(request);
                var header = response.Headers.GetValues("Set-Cookie").Single();
                Assert.That(HasAttribute(header, "HttpOnly"), Is.True);
                Assert.That(HasAttribute(header, "Secure"), Is.True);
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
            });

        [TestCase(false)]
        [TestCase(true)]
        public Task NativeLegacyAttributesAndSetCookieCloningRemainIntact(bool portCookie)
            => UseServerAsync(HttpListenerMode.Microsoft, server => server.OnAny(async context =>
            {
                var cookie = new Cookie("legacy", "original", "/area", "localhost")
                {
                    Version = 1,
                    Comment = "retained",
                    CommentUri = new Uri("https://example.com/cookie"),
                    Discard = true,
                    HttpOnly = true,
                    Secure = true,
                    Expires = DateTime.UtcNow.AddMinutes(-1)
                };
                if (portCookie)
                    cookie.Port = "\"80\"";
                context.Response.SetCookie(cookie);
                cookie.Value = "changed-after-set";
                context.Response.Headers.Add("Set-Cookie", "manual=preserved; Path=/; HttpOnly");
                await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            }), async (client, url) =>
            {
                using var response = await client.GetAsync(url);
                var name = portCookie ? "Set-Cookie2" : "Set-Cookie";
                var header = response.Headers.GetValues(name).Single(h => h.StartsWith("legacy=", StringComparison.Ordinal));
                Assert.That(header, Does.StartWith("legacy=original;"));
                Assert.That(header, Does.Contain("Comment=retained"));
                Assert.That(header, Does.Contain("CommentURL=\"https://example.com/cookie\""));
                Assert.That(header, Does.Contain("Domain=localhost"));
                Assert.That(header, Does.Contain("Path=/area"));
                Assert.That(header, Does.Contain("Max-Age=0"));
                Assert.That(header, Does.Contain("Version=1"));
                Assert.That(HasAttribute(header, "Discard"), Is.True);
                Assert.That(HasAttribute(header, "HttpOnly"), Is.True);
                Assert.That(HasAttribute(header, "Secure"), Is.True);
                if (portCookie)
                    Assert.That(header, Does.Contain("Port=\"80\""));
                Assert.That(response.Headers.GetValues("Set-Cookie").Count(h => h.StartsWith("manual=", StringComparison.Ordinal)), Is.EqualTo(1));
            });

        [TestCase(HttpListenerMode.Microsoft)]
        public Task WebSocketHandshakeEmitsProtectedCookies(HttpListenerMode mode)
            => UseServerAsync(mode, server => server.WithModule(new CookieMiddleware())
                .WithModule(new CookieSocket()), async (_, url) =>
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var socket = new ClientWebSocket();
                socket.Options.CollectHttpResponseDetails = true;
                await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "socket"), timeout.Token);
                var header = socket.HttpResponseHeaders!["Set-Cookie"].Single();
                Assert.That(HasAttribute(header, "HttpOnly"), Is.True);
                Assert.That(HasAttribute(header, "Secure"), Is.True);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            });

        private sealed class CookieMiddleware : WebModuleBase
        {
            public CookieMiddleware() : base("/socket") { }
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.Response.SetCookie(new Cookie("handshake", "yes", "/") { HttpOnly = true, Secure = true });
                return Task.CompletedTask;
            }
        }

        private sealed class CookieSocket : WebSocketModule
        {
            public CookieSocket() : base("/socket", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Task.CompletedTask;
        }
        [Test]
        public Task RejectedWritesDoNotCommitCookieHeaders()
            => UseServerAsync(HttpListenerMode.Microsoft, server => server.OnAny(async context =>
            {
                var output = context.Response.OutputStream;
                Assert.Throws<ArgumentException>(() => output.Write(new byte[1], 1, 1));
                await Assert.ThrowsAsync<ArgumentException>(async () => await output.WriteAsync(new byte[1], 1, 1));
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Assert.ThrowsAsync<TaskCanceledException>(async () => await output.WriteAsync(new byte[1], 0, 1, canceled.Token));
                context.Response.SetCookie(new Cookie("after-rejection", "yes", "/") { HttpOnly = true });
                context.Response.ContentLength64 = 2;
                await output.WriteAsync(Encoding.ASCII.GetBytes("ok"));
            }), async (client, url) =>
            {
                using var response = await client.GetAsync(url);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                Assert.That(HasAttribute(response.Headers.GetValues("Set-Cookie").Single(), "HttpOnly"), Is.True);
            });
        private static bool HasAttribute(string header, string attribute)
            => header.Split(';').Any(value => value.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

        private static async Task UseServerAsync(HttpListenerMode mode, Action<WebServer> configure, Func<HttpClient, string, Task> exercise)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode));
            configure(server);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                await exercise(client, url);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
