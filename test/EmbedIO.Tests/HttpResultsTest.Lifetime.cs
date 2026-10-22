using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using EmbedIO.Problems;
using EmbedIO.Testing;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class HttpResultsTest
    {
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task ResultsWorkOverRealProtocolsWithConditionalDownloads(int protocol) =>
            await WireResult(
                context => context.Request.Url.AbsolutePath switch
                {
                    "/file" => HttpResults
                        .Bytes(Encoding.UTF8.GetBytes("abcdef"), "text/plain", "book.txt", "\"v1\"")
                        .ExecuteAsync(context),
                    "/none" => HttpResults
                        .NoContent()
                        .ExecuteAsync(context),
                    "/problem" => HttpResults
                        .Problem(new ProblemDetails(409) { Detail = "safe" })
                        .ExecuteAsync(context),
                    _ => HttpResults
                        .Text("héllo")
                        .ExecuteAsync(context)
                },
                async client =>
                {
                    using var text = await client.GetAsync("/");
                    Assert.That(await text.Content.ReadAsStringAsync(), Is.EqualTo("héllo"));
                    using var none = await client.GetAsync("/none");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(none.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                        Assert.That(await none.Content.ReadAsByteArrayAsync(), Is.Empty);
                    }

                    using var range = new HttpRequestMessage(HttpMethod.Get, "/file")
                    {
                        Version = client.DefaultRequestVersion,
                        VersionPolicy = client.DefaultVersionPolicy
                    };
                    range.Headers.TryAddWithoutValidation("Range", "bytes=1-3");
                    using var partial = await client.SendAsync(range);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(partial.StatusCode, Is.EqualTo(HttpStatusCode.PartialContent));
                        Assert.That(await partial.Content.ReadAsStringAsync(), Is.EqualTo("bcd"));
                    }

                    using var head = new HttpRequestMessage(HttpMethod.Head, "/file")
                    {
                        Version = client.DefaultRequestVersion,
                        VersionPolicy = client.DefaultVersionPolicy
                    };
                    using var metadata = await client.SendAsync(head);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(metadata.Content.Headers.ContentLength, Is.EqualTo(6));
                        Assert.That(await metadata.Content.ReadAsByteArrayAsync(), Is.Empty);
                    }

                    using var conditional = new HttpRequestMessage(HttpMethod.Get, "/file")
                    {
                        Version = client.DefaultRequestVersion,
                        VersionPolicy = client.DefaultVersionPolicy
                    };
                    conditional.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
                    using var unchanged = await client.SendAsync(conditional);

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(unchanged.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
                        Assert.That(await unchanged.Content.ReadAsByteArrayAsync(), Is.Empty);
                    }

                    using var problem = await client.GetAsync("/problem");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(problem.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
                        Assert.That(await problem.Content.ReadAsStringAsync(), Does.Contain("requestId"));
                    }
                },
                protocol
            );

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task ConcurrentStreamExecutionDoesNotCloseTheActiveOwner(int alternatives)
        {
            var source = new BlockingResultStream();
            using var owned = HttpResults.Stream(source);
            var result = SelectStreamResult(owned, alternatives);
            await TestWebServer.UseAsync(
                s => s.WithModule(new ActionModule(c => result.ExecuteAsync(c))),
                async client =>
                {
                    var pending = client.GetAsync("/");
                    await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    using var second = await client.GetAsync("/");

                    using (Assert.EnterMultipleScope())
                    {
                        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                        Assert.That(source.Closes, Is.Zero);
                    }

                    source.Continue.TrySetResult();
                    using var first = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("stream"));
                }
            );
            Assert.That(source.Closes, Is.EqualTo(1));
        }

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task CancellationBeforeExecutionDisposesTheTransferredSource(int alternatives)
        {
            using var stop = new CancellationTokenSource();
            stop.Cancel();
            var context = DispatchProxy.Create<IHttpContext, ResultContextProxy>();
            ((ResultContextProxy)(object)context).Token = stop.Token;
            var source = new CountedResultStream([1]);
            using var owned = HttpResults.Stream(source);
            var result = SelectStreamResult(owned, alternatives);
            await Assert.ThatAsync(() => result.ExecuteAsync(context), Throws.InstanceOf<OperationCanceledException>());
            Assert.That(source.Closes, Is.EqualTo(1));
        }

        private static HttpResult SelectStreamResult(StreamHttpResult stream, int alternatives) => alternatives switch
        {
            2 => (HttpResult<StreamHttpResult, NoContentResult>)stream,
            3 => (HttpResult<StreamHttpResult, NoContentResult, ProblemHttpResult>)stream,
            _ => stream
        };

        private static readonly string[] ExpectedFailureMessages = ["read failure", "cleanup failure"];

        [TestCase(0)]
        [TestCase(2)]
        [TestCase(3)]
        public async Task BodyAndCleanupFailuresAreBothRetained(int alternatives)
        {
            var request = DispatchProxy.Create<IHttpRequest, RequestPreconditionsTest.RequestProxy>();
            ((RequestPreconditionsTest.RequestProxy)(object)request).Method = "GET";
            var response = DispatchProxy.Create<IHttpResponse, RepresentationStreamingTest.ResponseProxy>();
            using var responseState = (RepresentationStreamingTest.ResponseProxy)(object)response;
            var context = DispatchProxy.Create<IHttpContext, ResultContextProxy>();
            var state = (ResultContextProxy)(object)context;
            state.Request = request;
            state.Response = response;
            var source = new BlockingResultStream { Fail = true };
            using var owned = HttpResults.Stream(source);
            var result = SelectStreamResult(owned, alternatives);
            AggregateException? failure = null;

            try
            {
                await result.ExecuteAsync(context);
            }

            catch (AggregateException error)
            {
                failure = error;
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(
                    failure?.InnerExceptions.Select(error => error.Message),
                    Is.EquivalentTo(ExpectedFailureMessages)
                );
                Assert.That(source.Closes, Is.EqualTo(1));
            }
        }

        [Test]
        public async Task RuntimeStreamSelectionCannotBeRecordedForIdempotency()
        {
            var coordinator = new EmbedIO.Idempotency.IdempotencyCoordinator();
            var policy = new EmbedIO.Idempotency.IdempotencyPolicy("stream", _ => "trusted", TimeSpan.FromMinutes(1));
            var source = new CountedResultStream([1, 2]);
            using var result = HttpResults.Stream(source);
            await TestWebServer.UseAsync(
                s => s.WithModule(
                    new ActionModule(c => coordinator.ExecuteAsync(c, policy, x => result.ExecuteAsync(x)))
                ),
                async client =>
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/");
                    request.Headers.Add("Idempotency-Key", "key");
                    using var response = await client.SendAsync(request);
                    Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
                }
            );
            Assert.That(source.Closes, Is.EqualTo(1));
        }

        public class ResultContextProxy : DispatchProxy
        {
            internal CancellationToken Token;
            internal IHttpRequest? Request;
            internal IHttpResponse? Response;
            private readonly Dictionary<object, object> _items = [];

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
            {
                "get_CancellationToken" => Token,
                "get_Response" => Response,
                "get_Request" => Request,
                "get_Items" => _items,
                _ => throw new InvalidOperationException("Unexpected context access.")
            };
        }

        private static async Task WireResult(
            Func<IHttpContext, Task> handler,
            Func<HttpClient, Task> use,
            int protocol = 1
        )
        {
            if (protocol == 3 && (!QuicConnection.IsSupported))
            {
                if (Environment.GetEnvironmentVariable("EMBEDIO_REQUIRE_QUIC") == "1")
                    Assert.Fail("QUIC is required by this job.");

                Assert.Ignore("The host does not provide QUIC.");
                return;
            }

            var url = TestObjects.Resources.GetServerAddress();

            if (protocol > 1) url = url.Replace("http://", "https://", StringComparison.Ordinal);

            using var certificate = protocol > 1 ? HttpsSmoke.CreateCertificate(X509KeyStorageFlags.Exportable) : null;
            using var stop = new CancellationTokenSource();
            using var server = (protocol == 1
                ? new WebServer(url)
                : new WebServer(
                    protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO,
                    certificate ?? throw new InvalidOperationException("Missing test certificate."),
                    url
                )).WithModule(new ActionModule(context => handler(context)));
            var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StateChanged += (_, change) =>
            {
                if (change.NewState == WebServerState.Listening) listening.TrySetResult();
            };
            var running = server.RunAsync(stop.Token);
            using var transport = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, peer, _, _) => certificate != null
                        && peer?.GetCertHashString()
                        == certificate.GetCertHashString()
                }
            };

            try
            {
                using var client = new HttpClient(transport)
                {
                    BaseAddress = new Uri(url),
                    Timeout = TimeSpan.FromSeconds(10),
                    DefaultRequestVersion = protocol == 3
                        ? HttpVersion.Version30
                        : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11,
                    DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                await listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await use(client);
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    internal sealed class BlockingResultStream : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes("stream"));
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Fail;
        internal int Closes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (Fail) throw new IOException("read failure");

            Entered.TrySetResult();
            await Continue.Task.WaitAsync(cancellationToken);
            return await _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Closes++;
                _inner.Dispose();

                if (Fail) throw new IOException("cleanup failure");
            }

            base.Dispose(disposing);
        }
    }
}
