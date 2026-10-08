using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class SharedHttp1OwnershipTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task StaleOwnerSnapshotDoesNotStopAReusedSiblingConnection(bool drain)
        {
            var prefix = Resources.GetServerAddress();
            using var owner = new Net.HttpListener();
            using var sibling = new Net.HttpListener();
            owner.AddPrefix(prefix); sibling.AddPrefix(prefix + "sibling/");
            owner.Start(); sibling.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 });
            var firstRequest = client.GetStringAsync(prefix, deadline.Token);
            var first = await owner.GetContextAsync(deadline.Token);
            var connections = (IDictionary)(typeof(Net.HttpListener).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new AssertionException("Missing connection ownership table."));
            var captured = connections.Keys.Cast<object>().Single();
            await first.SendStringAsync("owner", "text/plain", WebServer.Utf8NoBomEncoding);
            first.Close();
            Assert.That(await firstRequest, Is.EqualTo("owner"));
            var request = client.GetStringAsync(prefix + "sibling/", deadline.Token);
            var second = await sibling.GetContextAsync(deadline.Token);
            Assert.That(second.RemoteEndPoint, Is.EqualTo(first.RemoteEndPoint));
            // The old listener captured this connection before its ownership moved.
            // Deterministically resume that snapshot after the sibling's admission.
            var method = captured.GetType().GetMethod(drain ? "DrainForListenerAsync" : "CloseForListener", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new AssertionException("Missing scoped lifetime operation.");
            var completion = method.Invoke(captured, new object[] { owner }) as Task;
            if (completion != null) Assert.That(completion.IsCompleted, Is.True, "The stale owner has nothing left to drain.");
            await second.SendStringAsync("sibling", "text/plain", WebServer.Utf8NoBomEncoding);
            second.Close();
            Assert.That(await request, Is.EqualTo("sibling"));
            var followup = client.GetStringAsync(prefix + "sibling/after", deadline.Token);
            var next = await sibling.GetContextAsync(deadline.Token);
            Assert.That(next.RemoteEndPoint, Is.EqualTo(first.RemoteEndPoint));
            await next.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
            next.Close();
            Assert.That(await followup, Is.EqualTo("healthy"));
        }
    }
}
