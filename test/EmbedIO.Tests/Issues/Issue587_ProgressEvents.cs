using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Testing;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests.Issues
{
    public class Issue587_ProgressEvents
    {
        [TestCase("", "data: \n\n")]
        [TestCase("10", "data: 10\n\n")]
        [TestCase("café ☕", "data: café ☕\n\n")]
        [TestCase("one\ntwo\n", "data: one\ndata: two\ndata: \n\n")]
        [TestCase("one\rtwo\r\nthree", "data: one\ndata: two\ndata: three\n\n")]
        [TestCase("safe\nevent: injected\n\n", "data: safe\ndata: event: injected\ndata: \ndata: \n\n")]
        public async Task DataIsFramedWithoutFieldInjection(string data, string expected)
        {
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get,
                context => context.OpenEventStream().WriteAsync(data)), async client =>
            {
                using var response = await client.GetAsync("/");
                Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
                Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(expected));
            });
        }

        [TestCase("bad\nevent", false)]
        [TestCase("bad\revent", false)]
        [TestCase("bad\0event", false)]
        [TestCase("bad\nid", true)]
        [TestCase("bad\rid", true)]
        [TestCase("bad\0id", true)]
        public async Task InvalidFieldsAreRejectedBeforeWriting(string value, bool isId)
        {
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, async context =>
            {
                var events = context.OpenEventStream();
                Assert.Throws<ArgumentException>(() => events.WriteAsync("unsafe", isId ? null : value, isId ? value : null));
                await events.WriteAsync("safe", "progress", "");
            }), async client => Assert.That(await client.GetStringAsync("/"), Is.EqualTo("event: progress\nid: \ndata: safe\n\n")));
        }

        [Test]
        public async Task HeartbeatDoesNotCreateAnEventAndNullDataIsRejected()
        {
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, async context =>
            {
                var events = context.OpenEventStream();
                Assert.Throws<ArgumentNullException>(() => events.WriteAsync(null));
                Assert.Throws<ArgumentNullException>(() => events.WriteCommentAsync(null));
                await events.WriteCommentAsync("alive\r\ndata: still a comment");
                await events.WriteAsync("done", "complete", "10");
            }), async client => Assert.That(await client.GetStringAsync("/"),
                Is.EqualTo(": alive\n: data: still a comment\n\nevent: complete\nid: 10\ndata: done\n\n")));
            Assert.Throws<ArgumentNullException>(() => HttpContextExtensions.OpenEventStream(null));
        }

        [Test]
        public async Task CanceledWriteDoesNotWriteAndWriterCanBeReused()
        {
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, async context =>
            {
                var events = context.OpenEventStream();
                using var cancel = new CancellationTokenSource();
                cancel.Cancel();
                await Assert.ThatAsync(() => events.WriteAsync("not sent", cancellationToken: cancel.Token), Throws.InstanceOf<OperationCanceledException>());
                await events.WriteAsync("sent");
            }), async client => Assert.That(await client.GetStringAsync("/"), Is.EqualTo("data: sent\n\n")));
        }

        [Test]
        public async Task TransportFailuresPropagateWithoutLeavingWriterBusy()
        {
            await TestWebServer.UseAsync(server => server.WithAction("/", HttpVerbs.Get, async context =>
            {
                var events = context.OpenEventStream();
                context.Response.OutputStream.Dispose();
                await Assert.ThatAsync(() => events.WriteAsync("first"), Throws.InstanceOf<ObjectDisposedException>());
                await Assert.ThatAsync(() => events.WriteCommentAsync(), Throws.InstanceOf<ObjectDisposedException>());
            }), async client => { using var response = await client.GetAsync("/"); });
        }

        [Test]
        public async Task OverlappingWritesAreRejectedWithoutInterleavingFrames()
        {
            using var stream = new ControlledStream();
            var events = CreateWriter(stream);
            var first = events.WriteAsync("first");
            await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await Assert.ThatAsync(() => events.WriteAsync("overlap"), Throws.InstanceOf<InvalidOperationException>());
            }
            finally { stream.Release.TrySetResult(); }
            await first;
            await events.WriteAsync("next");
            Assert.That(System.Text.Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("data: first\n\ndata: next\n\n"));
        }

        [Test]
        public async Task FlushFailurePropagatesAndReleasesWriteGuard()
        {
            using var stream = new ControlledStream { FailFlush = true };
            stream.Release.TrySetResult();
            var events = CreateWriter(stream);
            await Assert.ThatAsync(() => events.WriteAsync("first"), Throws.InstanceOf<IOException>());
            stream.FailFlush = false;
            await events.WriteAsync("next");
            Assert.That(System.Text.Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("data: first\n\ndata: next\n\n"));
        }

        private static ServerSentEventWriter CreateWriter(Stream stream)
        {
            var response = DispatchProxy.Create<IHttpResponse, PropertyProxy>();
            var responseProperties = ((PropertyProxy)(object)response).Values;
            responseProperties["Headers"] = new System.Net.WebHeaderCollection();
            responseProperties["OutputStream"] = stream;
            var context = DispatchProxy.Create<IHttpContext, PropertyProxy>();
            var contextProperties = ((PropertyProxy)(object)context).Values;
            contextProperties["Response"] = response;
            contextProperties["CancellationToken"] = CancellationToken.None;
            return context.OpenEventStream();
        }

        // Test doubles for deterministic transport backpressure and flush failures.
        public class PropertyProxy : DispatchProxy
        {
            public Dictionary<string, object?> Values { get; } = new();
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                if (targetMethod is null) throw new System.NullReferenceException();
                if (args is null) throw new System.NullReferenceException();
                var name = targetMethod.Name;
                if (name.StartsWith("get_", StringComparison.Ordinal)) return Values[name.Substring(4)];
                if (name.StartsWith("set_", StringComparison.Ordinal)) { Values[name.Substring(4)] = args[0]; return null; }
                throw new NotSupportedException(name);
            }
        }

        private sealed class ControlledStream : MemoryStream
        {
            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool FailFlush { get; set; }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                await base.WriteAsync(buffer, offset, count, cancellationToken);
            }
            public override Task FlushAsync(CancellationToken cancellationToken)
                => FailFlush ? Task.FromException(new IOException("Controlled flush failure")) : base.FlushAsync(cancellationToken);
        }

        [TestCase(HttpListenerMode.EmbedIO, false)]
        [TestCase(HttpListenerMode.Microsoft, false)]
        [TestCase(HttpListenerMode.EmbedIO, true)]
        [TestCase(HttpListenerMode.Microsoft, true)]
        public async Task ProgressArrivesBeforeCompletionAndObservesServerCancellation(HttpListenerMode mode, bool cancelServer)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = false;
            var url = Resources.GetServerAddress();
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(options => options.WithUrlPrefix(url).WithMode(mode))
                .WithAction("/", HttpVerbs.Get, async context =>
                {
                    var events = context.OpenEventStream();
                    try
                    {
                        await events.WriteAsync("10", "progress");
                        await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await events.WriteAsync("100", "progress");
                        await events.WriteAsync("done", "complete");
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { canceled = true; }
                    finally { finished.TrySetResult(); }
                });
            var running = server.RunAsync(stop.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));
                Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
                Assert.That(response.Headers.TransferEncodingChunked, Is.True);
                Assert.That(response.Content.Headers.ContentLength, Is.Null);
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
                Assert.That(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo("event: progress"));
                Assert.That(await reader.ReadLineAsync(), Is.EqualTo("data: 10"));
                Assert.That(await reader.ReadLineAsync(), Is.EqualTo(""));
                Assert.That(finished.Task.IsCompleted, Is.False, "First progress must arrive while the handler is still active.");
                if (cancelServer) stop.Cancel();
                release.TrySetResult();
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(canceled, Is.EqualTo(cancelServer));
                if (!cancelServer)
                    Assert.That(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)),
                        Is.EqualTo("event: progress\ndata: 100\n\nevent: complete\ndata: done\n\n"));
            }
            finally
            {
                release.TrySetResult();
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
