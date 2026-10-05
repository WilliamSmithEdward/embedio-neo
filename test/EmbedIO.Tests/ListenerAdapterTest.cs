using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Sessions;
using EmbedIO.Tests.TestObjects;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerAdapterTest
    {
        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task PostBodyQueryAndResponseHeadersSurviveAdapter(HttpListenerMode mode)
            => UseServerAsync(mode,
                server => server.OnAny(async context =>
                {
                    Assert.That(context.Request.HttpVerb, Is.EqualTo(HttpVerbs.Post));
                    Assert.That(context.Request.QueryString["name"], Is.EqualTo("hello world"));
                    Assert.That(context.Request.Headers["X-Test"], Is.EqualTo("request-value"));
                    Assert.That(context.Request.RemoteEndPoint, Is.Not.Null);
                    context.Response.StatusCode = 201;
                    context.Response.Headers.Set("X-Reply", "response-value");
                    await context.SendStringAsync(await context.GetRequestBodyAsStringAsync(), "text/plain", Encoding.UTF8);
                }),
                async url =>
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    using var request = new HttpRequestMessage(HttpMethod.Post, url + "?name=hello+world")
                    {
                        Content = new StringContent("Zürich €", Encoding.UTF8, "text/plain")
                    };
                    request.Headers.Add("X-Test", "request-value");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Created));
                    Assert.That(response.Headers.GetValues("X-Reply"), Is.EqualTo(new[] { "response-value" }));
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Zürich €"));
                });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task SessionCookieRetainsDataAcrossRequests(HttpListenerMode mode)
            => UseServerAsync(mode,
                server => server.WithSessionManager(new LocalSessionManager()).OnAny(context =>
                {
                    var count = context.Session.GetValue<int>("count") + 1;
                    context.Session["count"] = count;
                    return context.SendStringAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture), "text/plain", Encoding.UTF8);
                }),
                async url =>
                {
                    using var handler = new HttpClientHandler();
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("1"));
                    var cookies = handler.CookieContainer.GetCookies(new Uri(url));
                    Assert.That(cookies[LocalSessionManager.DefaultCookieName], Is.Not.Null);
                    if (mode == HttpListenerMode.EmbedIO)
                        Assert.That(cookies[LocalSessionManager.DefaultCookieName]!.HttpOnly, Is.True);
                    Assert.That(await client.GetStringAsync(url), Is.EqualTo("2"));
                });

        [TestCase(HttpListenerMode.EmbedIO, WebSocketMessageType.Text)]
        [TestCase(HttpListenerMode.EmbedIO, WebSocketMessageType.Binary)]
        [TestCase(HttpListenerMode.Microsoft, WebSocketMessageType.Text)]
        [TestCase(HttpListenerMode.Microsoft, WebSocketMessageType.Binary)]
        public Task FragmentedWebSocketMessagesAreReassembledAndMessageBoundariesPreserved(HttpListenerMode mode, WebSocketMessageType type)
            => UseServerAsync(mode,
                server => server.WithModule(new EchoSocket()),
                async url =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "echo"), timeout.Token);
                    var payload = type == WebSocketMessageType.Text
                        ? Encoding.UTF8.GetBytes("héllo € fragmented")
                        : new byte[] { 0, 255, 128, 1, 2, 3, 4 };
                    // Split inside the UTF-8 encoding of é to exercise byte-wise reassembly.
                    await socket.SendAsync(new ArraySegment<byte>(payload, 0, 2), type, false, timeout.Token);
                    await socket.SendAsync(new ArraySegment<byte>(payload, 2, payload.Length - 2), type, true, timeout.Token);
                    await AssertEchoAsync(socket, type, payload, timeout.Token);
                    var next = type == WebSocketMessageType.Text ? Encoding.UTF8.GetBytes("next") : new byte[] { 7, 8 };
                    await socket.SendAsync(new ArraySegment<byte>(next), type, true, timeout.Token);
                    await AssertEchoAsync(socket, type, next, timeout.Token);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                    Assert.That(socket.CloseStatus, Is.EqualTo(WebSocketCloseStatus.NormalClosure));
                });

        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public Task WebSocketNegotiatesSupportedSubprotocol(HttpListenerMode mode)
            => UseServerAsync(mode,
                server => server.WithModule(new EchoSocket("echo.v1")),
                async url =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var socket = new ClientWebSocket();
                    socket.Options.AddSubProtocol("other.v1");
                    socket.Options.AddSubProtocol("echo.v1");
                    await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "echo"), timeout.Token);
                    Assert.That(socket.SubProtocol, Is.EqualTo("echo.v1"));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                });

        [TestCase(HttpListenerMode.EmbedIO, true, "other.v1")]
        [TestCase(HttpListenerMode.Microsoft, true, "other.v1")]
        [TestCase(HttpListenerMode.EmbedIO, true, null)]
        [TestCase(HttpListenerMode.Microsoft, true, null)]
        [TestCase(HttpListenerMode.EmbedIO, false, "echo.v1")]
        [TestCase(HttpListenerMode.Microsoft, false, "echo.v1")]
        public Task WebSocketRejectsOffersWithoutAnAcceptedProtocol(HttpListenerMode mode, bool configured, string? offered)
            => UseServerAsync(mode,
                server => server.WithModule(new EchoSocket(configured ? "echo.v1" : null)),
                async url =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var socket = new ClientWebSocket();
                    socket.Options.CollectHttpResponseDetails = true;
                    if (offered != null)
                        socket.Options.AddSubProtocol(offered);
                    await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(async () =>
                        await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "echo"), timeout.Token));
                    Assert.That(socket.HttpStatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
                });
        [TestCase(HttpListenerMode.EmbedIO)]
        [TestCase(HttpListenerMode.Microsoft)]
        public async Task WebSocketCompletionRunsCallbacksAndLeavesHttpListenerAvailable(HttpListenerMode mode)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await UseServerAsync(mode,
                server => server.WithModule(new CompletionObserver(completed))
                    .WithModule(new EchoSocket())
                    .OnGet("/health", context => context.SendStringAsync("healthy", "text/plain", Encoding.UTF8)),
                async url =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(url.Replace("http://", "ws://") + "echo"), timeout.Token);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                    await completed.Task.WaitAsync(timeout.Token);
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    Assert.That(await client.GetStringAsync(url + "health"), Is.EqualTo("healthy"));
                });
        }

        private sealed class CompletionObserver : WebModuleBase
        {
            private readonly TaskCompletionSource _completed;

            public CompletionObserver(TaskCompletionSource completed) : base("/echo") => _completed = completed;

            public override bool IsFinalHandler => false;

            protected override Task OnRequestAsync(IHttpContext context)
            {
                context.OnClose(_ => _completed.TrySetResult());
                return Task.CompletedTask;
            }
        }
        private static async Task AssertEchoAsync(ClientWebSocket socket, WebSocketMessageType type, byte[] expected, CancellationToken token)
        {
            using var received = new MemoryStream();
            var buffer = new byte[3];
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                Assert.That(result.MessageType, Is.EqualTo(type));
                received.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Assert.That(received.ToArray(), Is.EqualTo(expected));
        }

        private static async Task UseServerAsync(HttpListenerMode mode, Action<WebServer> configure, Func<string, Task> exercise)
        {
            var url = Resources.GetServerAddress();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode));
            configure(server);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            try
            {
                Assert.That(server.State, Is.EqualTo(WebServerState.Listening));
                await exercise(url);
            }
            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class EchoSocket : WebSocketModule
        {
            public EchoSocket(string? protocol = null) : base("/echo", false)
            {
                if (protocol != null)
                    AddProtocol(protocol);
            }

            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => result.MessageType == (int)WebSocketMessageType.Text
                    ? SendAsync(context, Encoding.GetString(buffer))
                    : SendAsync(context, buffer);
        }
    }
}
