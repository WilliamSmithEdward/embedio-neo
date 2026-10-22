using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3RequestBodyAbortTest
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // A body read the application abandoned (for example when its read deadline
        // answered 408) stays pending until the request ends and the server stops reading.
        // That local abort ends the input. It must not reset the response stream, which
        // already carries the committed response the client may not have read yet.
        [Test]
        public async Task LocalReadAbortEndsAnAbandonedBodyReadWithoutResettingTheResponse()
        {
            var assembly = typeof(WebServer).Assembly;
            var streamType = assembly.GetType("EmbedIO.Quic.AsyncQuicStream");

            if (streamType == null)
            {
                Assert.Ignore("The legacy assembly has no EmbedIO.Quic engine.");
                return;
            }

            var quic = Create(
                streamType,
                0UL,
                (Action)(() => { })
            );
            // A HEADERS frame; the request reader hands its field section on undecoded.
            Call(quic, "Receive", new byte[] { 1, 2, 0, 0 }, false);
            var transport = Create(Type(assembly, "EmbedIO.Net.Internal.Http3.EmbedIOQuicTransportStream"), quic);
            var reader = Create(
                Type(assembly, "EmbedIO.Net.Internal.Http3.Http3RequestStream"),
                0L,
                transport,
                65536,
                long.MaxValue
            );
            await AwaitValueTask(Call(reader, "ReadEventAsync", CancellationToken.None));
            Call(reader, "ConfirmHeaders", [null]);
            var connection = RuntimeHelpers.GetUninitializedObject(
                Type(assembly, "EmbedIO.Net.Internal.Http3.Http3QuicConnection")
            );
            var owner = Create(
                Type(assembly, "EmbedIO.Net.Internal.Http3.Http3QuicConnection+RequestScope"),
                connection,
                transport
            );
            var body = Create(
                Type(assembly, "EmbedIO.Net.Internal.Http3.Http3RequestBody"),
                0L,
                reader,
                owner
            ) as Stream
                ?? throw new AssertionException("The request body is not a stream.");

            var pending = body.ReadAsync(new byte[16], 0, 16, CancellationToken.None);
            Assert.That(pending.IsCompleted, Is.False, "The read must wait for request data.");
            // What the request worker does when the request ends with input still unread.
            Call(quic, "AbortRead", 0x100UL);
            var failure = Assert.CatchAsync<IOException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var writes = Property(quic, "WritesClosed") as Task
                ?? throw new AssertionException("Missing write completion.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(Field(quic, "_abortWrite"), Is.Null, "A local input abort must not reset the response.");
                Assert.That(writes.IsCompleted, Is.False, "The response direction must stay open.");
                Assert.That(body.CanRead, Is.False, "The ended input must not be read again.");
            }
        }

        private static Type Type(Assembly assembly, string name) =>
            assembly.GetType(name) ?? throw new AssertionException("Missing type: " + name);

        private static object Create(Type type, params object?[] arguments) =>
            Activator.CreateInstance(type, Members, null, arguments, null)
                ?? throw new AssertionException("Could not create " + type.Name + ".");

        private static object? Call(object target, string name, params object?[] arguments) =>
            (target
                .GetType()
                .GetMethod(name, Members)
                ?? throw new AssertionException("Missing method: " + name)).Invoke(target, arguments);

        private static object? Property(object target, string name) =>
            (target
                .GetType()
                .GetProperty(name, Members)
                ?? throw new AssertionException("Missing property: " + name)).GetValue(target);

        private static object? Field(object target, string name) =>
            (target
                .GetType()
                .GetField(name, Members)
                ?? throw new AssertionException("Missing field: " + name)).GetValue(target);

        private static async Task AwaitValueTask(object? valueTask)
        {
            var source = valueTask ?? throw new AssertionException("Missing pending read.");
            var task = source
                .GetType()
                .GetMethod("AsTask")
                ?.Invoke(source, null) as Task
                ?? throw new AssertionException("Missing task conversion.");
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
