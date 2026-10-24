using System;
using System.IO;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3RequestBodyAbortTest
    {
        // A body read the application abandoned (for example when its read deadline
        // answered 408) stays pending until the request ends and the server stops reading.
        // That local abort ends the input. It must not reset the response stream, which
        // already carries the committed response the client may not have read yet.
        [Test]
        public async Task LocalReadAbortEndsAnAbandonedBodyReadWithoutResettingTheResponse()
        {
            var streamType = EngineInternals.Find("EmbedIO.Quic.AsyncQuicStream");

            if (streamType == null)
            {
                Assert.Ignore("This build of the library has no EmbedIO.Quic engine.");
                return;
            }

            // The stream reports itself to its readiness callback; this one has nothing to do.
            var ready = Expression
                .Lambda(
                    typeof(Action<>).MakeGenericType(streamType),
                    Expression.Empty(),
                    Expression.Parameter(streamType)
                )
                .Compile();
            var quic = EngineInternals.Create(streamType, 0UL, ready);
            // A HEADERS frame; the request reader hands its field section on undecoded.
            EngineInternals.Call(quic, "Receive", new byte[] { 1, 2, 0, 0 }, false);
            var transport = EngineInternals.Create("EmbedIO.Net.Internal.Http3.EmbedIOQuicTransportStream", quic);
            var reader = EngineInternals.Create(
                "EmbedIO.Net.Internal.Http3.Http3RequestStream",
                0L,
                transport,
                65536,
                long.MaxValue
            );
            await EngineInternals
                .AwaitAsync(EngineInternals.Call(reader, "ReadEventAsync", CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));
            EngineInternals.Call(reader, "ConfirmHeaders", [null]);
            var connection = RuntimeHelpers.GetUninitializedObject(
                EngineInternals.Type("EmbedIO.Net.Internal.Http3.Http3QuicConnection")
            );
            var owner = EngineInternals.Create(
                "EmbedIO.Net.Internal.Http3.Http3QuicConnection+RequestScope",
                connection,
                transport
            );
            var body = EngineInternals.Create(
                "EmbedIO.Net.Internal.Http3.Http3RequestBody",
                0L,
                reader,
                owner
            ) as Stream
                ?? throw new AssertionException("The request body is not a stream.");

            var pending = body.ReadAsync(new byte[16], 0, 16, CancellationToken.None);
            Assert.That(pending.IsCompleted, Is.False, "The read must wait for request data.");
            // What the request worker does when the request ends with input still unread.
            EngineInternals.Call(quic, "AbortRead", 0x100UL);
            // NUnit 5's CatchAsync completes only when awaited; the read must end before CanRead is checked.
            var failure = await Assert.CatchAsync<IOException>(
                async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))
            );
            var writes = EngineInternals.Property(quic, "WritesClosed") as Task
                ?? throw new AssertionException("Missing write completion.");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure, Is.Not.Null);
                Assert.That(
                    EngineInternals.Field(quic, "_abortWrite"),
                    Is.Null,
                    "A local input abort must not reset the response."
                );
                Assert.That(writes.IsCompleted, Is.False, "The response direction must stay open.");
                Assert.That(body.CanRead, Is.False, "The ended input must not be read again.");
            }
        }
    }
}
