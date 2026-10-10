using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http2InteroperabilityTest
    {
        private static Task SendResponseTrailers(object exchange, params string[] pairs)
        {
            var fields = Array.CreateInstance(Type("HpackField"), pairs.Length / 2);
            for (var i = 0; i < pairs.Length; i += 2)
                fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags, null,
                    new object[] { pairs[i], pairs[i + 1], false }, null), i / 2);
            return (Task)((exchange.GetType().GetMethod("SendTrailersAsync", Flags)
                ?? throw new AssertionException("Missing response trailer writer.")).Invoke(exchange,
                new object[] { fields, Property<CancellationToken>(exchange, "CancellationToken") })
                ?? throw new AssertionException("Missing trailer task."));
        }

        private static void ReserveResponseTrailers(object exchange)
            => (exchange.GetType().GetMethod("ExpectTrailers", Flags)
                ?? throw new AssertionException("Missing trailer reservation.")).Invoke(exchange, null);

        private static Task WriteResponseData(object exchange, byte[] bytes)
            => (Task)((exchange.GetType().GetMethod("WriteAsync", Flags)
                ?? throw new AssertionException("Missing body writer.")).Invoke(exchange,
                new object[] { bytes, 0, bytes.Length, false, Property<CancellationToken>(exchange, "CancellationToken") })
                ?? throw new AssertionException("Missing body task."));

        [TestCase(false, 0)]
        [TestCase(false, 3)]
        [TestCase(true, 0)]
        [TestCase(true, 3)]
        public async Task AdapterCompletesReservedTrailersAfterTheBody(bool length, int size)
        {
            var expected = Enumerable.Range(1, size).Select(value => (byte)value).ToArray();
            await WithServer(async exchange =>
            {
                var context = Adapter(exchange);
                try
                {
                    if (length) context.Response.ContentLength64 = size;
                    var sections = context.Response as IHttpResponseSections ?? throw new AssertionException("Missing response capability.");
                    sections.DeclareTrailers("X-Adapter");
                    if (size != 0) await context.Response.OutputStream.WriteAsync(expected, context.CancellationToken);
                    var trailers = new System.Net.WebHeaderCollection { ["X-Adapter"] = "snapshot" };
                    sections.SetTrailers(trailers);
                    trailers["X-Adapter"] = "changed-after-set";
                }
                finally { context.Close(); }
            }, async client =>
            {
                using var response = await client.GetAsync("adapter-trailers");
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected));
                Assert.That(response.TrailingHeaders.GetValues("x-adapter"), Is.EqualTo(new[] { "snapshot" }));
                Assert.That(response.Headers.Contains("x-adapter"), Is.False);
            });
        }

        [TestCase(false, 0)]
        [TestCase(false, 3)]
        [TestCase(true, 0)]
        [TestCase(true, 3)]
        public async Task ResponseTrailersFollowCompleteBodyWithoutPrematureEnd(bool length, int size)
        {
            var expected = Enumerable.Range(1, size).Select(value => (byte)value).ToArray();
            await WithServer(async exchange =>
            {
                ReserveResponseTrailers(exchange);
                await SendResponseHeaders(exchange, length
                    ? new[] { ":status", "200", "content-length", size.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : new[] { ":status", "200" }, false);
                if (size != 0) await WriteResponseData(exchange, expected);
                await SendResponseTrailers(exchange, "x-body-check", "verified", "x-second", "finished");
            }, async client =>
            {
                for (var i = 0; i < 3; i++)
                {
                    using var response = await client.GetAsync("trailers");
                    Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(expected));
                    Assert.That(response.TrailingHeaders.GetValues("x-body-check").Single(), Is.EqualTo("verified"));
                    Assert.That(response.TrailingHeaders.GetValues("x-second").Single(), Is.EqualTo("finished"));
                    Assert.That(response.Headers.Contains("x-body-check"), Is.False);
                }
            });
        }

        [Test]
        public async Task CoalescedCompleteBodyStillAllowsReservedTrailers()
        {
            await WithServer(async exchange =>
            {
                ReserveResponseTrailers(exchange);
                var fields = Array.CreateInstance(Type("HpackField"), 2);
                fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags, null,
                    new object[] { ":status", "200", false }, null), 0);
                fields.SetValue(Activator.CreateInstance(Type("HpackField"), Flags, null,
                    new object[] { "content-length", "3", false }, null), 1);
                await (Task)((exchange.GetType().GetMethod("SendHeadersAndWriteAsync", Flags)
                    ?? throw new AssertionException("Missing coalesced writer.")).Invoke(exchange,
                    new object[] { fields, new byte[] { 4, 5, 6 }, 0, 3, Property<CancellationToken>(exchange, "CancellationToken") })
                    ?? throw new AssertionException("Missing coalesced task."));
                await SendResponseTrailers(exchange, "x-after-body", "yes");
            }, async client =>
            {
                using var response = await client.GetAsync("coalesced-trailers");
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 4, 5, 6 }));
                Assert.That(response.TrailingHeaders.GetValues("x-after-body").Single(), Is.EqualTo("yes"));
            });
        }

        [Test]
        public async Task TrailersCannotFinishAnIncompleteDeclaredBody()
        {
            var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                ReserveResponseTrailers(exchange);
                await SendResponseHeaders(exchange, new[] { ":status", "200", "content-length", "3" }, false);
                await WriteResponseData(exchange, new byte[] { 1 });
                await Assert.ThatAsync(async () => await SendResponseTrailers(exchange, "x-finish", "early"),
                    Throws.InstanceOf<InvalidDataException>());
                await WriteResponseData(exchange, new byte[] { 2, 3 });
                await SendResponseTrailers(exchange, "x-finish", "complete");
                await Assert.ThatAsync(async () => await SendResponseTrailers(exchange, "x-repeat", "forbidden"),
                    Throws.InstanceOf<InvalidOperationException>());
                verified.TrySetResult();
            }, async client =>
            {
                using var response = await client.GetAsync("short-body");
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(new byte[] { 1, 2, 3 }));
                Assert.That(response.TrailingHeaders.GetValues("x-finish").Single(), Is.EqualTo("complete"));
                await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            });
        }

        [TestCase("content-length", "0")]
        [TestCase(":status", "200")]
        [TestCase("connection", "close")]
        [TestCase("te", "trailers")]
        [TestCase("content-type", "text/plain")]
        [TestCase("authorization", "Bearer example")]
        [TestCase("set-cookie", "name=value")]
        [TestCase("location", "/other")]
        [TestCase("x-invalid", "value\r\ninjected: true")]
        public async Task InvalidResponseTrailersDoNotCommitOrPoisonTheEncoder(string name, string value)
        {
            await WithServer(async exchange =>
            {
                ReserveResponseTrailers(exchange);
                await SendResponseHeaders(exchange, new[] { ":status", "200" }, false);
                await Assert.ThatAsync(async () => await SendResponseTrailers(exchange, name, value),
                    Throws.InstanceOf<InvalidDataException>());
                await SendResponseTrailers(exchange, "x-valid", "yes");
            }, async client =>
            {
                using var response = await client.GetAsync("invalid-trailer");
                Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.Empty);
                Assert.That(response.TrailingHeaders.GetValues("x-valid").Single(), Is.EqualTo("yes"));
            });
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task ReservedTrailersUnderBackpressureDoNotBlockSiblingResponses(bool reset)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var large = new byte[8 * 1024 * 1024];
            new Random(739).NextBytes(large);
            var pending = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var flowReady = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await WithServer(async exchange =>
            {
                if (Property<string>(Property<object>(exchange, "Request"), "Path") != "/slow-trailers")
                { await Respond(exchange, new byte[] { 11, 12, 13 }); return; }
                var context = Adapter(exchange);
                try
                {
                    var connection = exchange.GetType().GetField("_connection", Flags)?.GetValue(exchange)
                        ?? throw new AssertionException("Missing connection owner.");
                    var flow = connection.GetType().GetProperty("SendFlow", Flags)?.GetValue(connection)
                        ?? throw new AssertionException("Missing send flow control.");
                    flowReady.TrySetResult(flow);
                    context.Response.ContentLength64 = large.Length;
                    var sections = context.Response as IHttpResponseSections
                        ?? throw new AssertionException("Missing response capability.");
                    sections.DeclareTrailers("x-finished");
                    var write = context.Response.OutputStream.WriteAsync(large, context.CancellationToken).AsTask();
                    pending.TrySetResult(write);
                    await write;
                    sections.SetTrailers(new System.Net.WebHeaderCollection { ["x-finished"] = "yes" });
                    await context.Response.OutputStream.DisposeAsync();
                    if (reset) throw new AssertionException("A reset must interrupt the backpressured response.");
                    finished.TrySetResult();
                }
                catch (Exception error) when (reset && error is IOException or OperationCanceledException)
                { finished.TrySetResult(); }
                catch (Exception error) { finished.TrySetException(error); throw; }
                finally { context.Close(); }
            }, async client =>
            {
                using var response = await client.GetAsync("slow-trailers", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var write = await pending.Task.WaitAsync(deadline.Token);
                var flow = await flowReady.Task.WaitAsync(deadline.Token);
                while (Property<int>(flow, "PendingCount") == 0)
                {
                    if (write.IsCompleted) await write;
                    Assert.That(write.IsCompleted, Is.False, "Unread output must reach actual flow-control backpressure.");
                    await Task.Delay(10, deadline.Token);
                }
                Assert.That(response.TrailingHeaders.Contains("x-finished"), Is.False);
                Assert.That(await client.GetByteArrayAsync("sibling", deadline.Token), Is.EqualTo(new byte[] { 11, 12, 13 }));
                Assert.That(write.IsCompleted, Is.False, "The sibling must finish while the large response is blocked.");
                if (reset) response.Dispose();
                else
                {
                    Assert.That(await response.Content.ReadAsByteArrayAsync(deadline.Token), Is.EqualTo(large));
                    Assert.That(response.TrailingHeaders.GetValues("x-finished"), Is.EqualTo(new[] { "yes" }));
                }
                await finished.Task.WaitAsync(deadline.Token);
                Assert.That(await client.GetByteArrayAsync("after", deadline.Token), Is.EqualTo(new byte[] { 11, 12, 13 }));
                Assert.That(Property<int>(flow, "PendingCount"), Is.Zero);
            });
        }

    }
}
