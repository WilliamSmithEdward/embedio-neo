using System;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        // Disposal can cancel the first UDP receive while .NET is still starting it. On
        // Windows that receive then completes at once with OperationAborted rather than as
        // canceled; shutdown must treat it as the stop it is and not report a failure.
        [Test]
        public async Task ListenerBindingDisposedAtOnceStopsCleanly()
        {
            var assembly = typeof(WebServer).Assembly;
            var bindingType = assembly.GetType("EmbedIO.Net.Internal.Http3.EmbedIOQuicListenerBinding")
                ?? throw new AssertionException("Missing EmbedIO.Quic listener binding.");
            var sessionType = assembly.GetType("EmbedIO.Quic.TlsSession")
                ?? throw new AssertionException("Missing TLS session type.");
            // No datagram arrives, so the binding never asks for a TLS session.
            var parameter = Expression.Parameter(typeof(byte[]));
            var unused = Expression
                .Lambda(
                    typeof(Func<,>).MakeGenericType(typeof(byte[]), sessionType),
                    Expression.Throw(Expression.New(typeof(InvalidOperationException)), sessionType),
                    parameter
                )
                .Compile();

            // Concurrent cycles vary when each first receive starts relative to its disposal.
            await Task.WhenAll(
                Enumerable
                    .Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8))
                    .Select(_ => Task.Run(Cycles))
            );

            async Task Cycles()
            {
                for (var iteration = 0; iteration < 150; iteration++)
                {
                    var binding = Activator.CreateInstance(
                        bindingType,
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        [
                            new IPEndPoint(IPAddress.Loopback, 0),
                            unused
                        ],
                        null
                    ) as IAsyncDisposable
                        ?? throw new AssertionException("The binding is not asynchronously disposable.");

                    await binding
                        .DisposeAsync()
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        }
    }
}
