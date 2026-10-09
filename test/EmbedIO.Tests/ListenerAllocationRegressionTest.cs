using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.PlatformTests;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ListenerAllocationRegressionTest
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type ConnectionType = (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpConnection", true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        [TestCase(0, 0)]
        [TestCase(0, 1)]
        [TestCase(0, 8192)]
        [TestCase(3, 0)]
        [TestCase(3, 1)]
        [TestCase(3, 8192)]
        [TestCase(8192, 0)]
        [TestCase(8192, 1)]
        [TestCase(8192, 8192)]
        public void BodyDrainPreservesFollowingBytesAndClearsTemporaryStorage(int buffered, int consumed)
        {
            var body = Enumerable.Repeat((byte)'b', 8192).ToArray();
            var remaining = body.Skip(buffered).Concat(Encoding.ASCII.GetBytes("NEXT")).ToArray();
            using var source = new CapturingSource(remaining);
            var connection = NewConnection(source);
            var context = Context(connection);
            context.Request.Headers["Content-Length"] = "8192";
            Field("_pendingInput").SetValue(connection, new ArraySegment<byte>(body, 0, buffered));
            var input = context.Request.InputStream;
            var destination = new byte[consumed];
            input.ReadExactly(destination);
            Assert.That(destination, Is.EqualTo(body.Take(consumed).ToArray()));
            source.Buffers.Clear();
            var before = source.ReadCalls;
            Assert.That(Flush(context), Is.True);
            Assert.That(source.Position, Is.EqualTo(8192 - buffered));
            Assert.That(input.ReadByte(), Is.EqualTo(-1));
            if (consumed == body.Length) Assert.That(source.ReadCalls, Is.EqualTo(before));
            foreach (var bytes in source.Buffers) Assert.That(bytes, Is.All.Zero, "Returned temporary storage must not retain request data.");
            Assert.That(source.ReadByte(), Is.EqualTo((int)'N'));
        }

        [TestCase("io", false)]
        [TestCase("disposed", true)]
        [TestCase("getter-disposed", true)]
        public void BodyDrainKeepsExistingFailureResultsAndClearsStorage(string failure, bool expected)
        {
            using var source = new CapturingSource(new byte[4096]) { Failure = failure };
            var connection = NewConnection(source);
            var context = Context(connection);
            context.Request.Headers["Content-Length"] = "4096";
            if (failure == "getter-disposed") Field("_resourcesDisposed").SetValue(connection, 1);
            Assert.That(Flush(context), Is.EqualTo(expected));
            foreach (var bytes in source.Buffers) Assert.That(bytes, Is.All.Zero);
        }

        [TestCase(0, 0)]
        [TestCase(0, 1024)]
        [TestCase(128, 0)]
        [TestCase(128, 1024)]
        [TestCase(4096, 0)]
        [TestCase(4096, 1024)]
        [TestCase(20000, 0)]
        [TestCase(20000, 1024)]
        public void HeaderBytesPreserveUtf8OrderAndBufferGrowth(int size, int bodyLength)
        {
            var context = Context(NewConnection(Stream.Null));
            context.Response.StatusCode = 201;
            var value = "caf\u00e9-" + new string('a', size);
            context.Response.Headers["X-Text"] = value;
            context.Response.Headers["X-Last"] = "tail";
            var expected = Encoding.UTF8.GetBytes($"HTTP/1.1 201 Created\r\nX-Text: {value}\r\nX-Last: tail\r\n\r\n");
            using var wire = (MemoryStream)((((context).Response.GetType().GetMethod("WriteHeaders", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(context.Response, null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            Assert.That((wire).Position, Is.EqualTo(0), "Default encoding has no preamble.");
            Assert.That(wire.ToArray(), Is.EqualTo(expected));
            var body = Enumerable.Repeat((byte)'z', bodyLength).ToArray();
            wire.Position = wire.Length;
            wire.Write(body);
            Assert.That(wire.ToArray(), Is.EqualTo(expected.Concat(body).ToArray()));
        }

        [TestCase(false, "full")]
        [TestCase(true, "full")]
        [TestCase(false, "partial")]
        [TestCase(true, "partial")]
        [TestCase(false, "none")]
        [TestCase(true, "none")]
        public async Task PostBodiesKeepSequentialRequestBoundariesAcrossConsumptionModes(bool secure, string mode)
        {
            using var certificate = secure ? HttpsSmoke.CreateCertificate() : null;
            var url = HttpsSmoke.GetUrl();
            if (!secure) url = url.Replace("https://", "http://", StringComparison.Ordinal);
            using var server = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO);
                if (secure) options.WithCertificate(certificate);
            })
                .WithModule(new ActionModule("/", HttpVerbs.Post, async context =>
                {
                    var count = mode == "full" ? 65536 : mode == "partial" ? 13 : 0;
                    var bytes = new byte[count];
                    await context.Request.InputStream.ReadExactlyAsync(bytes, context.CancellationToken);
                    Assert.That(bytes, Is.All.EqualTo((byte)'b'));
                    await context.SendStringAsync(context.Request.RawTarget, "text/plain", Encoding.UTF8);
                }));
            using var stop = new CancellationTokenSource();
            var running = server.RunAsync(stop.Token);
            using var client = secure ? HttpsSmoke.CreateClient((certificate ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))) : new HttpClient();
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    using var body = new ByteArrayContent(Enumerable.Repeat((byte)'b', 65536).ToArray());
                    using var response = await client.PostAsync(url + i, body);
                    Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo($"/{i}"));
                }
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        private static bool Flush(IHttpContext context)
            => (bool)((((context).Request.GetType().GetMethod("FlushInput", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(context.Request, null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static IHttpContext Context(object connection) => (IHttpContext)(Field("_context").GetValue(connection) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static FieldInfo Field(string name) => (ConnectionType.GetField(name, PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static object NewConnection(Stream source)
        {
            var connection = RuntimeHelpers.GetUninitializedObject(ConnectionType);
            Field("_connectionSync").SetValue(connection, new object());
            Field("<Stream>k__BackingField").SetValue(connection, source);
            ((ConnectionType).GetMethod("Init", PrivateInstance) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(connection, null);
            return connection;
        }

        private sealed class CapturingSource(byte[] bytes) : MemoryStream(bytes)
        {
            internal readonly List<byte[]> Buffers = new();
            internal int ReadCalls;
            internal string? Failure;
            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                Buffers.Add(buffer);
                if (Failure == "io" || Failure == "disposed")
                {
                    Array.Fill(buffer, (byte)'s');
                    if (Failure == "io") throw new IOException("Controlled drain failure.");
                    throw new ObjectDisposedException("Controlled drain disposal.");
                }
                return base.Read(buffer, offset, Math.Min(count, 511));
            }
        }
    }
}
