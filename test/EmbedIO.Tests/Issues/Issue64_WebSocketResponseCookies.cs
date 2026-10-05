using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue64_WebSocketResponseCookies
    {
        [TestCase(HttpListenerMode.EmbedIO, false, false)]
        [TestCase(HttpListenerMode.EmbedIO, false, true)]
        [TestCase(HttpListenerMode.EmbedIO, true, false)]
        [TestCase(HttpListenerMode.EmbedIO, true, true)]
        [TestCase(HttpListenerMode.Microsoft, false, false)]
        [TestCase(HttpListenerMode.Microsoft, false, true)]
        [TestCase(HttpListenerMode.Microsoft, true, false)]
        [TestCase(HttpListenerMode.Microsoft, true, true)]
        public Task ConfiguredCookiesSurviveUpgrade(HttpListenerMode mode, bool add, bool collision)
            => UseServerAsync(mode, context =>
            {
                var cookie = new Cookie("auth", "new", "/socket", "localhost")
                {
                    HttpOnly = true,
                    Secure = true,
                    Expires = DateTime.UtcNow.AddHours(1)
                };
                if (add)
                {
                    context.Response.Cookies.Add(new Cookie("auth", "replaced", "/socket", "localhost"));
                    context.Response.Cookies.Add(cookie);
                }
                else
                    context.Response.SetCookie(cookie);
                context.Response.SetCookie(new Cookie("second", "\"left,right\"", "/"));
            }, async (socket, http, url) =>
            {
                socket.Options.Cookies = new CookieContainer();
                socket.Options.Cookies.Add(new Uri(url), new Cookie("legacy", "keep"));
                if (collision)
                    socket.Options.Cookies.Add(new Uri(url), new Cookie("AUTH", "old"));
                await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "socket"), CancellationToken.None);
                var headers = socket.HttpResponseHeaders!["Set-Cookie"].ToArray();
                Assert.That(headers.Length, Is.EqualTo(mode == HttpListenerMode.EmbedIO ? 3 : 2));
                var auth = headers.Single(h => h.StartsWith("auth=", StringComparison.OrdinalIgnoreCase));
                Assert.That(auth, Does.StartWith("auth=new;"));
                Assert.That(auth, Does.Contain("Path=/socket"));
                Assert.That(auth, Does.Contain("Domain=localhost"));
                Assert.That(HasAttribute(auth, "HttpOnly"), Is.True);
                Assert.That(HasAttribute(auth, "Secure"), Is.True);
                var jar = new CookieContainer();
                jar.SetCookies(new Uri(url.Replace("http://", "https://") + "socket"), auth);
                Assert.That(jar.GetCookies(new Uri(url.Replace("http://", "https://") + "socket"))["auth"]!.Expires.ToUniversalTime(),
                    Is.InRange(DateTime.UtcNow.AddMinutes(58), DateTime.UtcNow.AddMinutes(61)));
                var second = headers.Single(h => h.StartsWith("second=", StringComparison.Ordinal));
                Assert.That(second, Does.StartWith("second=\"left,right\";"));
                Assert.That(HasAttribute(second, "HttpOnly"), Is.False);
                Assert.That(HasAttribute(second, "Secure"), Is.False);
                if (mode == HttpListenerMode.EmbedIO)
                    Assert.That(headers, Does.Contain("legacy=keep"));
                await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("hello")), WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[64];
                var reply = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                Assert.That(Encoding.UTF8.GetString(buffer, 0, reply.Count), Is.EqualTo("hello"));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task SameNameCookiesRetainSeparateExplicitScopes(HttpListenerMode mode)
            => UseServerAsync(mode, context =>
            {
                context.Response.SetCookie(new Cookie("scoped", "one", "/socket") { HttpOnly = true });
                context.Response.SetCookie(new Cookie("scoped", "two", "/other") { HttpOnly = true });
            }, async (socket, http, url) =>
            {
                socket.Options.Cookies = new CookieContainer();
                socket.Options.Cookies.Add(new Uri(url), new Cookie("scoped", "old"));
                await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "socket"), CancellationToken.None);
                var headers = socket.HttpResponseHeaders!["Set-Cookie"].ToArray();
                Assert.That(headers.Length, Is.EqualTo(2));
                Assert.That(headers.Any(h => h.StartsWith("scoped=one;", StringComparison.Ordinal) && h.Contains("Path=/socket")), Is.True);
                Assert.That(headers.Any(h => h.StartsWith("scoped=two;", StringComparison.Ordinal) && h.Contains("Path=/other")), Is.True);
                Assert.That(headers.All(h => HasAttribute(h, "HttpOnly")), Is.True);
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task RejectedUpgradeKeepsOrdinaryResponseCookies(HttpListenerMode mode)
            => UseServerAsync(mode, context => context.Response.SetCookie(new Cookie("rejected", "yes", "/") { HttpOnly = true, Secure = true }),
                async (socket, http, url) =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url + "socket");
                    request.Headers.Add("Connection", "Upgrade");
                    request.Headers.Add("Upgrade", "websocket");
                    request.Headers.Add("Sec-WebSocket-Version", "13");
                    request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
                    request.Headers.Add("Sec-WebSocket-Protocol", "unsupported");
                    using var response = await http.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                    var header = response.Headers.GetValues("Set-Cookie").Single();
                    Assert.That(header, Does.StartWith("rejected=yes;"));
                    Assert.That(HasAttribute(header, "HttpOnly"), Is.True);
                    Assert.That(HasAttribute(header, "Secure"), Is.True);
                }, requireProtocol: true);

        private static bool HasAttribute(string header, string attribute)
            => header.Split(';').Any(value => value.Trim().Equals(attribute, StringComparison.OrdinalIgnoreCase));

        private static async Task UseServerAsync(HttpListenerMode mode, Action<IHttpContext> configureCookies,
            Func<ClientWebSocket, HttpClient, string, Task> exercise, bool requireProtocol = false)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new CookieMiddleware(configureCookies)).WithModule(new EchoSocket(requireProtocol))
                .OnGet("/health", context => context.SendStringAsync("healthy", "text/plain", Encoding.UTF8));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            try
            {
                await exercise(socket, http, url).WaitAsync(TimeSpan.FromSeconds(10));
                if (socket.State == WebSocketState.Open)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await http.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
            }
            finally
            {
                socket.Abort();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class CookieMiddleware : WebModuleBase
        {
            private readonly Action<IHttpContext> _configure;
            public CookieMiddleware(Action<IHttpContext> configure) : base("/socket") => _configure = configure;
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context)
            {
                _configure(context);
                return Task.CompletedTask;
            }
        }

        private sealed class EchoSocket : WebSocketModule
        {
            public EchoSocket(bool requireProtocol) : base("/socket", false)
            {
                if (requireProtocol) AddProtocol("accepted");
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => SendAsync(context, buffer);
        }
    }
}
