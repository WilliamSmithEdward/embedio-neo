using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using EmbedIO.Files;
using EmbedIO.Testing;
using EmbedIO.Utilities;
using EmbedIO.WebSockets;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class SourceAuditBoundaryTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FileProviderRejectsSiblingWithMatchingNamePrefix(bool trailingSeparator)
        {
            var temporary = Path.Combine(Path.GetTempPath(), "embedio-boundary-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(temporary, "root");
            var sibling = Path.Combine(temporary, "root-sibling");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(sibling);
            try
            {
                File.WriteAllText(Path.Combine(root, "inside.txt"), "inside");
                File.WriteAllText(Path.Combine(sibling, "secret.txt"), "outside");
                using var provider = new FileSystemProvider(root + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : string.Empty), true);
                var mime = new MimeTypeProviderStack();
                Assert.Multiple(() =>
                {
                    Assert.That(provider.MapUrlPath("/inside.txt", mime), Is.Not.Null);
                    Assert.That(provider.MapUrlPath("/", mime), Is.Not.Null);
                    Assert.That(provider.MapUrlPath("/%2e%2e/root-sibling/secret.txt", mime), Is.Null);
                    Assert.That(provider.MapUrlPath("/%2e%2e/root-sibling", mime), Is.Null);
                });
            }
            finally { Directory.Delete(temporary, true); }
        }

        [Test]
        public void MockSubdirectoryMetadataMapsBackToItsOwnEntries()
        {
            var provider = new MockFileProvider();
            var mime = new MimeTypeProviderStack();
            var directory = provider.MapUrlPath(MockFileProvider.SubDirectoryUrlPath, mime)
                ?? throw new InvalidOperationException("Missing stock directory.");
            Assert.That(directory.Path, Is.EqualTo(MockFileProvider.SubDirectoryName));
            var entries = provider.GetDirectoryEntries(directory.Path, mime).ToArray();
            Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(new[] { "sub/index.html" }));
        }

        [TestCase("https://[::1]:8443/", 8443)]
        [TestCase("https://[2001:db8::1]:9443/", 9443)]
        [TestCase("https://[::1]/", 443)]
        [TestCase("https://localhost/path:123/", 443)]
        [TestCase("https://*:8443/", 8443)]
        [TestCase("https://+:9443/", 9443)]
        [TestCase("https://localhost/", 443)]
        public void SslPortComesOnlyFromAuthority(string prefix, int expected)
        {
            var options = new WebServerOptions().WithUrlPrefix(prefix);
            var method = typeof(WebServerOptions).GetMethod("GetSslPort", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing SSL port helper.");
            Assert.That(method.Invoke(options, null), Is.EqualTo(expected));
        }

        [Test]
        public void DuplicateProtocolBatchIsRejectedWithoutPartialRegistration()
        {
            using var module = new ProtocolModule();
            Assert.Throws<ArgumentException>(() => module.Register("chat", "chat"));
            Assert.DoesNotThrow(() => module.Register("chat", "other"));
            Assert.Throws<ArgumentException>(() => module.Register("third", "chat"));
            Assert.DoesNotThrow(() => module.Register("third"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BufferDisposalIsOnceOnlyAndReleasesBufferWhenCopyFails(bool failWrite)
        {
            using var output = new ObservedOutput(failWrite);
            var response = new Response(output);
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Internal.BufferingResponseStream")
                ?? throw new InvalidOperationException("Missing buffering stream.");
            var stream = Activator.CreateInstance(type, response) as Stream
                ?? throw new InvalidOperationException("Cannot create buffering stream.");
            stream.WriteByte(42);
            if (failWrite) Assert.Throws<IOException>(() => stream.Dispose());
            else Assert.DoesNotThrow(() => stream.Dispose());
            Assert.DoesNotThrow(() => stream.Dispose());
            Assert.That(output.Writes, Is.EqualTo(1));
            Assert.That(output.CanWrite, Is.True, "The response owns its output stream.");
            Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(43));
            Assert.That(response.ContentLength64, Is.EqualTo(1));
        }

        private sealed class ProtocolModule : WebSocketModule
        {
            internal ProtocolModule() : base("/", false) { }
            internal void Register(params string[] protocols) => AddProtocols(protocols);
            protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
                => Task.CompletedTask;
        }

        private sealed class ObservedOutput : MemoryStream
        {
            private readonly bool _failWrite;
            internal ObservedOutput(bool failWrite) => _failWrite = failWrite;
            internal int Writes { get; private set; }
            public override void Write(byte[] buffer, int offset, int count)
            {
                Writes++;
                if (_failWrite) throw new IOException("Controlled output failure.");
                base.Write(buffer, offset, count);
            }
        }

        private sealed class Response : IHttpResponse
        {
            internal Response(Stream output) => OutputStream = output;
            public WebHeaderCollection Headers { get; } = new WebHeaderCollection();
            public int StatusCode { get; set; } = 200;
            public long ContentLength64 { get; set; }
            public string? ContentType { get; set; }
            public Stream OutputStream { get; }
            public Encoding? ContentEncoding { get; set; }
            public bool KeepAlive { get; set; }
            public bool SendChunked { get; set; }
            public string? StatusDescription { get; set; }
            public ICookieCollection Cookies => throw new NotSupportedException("Cookies are not used by this stream fixture.");
            public Version ProtocolVersion => HttpVersion.Version11;
            public void SetCookie(Cookie cookie) => Cookies.Add(cookie);
            public void Close() => OutputStream.Dispose();
        }
    }
}
