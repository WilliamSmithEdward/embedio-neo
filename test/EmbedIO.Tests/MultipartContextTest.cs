using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.Sessions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [TestFixture]
    public sealed class MultipartContextTest
    {
        [TestCase(null, 400)]
        [TestCase("invalid", 400)]
        [TestCase("application/json", 415)]
        [TestCase("multipart/form-data", 400)]
        [TestCase("multipart/form-data; boundary=a; boundary=b", 400)]
        [TestCase("multipart/form-data; boundary; boundary=a", 400)]
        [TestCase("multipart/form-data; boundary=a; boundary", 400)]
        [TestCase("multipart/form-data; boundary=\"\"", 400)]
        [TestCase("multipart/form-data; boundary=\"a \"", 400)]
        public void RejectsContentTypeBeforeTakingStreamOwnership(string? contentType, int status)
        {
            using var stream = new MemoryStream();
            var context = new UploadContext(contentType, stream);
            var error = Assert.Throws<HttpException>(() => context.OpenMultipartReader());
            Assert.That(error?.StatusCode, Is.EqualTo(status));
            Assert.That(stream.CanRead, Is.True);
            Assert.That(context.Close, Is.Null);
            Assert.That(context.Items, Is.Empty);
        }

        [Test]
        public async Task QuotedBoundaryAndCaseInsensitiveMediaTypeWork()
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes("--a b--\r\n"));
            var context = new UploadContext("Multipart/Form-Data; BOUNDARY=\"a b\"", stream);
            using var reader = context.OpenMultipartReader();
            Assert.That(await reader.ReadNextSectionAsync(), Is.Null);
            Assert.Throws<InvalidOperationException>(() => context.OpenMultipartReader());
        }

        [Test]
        public async Task ContextCloseDisposesReaderAndOwnedStream()
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes("--a--\r\n"));
            var context = new UploadContext("multipart/form-data; boundary=a", stream);
            using var reader = context.OpenMultipartReader();
            (context.Close ?? throw new AssertionException("Cleanup was not registered."))(context);
            Assert.That(stream.CanRead, Is.False);
            await Assert.ThatAsync(async () => await reader.ReadNextSectionAsync(), Throws.TypeOf<ObjectDisposedException>());
        }

        [Test]
        public void FailedCleanupRegistrationDisposesOwnedStream()
        {
            using var stream = new MemoryStream();
            var context = new UploadContext("multipart/form-data; boundary=a", stream) { RejectClose = true };
            Assert.Throws<InvalidOperationException>(() => context.OpenMultipartReader());
            Assert.That(stream.CanRead, Is.False);
            Assert.That(context.Items, Is.Empty);
        }

        private sealed class UploadContext : IHttpContext
        {
            internal UploadContext(string? contentType, Stream stream) => Request = new UploadRequest(contentType, stream);
            internal Action<IHttpContext>? Close { get; private set; }
            internal bool RejectClose { get; init; }
            public IHttpRequest Request { get; }
            public IDictionary<object, object> Items { get; } = new Dictionary<object, object>();
            public CancellationToken CancellationToken => CancellationToken.None;
            public string Id => "multipart-test";
            public bool SupportCompressedRequests => false;
            public IPEndPoint RemoteEndPoint => throw new NotSupportedException();
            public IPEndPoint LocalEndPoint => throw new NotSupportedException();
            public RouteMatch Route => throw new NotSupportedException();
            public string RequestedPath => throw new NotSupportedException();
            public IHttpResponse Response => throw new NotSupportedException();
            public IPrincipal User => throw new NotSupportedException();
            public ISessionProxy Session => throw new NotSupportedException();
            public long Age => throw new NotSupportedException();
            public bool IsHandled => throw new NotSupportedException();
            public void SetHandled() => throw new NotSupportedException();
            public string? GetMimeType(string extension) => throw new NotSupportedException();
            public bool TryDetermineCompression(string mimeType, out bool preferCompression) => throw new NotSupportedException();
            public void OnClose(Action<IHttpContext> callback)
            {
                if (RejectClose) throw new InvalidOperationException("Deliberate registration failure.");
                Close = callback;
            }
        }

        private sealed class UploadRequest : IHttpRequest
        {
            internal UploadRequest(string? contentType, Stream stream) { ContentType = contentType; InputStream = stream; }
            public string? ContentType { get; }
            public Stream InputStream { get; }
            public NameValueCollection Headers { get; } = new();
            public string HttpMethod => "POST";
            public ICookieCollection Cookies => throw new NotSupportedException();
            public Version ProtocolVersion => HttpVersion.Version11;
            public bool KeepAlive => true;
            public string RawTarget => "/";
            public NameValueCollection QueryString => throw new NotSupportedException();
            public HttpVerbs HttpVerb => HttpVerbs.Post;
            public Uri Url => new("http://localhost/");
            public bool HasEntityBody => true;
            public Encoding ContentEncoding => Encoding.UTF8;
            public IPEndPoint RemoteEndPoint => new(IPAddress.Loopback, 1);
            public bool IsLocal => true;
            public bool IsSecureConnection => false;
            public string? UserAgent => null;
            public bool IsWebSocketRequest => false;
            public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, 2);
            public long ContentLength64 => 0;
            public bool IsAuthenticated => false;
            public Uri? UrlReferrer => null;
        }
    }
}
