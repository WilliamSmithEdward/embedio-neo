using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ManagedConnectionDrainTest
    {
        private static object Connection(Net.HttpListener listener)
            => ((IDictionary)(typeof(Net.HttpListener).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener)
                ?? throw new AssertionException("Missing connection ownership table."))).Keys.Cast<object>().Single();
        private static Task Drain(object connection)
            => (Task)(connection.GetType().GetMethod("DrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(connection, null)
                ?? throw new AssertionException("Missing connection drain operation."));

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public async Task Http1DrainPreservesResponseAndDoesNotDispatchPipelinedSuccessor(bool pipelined, bool headersSent)
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix);
            listener.Start();
            using var client = new TcpClient();
            var uri = new Uri(prefix);
            await client.ConnectAsync(IPAddress.Loopback, uri.Port);
            using var wire = client.GetStream();
            var request = $"GET / HTTP/1.1\r\nHost: {uri.Authority}\r\n\r\n";
            await wire.WriteAsync(Encoding.ASCII.GetBytes(request + (pipelined ? request : "")));
            var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var connection = Connection(listener);
            context.Response.ContentLength64 = 6;
            if (headersSent) await context.Response.OutputStream.WriteAsync(new byte[] { 97, 98, 99 });
            var drain = Drain(connection);
            Assert.That(drain.IsCompleted, Is.False);
            Assert.That(Drain(connection), Is.SameAs(drain));
            await context.Response.OutputStream.WriteAsync(headersSent ? new byte[] { 100, 101, 102 } : Encoding.ASCII.GetBytes("abcdef"));
            context.Close();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            using var reader = new StreamReader(wire, Encoding.ASCII);
            var response = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(response, Does.EndWith("\r\n\r\nabcdef"));
            Assert.That(response.Split("HTTP/1.1", StringSplitOptions.None), Has.Length.EqualTo(2));
            if (!headersSent) Assert.That(response, Does.Contain("Connection: close\r\n"));
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThatAsync(async () => await listener.GetContextAsync(stop.Token), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(listener.IsListening, Is.True, "Connection drain must not stop the listener.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Http2DrainWaitsForAcceptedResponseOrExplicitAbort(bool abort)
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix);
            listener.Start();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                DefaultRequestVersion = HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(10)
            };
            var request = client.GetAsync(prefix);
            var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var connection = Connection(listener);
            var drain = Drain(connection);
            Assert.That(drain.IsCompleted, Is.False);
            Assert.That(Drain(connection), Is.SameAs(drain));
            if (abort)
            {
                listener.Stop();
                await Assert.ThatAsync(async () => { using var response = await request; }, Throws.InstanceOf<HttpRequestException>());
            }
            else
            {
                context.Response.ContentLength64 = 3;
                await context.Response.OutputStream.WriteAsync(new byte[] { 97, 98, 99 });
                context.Close();
                using var response = await request;
                Assert.That(response.Version, Is.EqualTo(HttpVersion.Version20));
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("abc"));
            }
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(Drain(connection).IsCompletedSuccessfully, Is.True);
        }

        [Test]
        public async Task Http1IdleKeepAliveDrainClosesWithoutAnotherRequest()
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix);
            listener.Start();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var request = client.GetAsync(prefix);
            var context = await listener.GetContextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var connection = Connection(listener);
            context.Response.ContentLength64 = 0;
            await context.Response.OutputStream.FlushAsync();
            context.Close();
            using var response = await request;
            await Drain(connection).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }
    }
}
