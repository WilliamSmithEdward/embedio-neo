using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using EmbedIO.Sessions;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue479_SameSiteCookies
    {
        [TestCase(HttpListenerMode.EmbedIO, "entry=value; Path=/; SameSite=Lax; HttpOnly")]
        [TestCase(HttpListenerMode.Microsoft, "entry=value; Path=/; SameSite=Lax; HttpOnly")]
        [TestCase(HttpListenerMode.EmbedIO, "entry=value; Path=/; SameSite=None; Secure; Priority=High")]
        [TestCase(HttpListenerMode.Microsoft, "entry=value; Path=/; SameSite=None; Secure; Priority=High")]
        [TestCase(HttpListenerMode.EmbedIO, "entry=value; Expires=Wed, 09 Jun 2032 10:18:14 GMT; Max-Age=42; SameSite=Strict")]
        [TestCase(HttpListenerMode.Microsoft, "entry=value; Expires=Wed, 09 Jun 2032 10:18:14 GMT; Max-Age=42; SameSite=Strict")]
        [TestCase(HttpListenerMode.EmbedIO, "entry=value; SameSite=None; Secure; Partitioned")]
        [TestCase(HttpListenerMode.Microsoft, "entry=value; SameSite=None; Secure; Partitioned")]
        public async Task RawCookieAttributesRemainIntact(HttpListenerMode mode, string cookie)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .OnGet("/", async c =>
                {
                    c.Response.Headers.Add("Set-Cookie", cookie);
                    c.Response.Headers.Add("Set-Cookie", "second=two; Path=/other; SameSite=Strict");
                    await c.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                Assert.That(response.Headers.GetValues("Set-Cookie").ToArray(),
                    Is.EqualTo(new[] { cookie, "second=two; Path=/other; SameSite=Strict" }));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task RawCookiesSurviveWebSocketUpgrade(HttpListenerMode mode)
        {
            const string cookie = "entry=value; Path=/socket; SameSite=Lax; HttpOnly";
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new CookieMiddleware(c => c.Response.Headers.Add("Set-Cookie", cookie)))
                .WithModule(new EchoSocket());
            var running = server.RunAsync(stop.Token);
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "socket"), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(socket.HttpResponseHeaders!["Set-Cookie"].ToArray(), Is.EqualTo(new[] { cookie }));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { socket.Abort(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.None)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.None)]
        public Task TypedPolicyPreservesFlagsAndMutableScope(HttpListenerMode mode, CookieSameSiteMode policy)
            => UseHttp(mode, c =>
            {
                c.Response.SetCookie(new Cookie("entry", "one", "/before") { Secure = policy == CookieSameSiteMode.None }, policy);
                var stored = c.Response.Cookies["entry"]!;
                stored.Path = "/changed";
                stored.Value = "two";
                stored.HttpOnly = true;
            }, headers =>
            {
                Assert.That(headers.Length, Is.EqualTo(1));
                Assert.That(headers[0], Does.StartWith("entry=two;"));
                Assert.That(headers[0], Does.Contain("; Path=/changed"));
                Assert.That(headers[0], Does.Contain("; SameSite=" + policy));
                Assert.That(headers[0], Does.Contain("; HttpOnly"));
                Assert.That(headers[0].Contains("; Secure", StringComparison.Ordinal), Is.EqualTo(policy == CookieSameSiteMode.None));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task InvalidPoliciesDoNotAddCookies(HttpListenerMode mode)
            => UseHttp(mode, c =>
            {
                Assert.Throws<ArgumentException>(() => c.Response.SetCookie(new Cookie("bad", "one"), CookieSameSiteMode.None));
                Assert.Throws<ArgumentOutOfRangeException>(() => c.Response.SetCookie(new Cookie("bad", "one"), (CookieSameSiteMode)99));
                Assert.Throws<ArgumentNullException>(() => c.Response.SetCookie(null!, CookieSameSiteMode.Lax));
                Assert.That(c.Response.Cookies.Count, Is.Zero);
                c.Response.SetCookie(new Cookie("healthy", "yes"), CookieSameSiteMode.Lax);
            }, headers => Assert.That(headers.Single(), Does.StartWith("healthy=yes;")));

        [Test]
        public void SessionPolicyConfigurationValidationAndLocking()
        {
            var manager = new LocalSessionManager();
            Assert.That(manager.CookieSameSite, Is.Null);
            Assert.That(manager.CookieSecure, Is.False);
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.CookieSameSite = (CookieSameSiteMode)99);
            manager.CookieSameSite = CookieSameSiteMode.None;
            using var stop = new CancellationTokenSource();
            Assert.Throws<InvalidOperationException>(() => manager.Start(stop.Token));
            manager.CookieSecure = true;
            manager.Start(stop.Token);
            try
            {
                Assert.Throws<InvalidOperationException>(() => manager.CookieSecure = false);
                Assert.Throws<InvalidOperationException>(() => manager.CookieSameSite = null);
            }
            finally { stop.Cancel(); }
        }

        [TestCase(HttpListenerMode.EmbedIO, -1)]
        [TestCase(HttpListenerMode.Microsoft, -1)]
        [TestCase(HttpListenerMode.EmbedIO, (int)CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.Microsoft, (int)CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.EmbedIO, (int)CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.Microsoft, (int)CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.EmbedIO, (int)CookieSameSiteMode.None)]
        [TestCase(HttpListenerMode.Microsoft, (int)CookieSameSiteMode.None)]
        public async Task SessionCreationAndDeletionPreserveSelectedPolicy(HttpListenerMode mode, int policy)
        {
            var url = Resources.GetServerAddress();
            var manager = new LocalSessionManager
            {
                CookieSameSite = policy < 0 ? null : (CookieSameSiteMode)policy,
                CookieSecure = policy == (int)CookieSameSiteMode.None,
            };
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithSessionManager(manager).OnGet("/", async c =>
                {
                    c.Session["state"] = "yes";
                    if (c.Request.Url.AbsolutePath == "/delete") c.Session.Delete();
                    await c.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient(new HttpClientHandler { UseCookies = false });
                using var create = await client.GetAsync(url);
                var initial = create.Headers.GetValues("Set-Cookie").Single();
                var pair = initial.Split(';')[0];
                using var request = new HttpRequestMessage(HttpMethod.Get, url + "delete");
                request.Headers.Add("Cookie", pair);
                using var deleted = await client.SendAsync(request);
                foreach (var header in new[] { initial, deleted.Headers.GetValues("Set-Cookie").Last() })
                {
                    Assert.That(header, Does.StartWith("__session="));
                    Assert.That(header, Does.Contain("; HttpOnly"));
                    Assert.That(header.Contains("; Secure", StringComparison.Ordinal), Is.EqualTo(manager.CookieSecure));
                    if (policy < 0) Assert.That(header, Does.Not.Contain("SameSite"));
                    else Assert.That(header, Does.Contain("; SameSite=" + (CookieSameSiteMode)policy));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task RawAndTypedCookiesAreIndependent(HttpListenerMode mode)
            => UseHttp(mode, c =>
            {
                c.Response.SetCookie(new Cookie("typed", "one", "/"), CookieSameSiteMode.Lax);
                c.Response.SetCookie(new Cookie("legacy", "two", "/") { HttpOnly = true });
                c.Response.Headers.Add("set-cookie", "raw=three; SameSite=Strict; Max-Age=12; Path=/");
            }, headers =>
            {
                Assert.That(headers.Length, Is.EqualTo(3));
                Assert.That(headers.Single(h => h.StartsWith("typed=", StringComparison.Ordinal)), Does.Contain("; SameSite=Lax"));
                Assert.That(headers.Single(h => h.StartsWith("legacy=", StringComparison.Ordinal)), Does.Not.Contain("SameSite"));
                Assert.That(headers.Single(h => h.StartsWith("raw=", StringComparison.Ordinal)), Is.EqualTo("raw=three; SameSite=Strict; Max-Age=12; Path=/"));
            });

        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.Lax)]
        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.Strict)]
        [TestCase(HttpListenerMode.EmbedIO, CookieSameSiteMode.None)]
        [TestCase(HttpListenerMode.Microsoft, CookieSameSiteMode.None)]
        public async Task TypedPolicySurvivesWebSocketUpgrade(HttpListenerMode mode, CookieSameSiteMode policy)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithModule(new CookieMiddleware(c => c.Response.SetCookie(
                    new Cookie("entry", "value", "/socket") { Secure = policy == CookieSameSiteMode.None, HttpOnly = true }, policy)))
                .WithModule(new EchoSocket());
            var running = server.RunAsync(stop.Token);
            using var socket = new ClientWebSocket();
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "socket"), CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
                var header = socket.HttpResponseHeaders!["Set-Cookie"].Single();
                Assert.That(header, Does.Contain("; SameSite=" + policy));
                Assert.That(header, Does.Contain("; HttpOnly"));
                Assert.That(header.Contains("; Secure", StringComparison.Ordinal), Is.EqualTo(policy == CookieSameSiteMode.None));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { socket.Abort(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task SharedCookiePoliciesStayScopedToEachResponse(HttpListenerMode mode)
        {
            var shared = new Cookie("shared", "value", "/");
            var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).OnGet("/", async c =>
            {
                var policy = c.Request.Url.AbsolutePath == "/lax" ? CookieSameSiteMode.Lax : CookieSameSiteMode.Strict;
                c.Response.SetCookie(shared, policy);
                if (Interlocked.Increment(ref entered) == 2) bothEntered.SetResult();
                await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await c.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                var responses = await Task.WhenAll(client.GetAsync(url + "lax"), client.GetAsync(url + "strict"))
                    .WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    Assert.That(responses[0].Headers.GetValues("Set-Cookie").Single(), Does.Contain("; SameSite=Lax"));
                    Assert.That(responses[1].Headers.GetValues("Set-Cookie").Single(), Does.Contain("; SameSite=Strict"));
                }
                finally { foreach (var response in responses) response.Dispose(); }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task SameNameCookiesRetainTheirOwnScopePolicy(HttpListenerMode mode)
            => UseHttp(mode, c =>
            {
                c.Response.SetCookie(new Cookie("scoped", "first", "/first"), CookieSameSiteMode.Lax);
                c.Response.SetCookie(new Cookie("scoped", "second", "/second"), CookieSameSiteMode.Strict);
            }, headers =>
            {
                Assert.That(headers.Length, Is.EqualTo(2));
                Assert.That(headers.Single(h => h.StartsWith("scoped=first", StringComparison.Ordinal)), Does.Contain("; SameSite=Lax"));
                Assert.That(headers.Single(h => h.StartsWith("scoped=second", StringComparison.Ordinal)), Does.Contain("; SameSite=Strict"));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task RawPolicyCoexistsWithUnmodifiedLegacyCookie(HttpListenerMode mode)
            => UseHttp(mode, c =>
            {
                c.Response.SetCookie(new Cookie("legacy", "one", "/"));
                c.Response.Headers.Add("Set-Cookie", "raw=two; Path=/; SameSite=Lax");
            }, headers =>
            {
                Assert.That(headers.Length, Is.EqualTo(2));
                Assert.That(headers.Single(h => h.StartsWith("legacy=", StringComparison.Ordinal)), Does.Not.Contain("SameSite"));
                Assert.That(headers.Single(h => h.StartsWith("raw=", StringComparison.Ordinal)), Is.EqualTo("raw=two; Path=/; SameSite=Lax"));
            });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task LongLivedCookieDoesNotExpireWhenRawPolicyIsPresent(HttpListenerMode mode)
            => UseHttp(mode, c =>
            {
                c.Response.SetCookie(new Cookie("future", "one", "/") { Expires = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
                c.Response.Headers.Add("Set-Cookie", "raw=two; SameSite=Lax");
            }, headers =>
            {
                var header = headers.Single(h => h.StartsWith("future=", StringComparison.Ordinal));
                Assert.That(header, Does.Not.Contain("Max-Age=0"));
                var jar = new CookieContainer();
                jar.SetCookies(new Uri("http://localhost/"), header);
                Assert.That(jar.GetCookies(new Uri("http://localhost/"))["future"]!.Expires.Year, Is.EqualTo(2099));
            });

        private static async Task UseHttp(HttpListenerMode mode, Action<IHttpContext> configure, Action<string[]> check)
        {
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).OnGet("/", async c =>
            {
                configure(c);
                await c.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            });
            var running = server.RunAsync(stop.Token);
            try
            {
                using var client = new HttpClient();
                using var response = await client.GetAsync(url).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                check(response.Headers.GetValues("Set-Cookie").ToArray());
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
        }

        private sealed class CookieMiddleware : WebModuleBase
        {
            private readonly Action<IHttpContext> _configure;
            public CookieMiddleware(Action<IHttpContext> configure) : base("/socket") => _configure = configure;
            public override bool IsFinalHandler => false;
            protected override Task OnRequestAsync(IHttpContext context) { _configure(context); return Task.CompletedTask; }
        }

        private sealed class EchoSocket : WebSocketModule
        {
            public EchoSocket() : base("/socket", false) { }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => SendAsync(context, buffer);
        }
    }
}
