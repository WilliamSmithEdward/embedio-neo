using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Actions;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public partial class Http3ListenerTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public async Task GracefulListenerDrainPreservesAcceptedResponse(bool concurrent)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("drained response", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            var response = client.GetStringAsync(prefix, stop.Token);
            try
            {
                await entered.Task.WaitAsync(stop.Token);
                var drain = server.DrainAsync(TimeSpan.FromSeconds(10));
                var other = concurrent ? server.DrainAsync(TimeSpan.FromMilliseconds(1)) : Task.CompletedTask;
                if (concurrent) await Task.Delay(50, stop.Token);
                Assert.That(drain.IsCompleted, Is.False, "An accepted callback still owns its response.");
                release.TrySetResult();
                Assert.That(await response.WaitAsync(stop.Token), Is.EqualTo("drained response"));
                client.Dispose(); // A cooperative client closes after receiving GOAWAY and its body.
                await Task.WhenAll(drain, other).WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
                Assert.That(server.Listener.IsListening, Is.False);
                using var replacement = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
                replacement.Listener.Start();
                Assert.That(replacement.Listener.IsListening, Is.True);
            }
            finally { release.TrySetResult(); stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [TestCase("deadline")]
        [TestCase("drain-cancel")]
        [TestCase("run-cancel")]
        [TestCase("listener-stop")]
        [TestCase("dispose")]
        public async Task GracefulListenerDrainCanAbortUnfinishedResponses(string reason)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var cancelDrain = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                    finally { canceled.TrySetResult(); }
                }));
            var running = server.RunAsync(stop.Token);
            using var client = Client(certificate);
            var response = client.GetStringAsync(prefix, stop.Token);
            try
            {
                await entered.Task.WaitAsync(stop.Token);
                var drain = server.DrainAsync(reason == "deadline" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(20), cancelDrain.Token);
                if (reason == "drain-cancel") cancelDrain.Cancel();
                if (reason == "run-cancel") stop.Cancel();
                if (reason == "listener-stop") server.Listener.Stop();
                if (reason == "dispose") server.Dispose();
                if (reason == "drain-cancel")
                    await Assert.ThatAsync(async () => await drain.WaitAsync(TimeSpan.FromSeconds(5)), Throws.InstanceOf<OperationCanceledException>());
                else await drain.WaitAsync(TimeSpan.FromSeconds(5));
                await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.ThatAsync(async () => await response, Throws.InstanceOf<HttpRequestException>().Or.InstanceOf<OperationCanceledException>());
                Assert.That(server.Listener.IsListening, Is.False);
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }

        [Test]
        public async Task ListenerDrainSendsGoAwayOnEveryConnectionAndRejectsLaterWork()
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            using var certificate = Certificate();
            var prefix = Prefix();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var server = new WebServer(o => o.WithUrlPrefix(prefix).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate))
                .WithModule(new ActionModule("/", HttpVerbs.Get, async context =>
                {
                    if (Interlocked.Increment(ref count) == 2) entered.TrySetResult();
                    await release.Task.WaitAsync(context.CancellationToken);
                    await context.SendStringAsync("done", "text/plain", WebServer.Utf8NoBomEncoding);
                }));
            var running = server.RunAsync(stop.Token);
            var clients = new List<QuicConnection>();
            var streams = new List<QuicStream>();
            var requests = new List<QuicStream>();
            var controls = new List<QuicStream>();
            var uri = new Uri(prefix);
            var authority = Encoding.ASCII.GetBytes(uri.Authority);
            var wire = new byte[9 + authority.Length];
            new byte[] { 1, (byte)(7 + authority.Length), 0, 0, 0xd1, 0xd7, 0xc1, 0x50, (byte)authority.Length }.CopyTo(wire, 0);
            authority.CopyTo(wire, 9);
            try
            {
                for (var index = 0; index < 2; ++index)
                {
                    var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
                    {
                        RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                        DefaultCloseErrorCode = 0x100,
                        DefaultStreamErrorCode = 0x10c,
                        MaxInboundBidirectionalStreams = 0,
                        MaxInboundUnidirectionalStreams = 8,
                        ClientAuthenticationOptions = new SslClientAuthenticationOptions
                        {
                            TargetHost = "localhost",
                            ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                            RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString()
                        }
                    }, stop.Token);
                    clients.Add(client);
                    var control = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, stop.Token); streams.Add(control);
                    await control.WriteAsync(new byte[] { 0, 4, 0 }, stop.Token);
                    var request = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token); streams.Add(request); requests.Add(request);
                    await request.WriteAsync(wire, true, stop.Token);
                    for (var streamIndex = 0; streamIndex < 2; ++streamIndex)
                    {
                        var peer = await client.AcceptInboundStreamAsync(stop.Token); streams.Add(peer);
                        if (await ReadDrainInteger(peer, stop.Token) != 0) continue;
                        Assert.That(await ReadDrainInteger(peer, stop.Token), Is.EqualTo(4));
                        var settings = new byte[checked((int)await ReadDrainInteger(peer, stop.Token))];
                        await peer.ReadExactlyAsync(settings, stop.Token);
                        controls.Add(peer);
                    }
                }
                await entered.Task.WaitAsync(stop.Token);
                var drain = server.DrainAsync(TimeSpan.FromSeconds(10));
                Assert.That(controls, Has.Count.EqualTo(2));
                foreach (var control in controls)
                {
                    Assert.That(await ReadDrainInteger(control, stop.Token), Is.EqualTo(7));
                    Assert.That(await ReadDrainInteger(control, stop.Token), Is.EqualTo(1));
                    Assert.That(await ReadDrainInteger(control, stop.Token), Is.EqualTo(4));
                }
                foreach (var client in clients)
                {
                    await using var late = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token);
                    QuicException? rejected = null;
                    try { await late.WriteAsync(wire, true, stop.Token); await late.ReadExactlyAsync(new byte[1], stop.Token); }
                    catch (QuicException error) { rejected = error; }
                    Assert.That(rejected?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                    Assert.That(rejected?.ApplicationErrorCode, Is.EqualTo(0x10b));
                }
                using (var fresh = Client(certificate))
                using (var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
                    await Assert.ThatAsync(async () => await fresh.GetStringAsync(prefix, probe.Token),
                        Throws.InstanceOf<HttpRequestException>().Or.InstanceOf<OperationCanceledException>());
                Assert.That(count, Is.EqualTo(2));
                Assert.That(drain.IsCompleted, Is.False);
                release.TrySetResult();
                foreach (var request in requests)
                {
                    using var response = new MemoryStream();
                    await request.CopyToAsync(response, stop.Token);
                    var bytes = response.ToArray();
                    Assert.That(bytes.AsSpan(bytes.Length - 4).ToArray(), Is.EqualTo(Encoding.ASCII.GetBytes("done")));
                }
                foreach (var client in clients) await client.CloseAsync(0x100, stop.Token);
                await drain.WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                release.TrySetResult(); stop.Cancel();
                foreach (var stream in streams) await stream.DisposeAsync();
                foreach (var client in clients) await client.DisposeAsync();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        private static async Task<long> ReadDrainInteger(Stream stream, CancellationToken token)
        {
            var bytes = new byte[8];
            await stream.ReadExactlyAsync(bytes.AsMemory(0, 1), token);
            var size = 1 << (bytes[0] >> 6);
            if (size > 1) await stream.ReadExactlyAsync(bytes.AsMemory(1, size - 1), token);
            long value = bytes[0] & 63;
            for (var index = 1; index < size; ++index) value = (value << 8) | bytes[index];
            return value;
        }

        [Test]
        public async Task EmptyListenerDrainsWithoutWaitingForTheDeadline()
        {
            using var certificate = Certificate();
            using var server = new WebServer(o => o.WithUrlPrefix(Prefix()).WithMode(HttpListenerMode.EmbedIOHttp3).WithCertificate(certificate));
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var running = server.RunAsync(stop.Token);
            try
            {
                await server.DrainAsync(TimeSpan.FromSeconds(20)).WaitAsync(TimeSpan.FromSeconds(5));
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(server.State, Is.EqualTo(WebServerState.Stopped));
            }
            finally { stop.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
        }
    }
}
