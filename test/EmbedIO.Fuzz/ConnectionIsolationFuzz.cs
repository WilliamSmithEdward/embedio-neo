using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

// Three separate handlers establish three physical connections, not merely three
// HTTP/2 streams. Application gates hold accepted requests concurrently so the
// trace controls release/cancellation order without relying on timing sleeps.
internal static class ConnectionIsolationFuzz
{
    internal static Task Http1Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 1, token);
    internal static Task Http2Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 2, token);
    internal static Task Http3Async(StateMachineTrace trace, CancellationToken token) => RunAsync(trace, 3, token);

    private static async Task RunAsync(StateMachineTrace trace, int protocol, CancellationToken token)
    {
        if (protocol == 3 && !QuicConnection.IsSupported) throw new PlatformNotSupportedException("The explicit HTTP/3 campaign requires QUIC support.");
        using var certificate = protocol == 3 ? Certificate() : null;
        var url = Prefix(protocol == 3);
        var requests = new ConcurrentDictionary<string, Request>();
        var completed = new ConcurrentBag<Request>();
        var faults = new ConcurrentQueue<Exception>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var server = new WebServer(options =>
        {
            options.WithUrlPrefix(url).WithMode(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO);
            if (certificate != null) options.WithCertificate(certificate);
        }).WithModule(new ActionModule("/", HttpVerbs.Post, async context =>
        {
            var id = context.Request.Headers["X-Fuzz-Request"];
            if (id == null || !requests.TryGetValue(id, out var request)) throw new StateMachineFailure("network.request-identity", "Unexpected request was dispatched.");
            context.OnClose(_ =>
            {
                Interlocked.Increment(ref request.CloseCount);
                request.Closed.TrySetResult();
            });
            try
            {
                using var body = context.OpenRequestStream();
                using var bytes = new MemoryStream();
                await body.CopyToAsync(bytes, context.CancellationToken).ConfigureAwait(false);
                if (!bytes.ToArray().AsSpan().SequenceEqual(request.Payload))
                    throw new StateMachineFailure("network.body-isolation", id);
                request.RemotePort = context.RemoteEndPoint.Port;
                request.Entered.TrySetResult();
                await request.Release.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                // Cancellation is a commanded transition after entry. The model
                // expects cleanup, not an application write to an aborted stream.
                if (request.Canceled) { trace.Cover("network.callback-cancellation-observed"); return; }
                await context.SendStringAsync(request.Id + ":" + Convert.ToHexString(request.Payload), "text/plain", WebServer.Utf8NoBomEncoding).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A canceled client may close its transport during response writing.
                // Oracle failures are always retained; unrelated I/O errors are too.
                if (!(request.Canceled && error is IOException or OperationCanceledException or ObjectDisposedException)) faults.Enqueue(error);
                request.Entered.TrySetException(error);
                throw;
            }
        }));
        var running = server.RunAsync(stop.Token);
        var clients = Enumerable.Range(0, 3).Select(_ => Client(protocol, certificate)).ToArray();
        var pending = new Request?[3];
        var sequence = 0;
        try
        {
            // Mandatory overlap makes even a shrunk/empty trace a multi-connection case.
            for (var actor = 0; actor < 3; actor++) await StartAsync(actor, actor + 1).ConfigureAwait(false);
            VerifyConnections();
            trace.Cover("network.three-physical-connections");
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = step.Actor;
                switch (step.Command % 6)
                {
                    case 0: await StartAsync(actor, step.Value).ConfigureAwait(false); break;
                    case 1: await FinishAsync(actor, false).ConfigureAwait(false); break;
                    case 2: await FinishAsync(actor, true).ConfigureAwait(false); break;
                    case 3:
                        if (pending[actor] == null)
                        {
                            clients[actor].Dispose();
                            clients[actor] = Client(protocol, certificate);
                            trace.Cover("network.client-replacement");
                        }
                        break;
                    case 4: await StartAsync((actor + 1) % 3, step.Value).ConfigureAwait(false); break;
                    case 5:
                        await Task.WhenAll(FinishAsync(actor, false), FinishAsync((actor + 1) % 3, step.Value % 2 == 0)).ConfigureAwait(false);
                        trace.Cover("network.overlapping-completion");
                        break;
                }
                VerifyConnections();
                CheckFaults();
                foreach (var held in pending)
                    if (held != null && held.Response.IsCompleted)
                        throw new StateMachineFailure("network.held-peer-survives", held.Id);
            }
            // Drain with three accepted, held requests and two concurrent callers.
            for (var actor = 0; actor < 3; actor++) await StartAsync(actor, 17 + actor).ConfigureAwait(false);
            var drain = server.DrainAsync(TimeSpan.FromSeconds(5), token);
            var secondDrain = server.DrainAsync(TimeSpan.FromSeconds(5), token);
            if (drain.IsCompleted) throw new StateMachineFailure("network.drain-waits", "Accepted requests are still held.");
            await Task.WhenAll(Enumerable.Range(0, 3).Select(actor => FinishAsync(actor, false))).ConfigureAwait(false);
            await Task.WhenAll(drain, secondDrain, running).WaitAsync(token).ConfigureAwait(false);
            if (server.State != WebServerState.Stopped) throw new StateMachineFailure("network.stopped", server.State.ToString());
            CheckFaults();
            if (completed.Any(request => request.CloseCount != 1)) throw new StateMachineFailure("network.close-once", "Request cleanup was not exactly once.");
            trace.Cover("network.drain-complete");
            // The drained endpoint is free at once: a new listener binds it.
            using (var replacement = new WebServer(options =>
            {
                options.WithUrlPrefix(url).WithMode(protocol == 3 ? HttpListenerMode.EmbedIOHttp3 : HttpListenerMode.EmbedIO);
                if (certificate != null) options.WithCertificate(certificate);
            }))
            {
                replacement.Listener.Start();
                if (!replacement.Listener.IsListening) throw new StateMachineFailure("network.rebind", url);
                trace.Cover("network.endpoint-rebound");
            }
        }
        finally
        {
            foreach (var request in requests.Values) request.Release.TrySetResult();
            // All owners start retiring before any failure is observed. WhenAll
            // waits for every owner even if another cleanup task faults. The
            // campaign watchdog supplies the deadline; no background task is
            // detached by a shorter local cleanup timeout.
            var cleanup = new List<Task>();
            foreach (var client in clients) cleanup.Add(Task.Run(client.Dispose));
            cleanup.Add(stop.CancelAsync());
            cleanup.Add(ObserveServerAsync());
            foreach (var request in requests.Values) cleanup.Add(DisposeRequestAsync(request));
            await Task.WhenAll(cleanup).ConfigureAwait(false);
        }

        async Task ObserveServerAsync()
        {
            try { await running.ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        static async Task DisposeRequestAsync(Request request)
        {
            try
            {
                if (request.PendingResponse is { } pendingResponse)
                {
                    using var response = await pendingResponse.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
            finally { request.Dispose(); }
        }
        async Task StartAsync(int actor, int value)
        {
            if (pending[actor] != null) { trace.Cover("network.start-held-noop"); return; }
            var id = trace.Seed + "-" + actor + "-" + sequence++;
            var payload = Encoding.UTF8.GetBytes(id + ":" + new string((char)('a' + actor), value + 1));
            var request = new Request(id, payload, token);
            requests[id] = request;
            pending[actor] = request;
            request.Message.RequestUri = new Uri(url);
            request.Message.Version = Version(protocol);
            request.Response = clients[actor].SendAsync(request.Message, request.Cancellation.Token);
            var entered = await Task.WhenAny(request.Entered.Task, request.Response, running).WaitAsync(token).ConfigureAwait(false);
            if (entered == request.Response)
            {
                using var premature = await request.Response.ConfigureAwait(false);
                throw new StateMachineFailure("network.response-before-entry", request.Id + " returned " + premature.StatusCode);
            }
            if (entered == running)
            {
                await running.ConfigureAwait(false);
                throw new StateMachineFailure("network.server-stopped-before-entry", request.Id);
            }
            await request.Entered.Task.WaitAsync(token).ConfigureAwait(false);
            trace.Cover("network.request-held");
        }

        async Task FinishAsync(int actor, bool cancel)
        {
            var request = pending[actor];
            if (request == null) return;
            request.Canceled = cancel;
            if (cancel) await request.Cancellation.CancelAsync().ConfigureAwait(false);
            request.Release.TrySetResult();
            try
            {
                using var response = await request.Response.WaitAsync(token).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK || body != request.Id + ":" + Convert.ToHexString(request.Payload))
                    throw new StateMachineFailure("network.response-isolation", request.Id + " received " + response.StatusCode + ":" + body);
                if (response.Version.Major != protocol) throw new StateMachineFailure("network.protocol", response.Version.ToString());
            }
            catch (OperationCanceledException) when (cancel && !token.IsCancellationRequested) { }
            catch (HttpRequestException) when (cancel && !token.IsCancellationRequested) { }
            await request.Closed.Task.WaitAsync(token).ConfigureAwait(false);
            if (request.CloseCount != 1) throw new StateMachineFailure("network.close-once", request.Id);
            completed.Add(request);
            pending[actor] = null;
            trace.Cover(cancel ? "network.request-canceled" : "network.request-completed");
        }

        void VerifyConnections()
        {
            var active = pending.Where(request => request != null).Select(request => request?.RemotePort ?? 0).ToArray();
            if (active.Any(port => port == 0) || (protocol != 3 && active.Distinct().Count() != active.Length))
                throw new StateMachineFailure("network.distinct-connections", "Actors share a transport endpoint.");
        }

        void CheckFaults()
        {
            if (faults.TryDequeue(out var error)) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    private static Version Version(int protocol) => protocol == 3 ? HttpVersion.Version30 : protocol == 2 ? HttpVersion.Version20 : HttpVersion.Version11;

    private static HttpClient Client(int protocol, X509Certificate2? certificate)
    {
        var handler = new SocketsHttpHandler { UseProxy = false, MaxConnectionsPerServer = 1 };
        if (certificate != null)
            handler.SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == certificate.GetCertHashString() };
        return new HttpClient(handler) { DefaultRequestVersion = Version(protocol), DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact, Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static string Prefix(bool quic)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, quic ? SocketType.Dgram : SocketType.Stream, quic ? ProtocolType.Udp : ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)(socket.LocalEndPoint ?? throw new InvalidOperationException("No local endpoint."))).Port;
        return (quic ? "https" : "http") + "://127.0.0.1:" + port + "/";
    }

    internal static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
    }

    private sealed class Request : IDisposable
    {
        private Task<HttpResponseMessage>? _response;
        internal Request(string id, byte[] payload, CancellationToken token)
        {
            Id = id;
            Payload = payload;
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Message = new HttpRequestMessage(HttpMethod.Post, (Uri?)null)
            { Content = new ByteArrayContent(payload), VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            Message.Headers.Add("X-Fuzz-Request", id);
        }

        internal string Id { get; }
        internal byte[] Payload { get; }
        internal HttpRequestMessage Message { get; }
        internal CancellationTokenSource Cancellation { get; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<HttpResponseMessage> Response { get => _response ?? throw new InvalidOperationException("Request not sent."); set => _response = value; }
        internal Task<HttpResponseMessage>? PendingResponse => _response;
        internal int RemotePort { get; set; }
        internal volatile bool Canceled;
        internal int CloseCount;
        public void Dispose() { Message.Dispose(); Cancellation.Dispose(); }
    }
}
