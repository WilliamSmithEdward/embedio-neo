using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http1AbortRetirementTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task ConcurrentAbortCannotFinalizeAnUnwrittenResponse(bool lateOutput)
        {
            var prefix = Resources.GetServerAddress();
            using var listener = new Net.HttpListener();
            listener.AddPrefix(prefix);
            listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = Http1AbortProbe.AssertClosedWithoutResponseAsync(prefix);
            var context = await listener.GetContextAsync(deadline.Token);
            var connection = EngineInternals.Property(context, "Connection")
                ?? throw new AssertionException("Missing accepted connection.");
            var heartbeat = EngineInternals.Type("EmbedIO.Net.Internal.Http1OutputHeartbeat");
            using var registration = (IDisposable)EngineInternals.Create(
                "EmbedIO.Net.Internal.Http1OutputHeartbeat+Registration"
            );
            EngineInternals.SetField(connection, "_outputHeartbeat", registration);

            if (!lateOutput) _ = context.Response.OutputStream;

            var heartbeatGate = heartbeat
                .GetField("Sync", EngineInternals.Members)
                ?.GetValue(null)
                ?? throw new AssertionException("Missing heartbeat gate.");
            Task closing;

            lock (heartbeatGate)
            {
                // Registration disposal pauses the first close after it claims
                // retirement, before output abandonment and socket shutdown.
                closing = Task.Run(() => EngineInternals.Call(connection, "ForceClose"));
                Assert.That(
                    () => EngineInternals.Field(connection, "_protocolEnded"),
                    Is
                        .EqualTo(1)
                        .After(5000, 10)
                );

                if (lateOutput) _ = context.Response.OutputStream;

                context.CancellationToken = new CancellationToken(true);
                context.Close();
            }

            await closing.WaitAsync(deadline.Token);
            await response.WaitAsync(deadline.Token);
            Assert.That(TcpSessionProbe.Closed(connection), Is.True);
        }
    }
}
