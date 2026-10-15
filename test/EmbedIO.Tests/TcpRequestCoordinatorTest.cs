using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TcpRequestCoordinatorTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentStreamLookupPublishesOneOwner(bool input)
        {
            using var listener = new Net.HttpListener();
            var address = new Uri(Resources.GetServerAddress());
            listener.AddPrefix(address.AbsoluteUri);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (var round = 0; round < 8; round++)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address.Host, address.Port, deadline.Token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
                    "POST / HTTP/1.1\r\nHost: " + address.Authority + "\r\nContent-Length: 1\r\nConnection: close\r\n\r\nx"), deadline.Token);
                var context = await listener.GetContextAsync(deadline.Token);
                using var ready = new CountdownEvent(16);
                using var start = new ManualResetEventSlim();
                var streams = new ConcurrentQueue<Stream>();
                var workers = Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
                {
                    ready.Signal();
                    if (!start.Wait(TimeSpan.FromSeconds(5))) throw new AssertionException("Stream lookup was not released.");
                    streams.Enqueue(input ? context.Request.InputStream : context.Response.OutputStream);
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
                try
                {
                    Assert.That(ready.Wait(TimeSpan.FromSeconds(5)), Is.True);
                    start.Set();
                    await Task.WhenAll(workers).WaitAsync(deadline.Token);
                    var published = streams.ToArray();
                    Assert.That(published, Has.Length.EqualTo(16));
                    Assert.That(published.All(stream => ReferenceEquals(stream, published[0])), Is.True,
                        "Concurrent property reads must share one request-local transport owner.");
                }
                finally
                {
                    start.Set();
                    await Task.WhenAll(workers).WaitAsync(deadline.Token);
                    client.Dispose();
                    context.Close();
                    foreach (var stream in streams.Distinct()) stream.Dispose();
                }
            }
        }
    }
}
