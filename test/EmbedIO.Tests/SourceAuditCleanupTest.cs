using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Sessions;
using EmbedIO.Testing;
using EmbedIO.Utilities;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class SourceAuditCleanupTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task RegeneratingSessionRegistersExactlyOneCloseCallback(bool accessBefore)
        {
            var manager = new CountingSessionManager();
            await TestWebServer.UseAsync(
                server => server.WithSessionManager(manager).OnAny(context =>
                {
                    if (accessBefore) context.Session["before"] = 1;
                    context.Session.Regenerate();
                    context.Session.Regenerate();
                    context.Session["after"] = 2;
                    return context.SendStringAsync("ok", "text/plain", Encoding.UTF8);
                }),
                async client => Assert.That(await client.GetStringAsync("/"), Is.EqualTo("ok")));
            Assert.That(manager.Closed, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(3)]
        public void PreferredIndexRetainsNullCandidatePositions(int index)
        {
            var values = new string[5];
            values[index] = "gzip";
            var qualities = new QValueList(false, "gzip");
            Assert.Multiple(() =>
            {
                Assert.That(qualities.FindPreferredIndex(values), Is.EqualTo(index));
                Assert.That(qualities.FindPreferredIndex((IEnumerable<string>)values), Is.EqualTo(index));
                Assert.That(qualities.FindPreferred(values), Is.EqualTo("gzip"));
                Assert.That(qualities.FindPreferredIndex(new string[3]), Is.EqualTo(-1));
            });
        }

        [Test]
        public void MimeProvidersRetainStackOrderAndShortCircuit()
        {
            var stack = new MimeTypeProviderStack();
            var bottom = new Provider("application/bottom");
            var middle = new Provider("application/middle");
            var top = new Provider(null);
            stack.Push(bottom);
            stack.Push(middle);
            stack.Push(top);
            Assert.That(stack.GetMimeType(".unknown"), Is.EqualTo("application/middle"));
            Assert.That(new[] { top.Calls, middle.Calls, bottom.Calls }, Is.EqualTo(new[] { 1, 1, 0 }));
            stack.Pop();
            stack.Pop();
            Assert.That(stack.GetMimeType(".unknown"), Is.EqualTo("application/bottom"));
            stack.Pop();
            Assert.That(stack.GetMimeType(".html"), Is.EqualTo(MimeType.Html));
            Assert.That(stack.GetMimeType(".no-such-mime-audit"), Is.Null);
        }

        [Test]
        public void MimeLookupDoesNotAllocatePerProviderTraversal()
        {
            var stack = new MimeTypeProviderStack();
            stack.Push(new Provider("application/value"));
            stack.Push(new Provider(null));
            for (var i = 0; i < 100; i++) stack.GetMimeType(".audit");
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) stack.GetMimeType(".audit");
            var bytes = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(bytes, Is.Zero);
        }

        private sealed class Provider(string? result) : IMimeTypeProvider
        {
            internal int Calls { get; private set; }
            public string? GetMimeType(string extension) { Calls++; return result; }
            public bool TryDetermineCompression(string mimeType, out bool preferCompression) { preferCompression = false; return false; }
        }

        private sealed class CountingSessionManager : ISessionManager
        {
            private readonly LocalSessionManager _inner = new();
            internal int Closed { get; private set; }
            public void Start(CancellationToken cancellationToken) => _inner.Start(cancellationToken);
            public ISession Create(IHttpContext context) => _inner.Create(context);
            public void Delete(IHttpContext context, string id) => _inner.Delete(context, id);
            public void OnContextClose(IHttpContext context) { Closed++; _inner.OnContextClose(context); }
        }
    }
}
