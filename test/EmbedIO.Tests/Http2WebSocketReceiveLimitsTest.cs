using System;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2WebSocketReceiveLimitsTest
    {
        [TestCase("count")]
        [TestCase("bytes")]
        [TestCase("four-null")]
        public async Task ReceiveLimitsInsideExtendedConnectPreserveCallbacksAndTheSharedHttp2Connection(string policy)
        {
            var url = HttpsSmoke.GetUrl().Replace("https:", "http:", StringComparison.Ordinal);
            var module = new WebSocketReceiveTunnelProbe(policy);
            var siblingPort = 0;
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
                .WithModule(module).WithModule(new ActionModule("/", HttpVerbs.Get, context =>
                {
                    siblingPort = context.RemoteEndPoint.Port;
                    return context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = server.RunAsync(stop.Token);
            using var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
            using var invoker = new HttpMessageInvoker(handler, false);
            using var client = new HttpClient(handler, false)
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var socket = new ClientWebSocket();
            socket.Options.HttpVersion = HttpVersion.Version20;
            socket.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            try
            {
                await socket.ConnectAsync(new Uri(url.Replace("http:", "ws:", StringComparison.Ordinal) + "ws"), invoker, stop.Token);
                Assert.That(socket.State, Is.EqualTo(WebSocketState.Open));
                await module.ExerciseAsync(socket, async () =>
                {
                    Assert.That(await client.GetStringAsync(url + "healthy", stop.Token), Is.EqualTo("healthy"));
                    Assert.That(siblingPort, Is.EqualTo(module.RemotePort), "A new TCP connection is not a healthy sibling proof.");
                }, () => Task.CompletedTask, stop.Token);
            }
            finally
            {
                module.ReleaseAll();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
