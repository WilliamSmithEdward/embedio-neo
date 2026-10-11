using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1ContextLifetimeTest
    {
        [Test]
        public async Task RepeatedCloseCompletesCallbacksOnceInTheirExistingOrder()
        {
            using var fixture = await Fixture.Open();
            var calls = new ConcurrentQueue<int>();
            fixture.Context.OnClose(_ => calls.Enqueue(1));
            fixture.Context.OnClose(_ => calls.Enqueue(2));
            fixture.Context.Close();
            fixture.Context.Close();
            Assert.That(calls, Is.EqualTo(new[] { 2, 1 }));
            Assert.Throws<InvalidOperationException>(() => fixture.Context.OnClose(_ => calls.Enqueue(3)));
        }

        [Test]
        public async Task ConcurrentCloseCannotDispatchCallbacksAheadOfTheFirstCompletion()
        {
            using var fixture = await Fixture.Open();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            fixture.Context.OnClose(_ =>
            {
                if (Interlocked.Increment(ref count) == 1)
                {
                    entered.TrySetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(5))) throw new AssertionException("Completion callback was not released.");
                }
            });
            var first = Task.Run(fixture.Context.Close);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Run(fixture.Context.Close).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(count, Is.EqualTo(1), "A second close must not invoke a callback while the first completion is pending.");
            }
            finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
            Assert.That(count, Is.EqualTo(1));
        }

        private sealed class Fixture : IDisposable
        {
            private readonly Net.HttpListener _listener;
            private readonly TcpClient _client;
            internal IHttpContextImpl Context { get; }
            private Fixture(Net.HttpListener listener, TcpClient client, IHttpContextImpl context)
            { _listener = listener; _client = client; Context = context; }
            internal static async Task<Fixture> Open()
            {
                var address = new Uri(Resources.GetServerAddress());
                var listener = new Net.HttpListener();
                var client = new TcpClient();
                try
                {
                    listener.AddPrefix(address.AbsoluteUri);
                    listener.Start();
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.ConnectAsync(address.Host, address.Port, deadline.Token);
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + address.Authority + "\r\nConnection: close\r\n\r\n"), deadline.Token);
                    var context = await listener.GetContextAsync(deadline.Token);
                    return new Fixture(listener, client, context);
                }
                catch { client.Dispose(); listener.Dispose(); throw; }
            }
            public void Dispose() { _client.Dispose(); _listener.Dispose(); }
        }
    }
}
