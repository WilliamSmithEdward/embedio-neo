using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using EmbedIO.Tests.TestObjects;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A connection gives its listener slot back once it can serve no more requests and none
    // still runs. After GOAWAY it keeps the slot while a request it accepted runs, and frees
    // it when that request ends, while the client still holds the connection open.
    public partial class Http3ListenerTest
    {
        [Test]
        public async Task ADrainedConnectionFreesItsSlotBeforeThePeerCloses()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            await DrainedConnectionWire();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task DrainedConnectionWire()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var endpoint = new IPEndPoint(IPAddress.Loopback, new Uri(prefix).Port);
            var authority = $"localhost:{endpoint.Port}";
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var server = new WebServer(o => o
                .WithUrlPrefix(prefix)
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate))
                .WithModule(new ActionModule("/hold", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    await release.Task;
                    await context.SendStringAsync("held", "text/plain", WebServer.Utf8NoBomEncoding);
                }))
                .WithModule(new ActionModule("/close", HttpVerbs.Get, context =>
                {
                    // A response that does not keep the connection alive makes it send GOAWAY.
                    context.Response.KeepAlive = false;
                    return context.SendStringAsync("closing", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var running = server.RunAsync(stop.Token);
            var accepted = new List<QuicStream>();
            QuicConnection? connection = null;
            QuicStream? control = null;
            Task<(long Limit, Task Open)>? goaway = null;

            try
            {
                (connection, control) = await Hold(endpoint, certificate, stop.Token);
                goaway = ReadGoawayAsync(connection, accepted, stop.Token);
                await using var held = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                await SendFieldSection(held, [(":method", "GET"), (":scheme", "https"), (":authority", authority), (":path", "/hold")], true, stop.Token);
                await entered.Task.WaitAsync(stop.Token);

                await using (var closing = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token))
                {
                    await SendFieldSection(closing, [(":method", "GET"), (":scheme", "https"), (":authority", authority), (":path", "/close")], true, stop.Token);
                    Assert.That((await ReadResponseFields(closing, stop.Token))[":status"], Is.EqualTo("200"));
                    await DrainBodyAsync(closing, stop.Token);
                }

                var (limit, open) = await goaway.WaitAsync(TimeSpan.FromSeconds(10), stop.Token);
                var whileRunning = Slots(server);
                release.TrySetResult();
                Assert.That((await ReadResponseFields(held, stop.Token))[":status"], Is.EqualTo("200"));
                await DrainBodyAsync(held, stop.Token);
                var clock = Stopwatch.StartNew();

                while (Slots(server) != 0 && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10, stop.Token);

                var afterDrain = Slots(server);

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(limit, Is.EqualTo(8));
                    Assert.That(whileRunning, Is.EqualTo(1), "A draining connection keeps its slot while a request runs.");
                    Assert.That(afterDrain, Is.Zero, "A drained connection frees its slot.");
                    // The server is still waiting for the client to close the connection.
                    Assert.That(open.IsCompleted, Is.False);
                    Assert.That(server.Listener.IsListening, Is.True);
                }
            }

            finally
            {
                if (control != null) await control.DisposeAsync();

                foreach (var stream in accepted) await stream.DisposeAsync();

                if (connection != null) await connection.DisposeAsync();

                // The client's pending reads end with the connection.
                if (goaway != null)
                {
                    await ((Task)goaway).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                    if (goaway.IsCompletedSuccessfully)
                        await goaway.Result.Open.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }

                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        // The connections holding the HTTP/3 listener's slots: established ones and those in
        // their handshake.
        private static int Slots(WebServer server)
        {
            var session = EngineInternals.Field(server.Listener, "_session")
                ?? throw new AssertionException("The HTTP/3 listener is not running.");
            return (int)(EngineInternals.Field(session, "_activeConnections") ?? throw new AssertionException("Missing active count."))
                + (int)(EngineInternals.Field(session, "_pendingHandshakes") ?? throw new AssertionException("Missing handshake count."));
        }

        // Accepts the server's unidirectional streams and reads its control stream up to GOAWAY.
        // Returns the GOAWAY's stream ID limit and a read that stays pending while the
        // connection is open.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<(long Limit, Task Open)> ReadGoawayAsync(QuicConnection connection, List<QuicStream> accepted, CancellationToken token)
        {
            while (true)
            {
                var stream = await connection.AcceptInboundStreamAsync(token);
                accepted.Add(stream);

                if (await ReadInteger(stream, token) != 0) continue;

                while (true)
                {
                    var type = await ReadInteger(stream, token);
                    var payload = new byte[(int)await ReadInteger(stream, token)];
                    await stream.ReadExactlyAsync(payload, token);

                    if (type != 7) continue;

                    using var frame = new MemoryStream(payload);
                    return (await ReadInteger(frame, token), stream.ReadAsync(new byte[1], token).AsTask());
                }
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task DrainBodyAsync(QuicStream stream, CancellationToken token)
        {
            using var body = new ClientDataStream(stream);
            await body.CopyToAsync(Stream.Null, token);
        }
    }
}
