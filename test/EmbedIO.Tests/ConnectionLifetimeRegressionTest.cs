using System;
using System.IO;
using System.Collections;
using System.Linq;
using System.Net.Sockets;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Text;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Actions;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ConnectionLifetimeRegressionTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [TestCase(false, "response")]
        [TestCase(true, "response")]
        [TestCase(false, "force")]
        [TestCase(true, "force")]
        [TestCase(false, "stop")]
        [TestCase(true, "stop")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "peer")]
        [TestCase(true, "peer")]
        public async Task TerminalCloseReleasesTransportAndTimerWithRetainedContext(bool secure, string operation)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = secure ? HttpsSmoke.CreateClient((certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : new HttpClient();
            object? connection = null;
            try
            {
                var accept = listener.GetContextAsync(timeout.Token);
                var received = client.GetAsync(url, timeout.Token);
                var context = await accept;
                connection = ((context).GetType().GetProperty("Connection", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(context);
                var stream = (Stream)((((connection ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetType().GetProperty("Stream") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
                var timer = (Timer)((((connection).GetType().GetField("_timer", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 0;
                context.Response.KeepAlive = operation != "response";
                context.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
                switch (operation)
                {
                    case "response": context.Close(); break;
                    case "force": ((connection).GetType().GetMethod("ForceClose", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(connection, null); break;
                    case "stop": listener.Stop(); break;
                    case "dispose": listener.Dispose(); break;
                    case "peer": context.Close(); client.Dispose(); break;
                }
                if (operation != "peer")
                {
                    using var response = await received;
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                }
                else
                {
                    try { using var response = await received; }
                    catch (OperationCanceledException) { }
                    catch (HttpRequestException) { }
                }
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while ((stream).CanRead && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.Multiple(() =>
                {
                    Assert.That(stream.CanRead, Is.False, "Retaining a completed context must not retain an open transport wrapper.");
                    Assert.That(TimerDisposed((timer)), Is.True, "Terminal close must dispose its request timer.");
                    Assert.That(((connection).GetType().GetField("_buffer", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection), Is.Null);
                    Assert.That(context.Request.HttpMethod, Is.EqualTo("GET"));
                    Assert.That(context.Request.RawTarget, Is.EqualTo("/"));
                    if (operation == "force" || operation == "stop" || operation == "dispose")
                        Assert.That(((connection).GetType().GetProperty("Reuses") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection), Is.EqualTo(0));
                    Assert.That(listener.IsListening, Is.EqualTo(operation != "stop" && operation != "dispose"));
                });
            }
            finally
            {
                timeout.Cancel();
                if (connection is IDisposable disposable) disposable.Dispose();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task KeepAliveReusesTheSameLiveTransportAndReleasesItOnStop(bool secure)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            var connections = new ConcurrentDictionary<object, byte>();
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                if (secure) options.WithCertificate(certificate);
            }).WithModule(new ActionModule("/", HttpVerbs.Any, async context =>
            {
                connections.TryAdd(Connection(context), 0);
                await context.Request.InputStream.CopyToAsync(Stream.Null, context.CancellationToken);
                await context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = secure ? HttpsSmoke.CreateClient((certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : new HttpClient();
            try
            {
                for (var index = 0; index < 40; index++)
                {
                    using var body = new ByteArrayContent(new byte[8192]);
                    using var response = await client.PostAsync(url, body);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("ok"));
                }
                Assert.That(connections.Count, Is.EqualTo(1), "Sequential requests should reuse the existing live transport.");
                var connection = System.Linq.Enumerable.Single(connections.Keys);
                Assert.That(Transport(connection).CanRead, Is.True);
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(Transport(connection).CanRead, Is.False);
                Assert.That(TimerDisposed(RequestTimer(connection)), Is.True);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task WebSocketUpgradeKeepsItsTransportUntilCloseOrShutdown(bool secure, bool shutdown)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            var module = new LifetimeSocket();
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                if (secure) options.WithCertificate(certificate);
            }).WithModule(module);
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new ClientWebSocket();
            using var pinned = secure ? HttpsSmoke.CreateClient((certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : null;
            try
            {
                var uri = new Uri(url.Replace("https://", "wss://", StringComparison.Ordinal)
                    .Replace("http://", "ws://", StringComparison.Ordinal) + "echo");
                if (secure) await client.ConnectAsync(uri, pinned, timeout.Token);
                else await client.ConnectAsync(uri, timeout.Token);
                var connection = await module.Connection.Task.WaitAsync(timeout.Token);
                var bytes = Encoding.ASCII.GetBytes("echo");
                for (var index = 0; index < 3; index++)
                {
                    await client.SendAsync(bytes, WebSocketMessageType.Binary, true, timeout.Token);
                    var result = new byte[32];
                    var received = await client.ReceiveAsync(result, timeout.Token);
                    Assert.That(result.AsSpan(0, received.Count).ToArray(), Is.EqualTo(bytes));
                    Assert.That(received.EndOfMessage, Is.True);
                    Assert.That(Transport(connection).CanRead, Is.True);
                }
                if (shutdown)
                {
                    stop.Cancel();
                    await running.WaitAsync(timeout.Token);
                }
                else
                {
                    await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                    Assert.That(server.Listener.IsListening, Is.True);
                }
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (Transport(connection).CanRead && DateTime.UtcNow < deadline) await Task.Delay(10);
                Assert.That(Transport(connection).CanRead, Is.False);
                Assert.That(TimerDisposed(RequestTimer(connection)), Is.True);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ResponseCloseRacingStopAndDisposeDoesNotRestartOrRetainTransport(bool secure)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var url = HttpsSmoke.GetUrl();
                if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
                using var listener = new Net.HttpListener(certificate);
                listener.AddPrefix(url);
                listener.Start();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var client = secure ? HttpsSmoke.CreateClient((certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : new HttpClient();
                var accepted = listener.GetContextAsync(timeout.Token);
                var received = client.GetAsync(url, timeout.Token);
                var context = await accepted;
                var connection = Connection(context);
                context.Response.StatusCode = 204;
                context.Response.ContentLength64 = 0;
                context.Response.OutputStream.Write(Array.Empty<byte>(), 0, 0);
                await Task.WhenAll(Task.Run(context.Close), Task.Run(listener.Stop), Task.Run(listener.Dispose)).WaitAsync(timeout.Token);
                using var response = await received;
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Transport(connection).CanRead, Is.False);
                Assert.That(TimerDisposed(RequestTimer(connection)), Is.True);
            }
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task UnregisteredIdleOrHandshakeConnectionReleasesResources(bool secure, bool shutdown)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            using var listener = new Net.HttpListener(certificate);
            listener.AddPrefix(url);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, new Uri(url).Port);
            var registrations = (IDictionary)((((typeof(Net.EndPointManager)).GetField("Registrations", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            var prefixes = (IDictionary)((registrations)[listener] ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            var endpoint = ((IEnumerable)((prefixes)[url] ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))).Cast<object>().Single();
            var pending = (IEnumerable)((((endpoint).GetType().GetField("_unregistered", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(endpoint)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            object? connection = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (connection == null && DateTime.UtcNow < deadline)
            {
                lock (pending) { connection = pending.Cast<object>().SingleOrDefault(); }
                if (connection == null) await Task.Delay(10);
            }
            Assert.That(connection, Is.Not.Null);
            if (shutdown) listener.Stop();
            else client.Dispose();
            deadline = DateTime.UtcNow.AddSeconds(2);
            while (((connection ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetType().GetField("_buffer", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection) != null
                && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.That(TimerDisposed(RequestTimer(connection)), Is.True);
            Assert.That(Transport(connection).CanRead, Is.False);
            Assert.That(listener.IsListening, Is.EqualTo(!shutdown));
        }
        private static object Connection(IHttpContext context)
            => ((((context).GetType().GetProperty("Connection", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(context)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static Stream Transport(object connection)
            => (Stream)((((connection).GetType().GetProperty("Stream") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static Timer RequestTimer(object connection)
            => (Timer)((((connection).GetType().GetField("_timer", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(connection)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        private sealed class LifetimeSocket() : WebSocketModule("/echo", false)
        {
            internal TaskCompletionSource<object> Connection { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            protected override Task OnClientConnectedAsync(IWebSocketContext context)
            {
                var close = (Action)((((context).WebSocket.GetType().GetField("_closeConnection", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(context.WebSocket)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
                Connection.TrySetResult(((close).Target ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")));
                return Task.CompletedTask;
            }
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => SendAsync(context, buffer);
        }
        private static bool TimerDisposed(Timer timer)
        {
            try { return !timer.Change(Timeout.Infinite, Timeout.Infinite); }
            catch (ObjectDisposedException) { return true; }
        }
    }
}
