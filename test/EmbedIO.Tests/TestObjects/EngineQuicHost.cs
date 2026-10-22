using System;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Net;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    // Runs the library's EmbedIO.Quic listener binding on loopback and hands each
    // accepted connection to a test, once its handshake completes, as the transport the
    // internal HTTP/3 connection layer consumes. Tests drive that layer directly over
    // the production QUIC provider; an independent client connects to LocalEndPoint.
    internal sealed class EngineQuicHost : IAsyncDisposable
    {
        private const string Http3 = "EmbedIO.Net.Internal.Http3.";
        private readonly IAsyncDisposable _binding;

        private EngineQuicHost(IAsyncDisposable binding) => _binding = binding;

        internal static EngineQuicHost Start(X509Certificate2 certificate)
        {
            var factory = EngineInternals.CallStatic(
                EngineInternals.Type(Http3 + "EmbedIOQuicTls"),
                "CreateFactory",
                new ServerCertificateSource(certificate),
                null
            );
            var binding = EngineInternals.Create(
                Http3 + "EmbedIOQuicListenerBinding",
                new IPEndPoint(IPAddress.Loopback, 0),
                factory
            ) as IAsyncDisposable
                ?? throw new AssertionException("The EmbedIO.Quic listener binding is not disposable.");
            return new EngineQuicHost(binding);
        }

        internal IPEndPoint LocalEndPoint =>
            EngineInternals.Property(_binding, "LocalEndPoint") as IPEndPoint
                ?? throw new AssertionException("Missing listener endpoint.");

        // Accepts the next connection and returns its HTTP/3 transport, which the caller owns.
        internal async Task<IAsyncDisposable> AcceptTransportAsync(CancellationToken token) =>
            (await AcceptAsync(token).ConfigureAwait(false)).Transport;

        // The same, with the endpoints the listener recorded for the connection.
        internal async Task<(IAsyncDisposable Transport, IPEndPoint Remote, IPEndPoint Local)> AcceptAsync(
            CancellationToken token
        )
        {
            var peer = await EngineInternals.AwaitAsync(EngineInternals.Call(_binding, "AcceptAsync", token))
                ?? throw new AssertionException("The listener returned no connection.");
            var remote = EngineInternals.Field(peer, "_remote") as IPEndPoint
                ?? throw new AssertionException("Missing remote endpoint.");
            var local = EngineInternals.Field(peer, "_local") as IPEndPoint
                ?? throw new AssertionException("Missing local endpoint.");
            await EngineInternals.AwaitAsync(EngineInternals.Call(peer, "WaitReadyAsync", token));
            var ready = EngineInternals.Property(
                EngineInternals.Field(peer, "_ready") ?? throw new AssertionException("Missing connection readiness."),
                "Task"
            ) as Task
                ?? throw new AssertionException("Missing connection readiness.");
            await ready
                .WaitAsync(token)
                .ConfigureAwait(false);
            var state = await EngineInternals.AwaitAsync(ready)
                ?? throw new AssertionException("Missing ready connection state.");
            var transport = EngineInternals.Create(
                Http3 + "EmbedIOQuicTransportConnection",
                EngineInternals.Field(state, "Item1"),
                EngineInternals.Field(peer, "_owner"),
                EngineInternals.Field(state, "Item2")
            ) as IAsyncDisposable
                ?? throw new AssertionException("The EmbedIO.Quic transport is not disposable.");
            return (transport, remote, local);
        }

        public ValueTask DisposeAsync() => _binding.DisposeAsync();

        // The dispatch delegate the HTTP/3 connection layer calls for each exchange: the
        // generic method `name` of `owner`, closed over the internal exchange type and
        // bound to `target` when it is an instance method.
        internal static Delegate Dispatch(Type owner, string name, object? target = null)
        {
            var exchange = EngineInternals.Type(Http3 + "Http3QuicExchange");
            var method = owner.GetMethod(name, EngineInternals.Members)
                ?? throw new AssertionException("Missing exchange handler: " + name);
            return method
                .MakeGenericMethod(exchange)
                .CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)), target);
        }

        // An HTTP/3 connection layer over an accepted transport, without WebTransport.
        // Like the listener, it dispatches each exchange on its own worker unless
        // dispatchInline is set.
        internal static IDisposable CreateConnection(
            IAsyncDisposable transport,
            Delegate dispatch,
            CancellationToken token,
            bool dispatchInline = false
        ) =>
            EngineInternals.Create(
                Http3 + "Http3QuicConnection",
                transport,
                dispatch,
                token,
                dispatchInline,
                null
            ) as IDisposable
                ?? throw new AssertionException("The HTTP/3 connection layer is not disposable.");

        // Runs the HTTP/3 connection layer to completion, then releases it and its transport.
        internal static async Task RunAsync(
            IAsyncDisposable transport,
            Delegate dispatch,
            CancellationToken token,
            bool dispatchInline = false
        )
        {
            await using (transport.ConfigureAwait(false))
            using (var session = CreateConnection(transport, dispatch, token, dispatchInline))
                await RunCoreAsync(session).ConfigureAwait(false);
        }

        internal static Task RunCoreAsync(IDisposable session) =>
            EngineInternals.Call(session, "RunCoreAsync") as Task
                ?? throw new AssertionException("Missing HTTP/3 connection runner.");
    }
}
