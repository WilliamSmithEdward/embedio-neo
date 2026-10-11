using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.PlatformTests;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class TcpRegistrationLifetimeTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task PendingSessionCleanupDoesNotBlockAnUnrelatedListenerStart(bool secure)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var address = new UriBuilder(HttpsSmoke.GetUrl()) { Scheme = secure ? "https" : "http" }.Uri;
            using var first = new Net.HttpListener(certificate);
            first.AddPrefix(address.ToString());
            first.Start();
            using var peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, address.Port);
            var endpoint = TcpRegistrationInventory.Owned(first).Single();
            var pending = endpoint.GetType().GetMethod("PendingConnections", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing endpoint pending inventory.");
            object? session = null;
            var until = DateTime.UtcNow.AddSeconds(2);
            while (session == null && DateTime.UtcNow < until)
            {
                session = (pending.Invoke(endpoint, null) as IEnumerable ?? throw new AssertionException("Missing pending snapshot."))
                    .Cast<object>().SingleOrDefault();
                if (session == null) await Task.Delay(10);
            }
            Assert.That(session, Is.Not.Null);
            var gate = (session ?? throw new AssertionException("No accepted session.")).GetType()
                .GetField("_connectionSync", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(session)
                ?? throw new AssertionException("Missing session gate.");
            var listeningSocket = endpoint.GetType().GetProperty("ListeningSocket", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(endpoint) as Socket ?? throw new AssertionException("Missing listening socket.");
            using var release = new ManualResetEventSlim();
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Factory.StartNew(() =>
            {
                lock (gate)
                {
                    held.SetResult();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Session-gate test did not release.");
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Task? stopping = null;
            Task? starting = null;
            var secondAddress = new UriBuilder(HttpsSmoke.GetUrl()) { Scheme = "http" }.Uri;
            using var second = new Net.HttpListener();
            second.AddPrefix(secondAddress.ToString());
            try
            {
                stopping = Task.Run(first.Stop);
                until = DateTime.UtcNow.AddSeconds(2);
                while (!listeningSocket.SafeHandle.IsClosed && DateTime.UtcNow < until) await Task.Delay(10);
                Assert.That(listeningSocket.SafeHandle.IsClosed, Is.True, "Stop must have sealed the first endpoint before the competing start.");
                Assert.That(stopping.IsCompleted, Is.False, "The controlled session cleanup must still be pending.");
                starting = Task.Run(second.Start);
                await starting.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.That(second.IsListening, Is.True);
                Assert.That(stopping.IsCompleted, Is.False, "Unrelated startup must finish before blocked cleanup is released.");
            }
            finally
            {
                release.Set();
                await holder.WaitAsync(TimeSpan.FromSeconds(5));
                if (stopping != null) await stopping.WaitAsync(TimeSpan.FromSeconds(5));
                if (starting != null) await starting.WaitAsync(TimeSpan.FromSeconds(5));
            }
            using var client = new HttpClient();
            var accepting = second.GetContextAsync(default);
            var receiving = client.GetAsync(secondAddress);
            var context = await accepting.WaitAsync(TimeSpan.FromSeconds(5));
            context.Response.StatusCode = 204;
            context.Close();
            using var result = await receiving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(result.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        }
    }
}
