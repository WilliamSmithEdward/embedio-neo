using System;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http2InteroperabilityTest
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static Type Type(string name) => typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2." + name, true)!;
        private static T Property<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
        private static async Task<object> Result(Task task) { await task; return task.GetType().GetProperty("Result")!.GetValue(task)!; }
        private static Task Respond(object exchange, byte[] bytes)
            => (Task)exchange.GetType().GetMethod("RespondAsync", Flags)!.Invoke(exchange, new object[] { bytes, Property<CancellationToken>(exchange, "CancellationToken") })!;
        private static async Task WithServer(Func<object, Task> application, Func<HttpClient, Task> verify)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                using var stream = socket.GetStream();
                var connection = await Result((Task)Type("Http2Connection").GetMethod("AcceptAsync", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { stream, stop.Token })!);
                using var connectionLifetime = (IDisposable)connection;
                var dispatcher = Activator.CreateInstance(Type("Http2Dispatcher"), Flags, null, new[] { connection }, null)!;
                using var dispatcherLifetime = (IDisposable)dispatcher;
                var argument = Expression.Parameter(Type("Http2Exchange"));
                var delegateType = typeof(Func<,>).MakeGenericType(Type("Http2Exchange"), typeof(Task));
                var callback = Expression.Lambda(delegateType, Expression.Invoke(Expression.Constant(application), Expression.Convert(argument, typeof(object))), argument).Compile();
                await (Task)dispatcher.GetType().GetMethod("RunAsync", Flags)!.Invoke(dispatcher, new object[] { callback, stop.Token })!;
            });
            try
            {
                using var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
                using var client = new HttpClient(handler)
                {
                    BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                    DefaultRequestVersion = HttpVersion.Version20,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    Timeout = TimeSpan.FromSeconds(15),
                };
                try { await verify(client); }
                finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            finally
            {
                stop.Cancel(); listener.Stop();
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(65535)]
        [TestCase(65536)]
        [TestCase(262144)]
        public async Task IndependentDotNetHttp2ClientEchoesAcrossBothFlowWindows(int length)
        {
            var expected = new byte[length]; new Random(41).NextBytes(expected);
            await WithServer(async exchange =>
            {
                using var body = new MemoryStream();
                await Property<Stream>(exchange, "InputStream").CopyToAsync(body, Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, body.ToArray());
            }, async client =>
            {
                using var response = await client.PostAsync("echo", new ByteArrayContent(expected));
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected));
            });
        }

        [Test]
        public async Task ConcurrentRequestsShareOneConnectionWithoutMixingBodies()
        {
            await WithServer(async exchange =>
            {
                using var body = new MemoryStream();
                await Property<Stream>(exchange, "InputStream").CopyToAsync(body, Property<CancellationToken>(exchange, "CancellationToken"));
                await Respond(exchange, body.ToArray());
            }, async client =>
            {
                await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
                {
                    var bytes = new byte[20000 + index * 7000]; new Random(index).NextBytes(bytes);
                    using var response = await client.PostAsync("echo/" + index, new ByteArrayContent(bytes));
                    Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(bytes));
                }));
            });
        }

        [Test]
        public async Task CancelingBlockedRequestLeavesOtherStreamsHealthy()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                var request = Property<object>(exchange, "Request");
                if (Property<string>(request, "Path") == "/blocked")
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, Property<CancellationToken>(exchange, "CancellationToken"));
                }
                else await Respond(exchange, new byte[] { 7, 8, 9 });
            }, async client =>
            {
                using var cancel = new CancellationTokenSource();
                var blocked = client.GetAsync("blocked", cancel.Token);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(await client.GetByteArrayAsync("fast"), Is.EqualTo(new byte[] { 7, 8, 9 }));
                cancel.Cancel();
                await Assert.ThatAsync(async () => await blocked, Throws.InstanceOf<OperationCanceledException>());
                Assert.That(await client.GetByteArrayAsync("after-reset"), Is.EqualTo(new byte[] { 7, 8, 9 }));
            });
        }

        [Test]
        public async Task LargeResponseHeadersUseContinuationAndPreserveCompressionOrder()
        {
            var value = new string(Enumerable.Range(0, 26000).Select(i => (char)('!' + i % 90)).ToArray());
            await WithServer(exchange =>
            {
                var pairs = new[] { ":status", "200", "x-large", value, "content-length", "0" };
                var fields = Array.CreateInstance(Type("HpackField"), 3);
                for (var i = 0; i < pairs.Length; i += 2)
                    fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags, null, new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
                return (Task)exchange.GetType().GetMethod("SendHeadersAsync", Flags)!.Invoke(exchange, new object[] { fields, true, Property<CancellationToken>(exchange, "CancellationToken") })!;
            }, async client =>
            {
                for (var i = 0; i < 3; i++)
                {
                    using var response = await client.GetAsync("headers");
                    Assert.That(response.Headers.GetValues("x-large").Single(), Is.EqualTo(value));
                }
            });
        }
    }
}
