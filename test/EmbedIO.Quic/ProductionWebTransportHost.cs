using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EmbedIO.Actions;

namespace EmbedIO.Quic
{
    // An ordinary WebServer that serves the independent peer's WebTransport echo
    // protocol through the public API only. Streams echo incrementally: the peer's
    // unidirectional upload is answered on a server stream opened at its first byte.
    // The host stops when its standard input closes, then prints what it served.
    // Diagnostics go to standard error with the time since start.
    internal static class ProductionWebTransportHost
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        internal static async Task<int> RunAsync(string certificatePath, string keyPath)
        {
            using var pem = X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
            using var certificate = X509CertificateLoader.LoadPkcs12(
                pem.Export(X509ContentType.Pkcs12),
                null,
                X509KeyStorageFlags.Exportable
            );
            var authorization = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var port = FreePort();
            var counts = new Counts();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var options = new WebServerOptions()
                .WithUrlPrefix($"https://localhost:{port}/")
                .WithMode(HttpListenerMode.EmbedIOHttp3)
                .WithCertificate(certificate)
                .WithWebTransport(webTransport => webTransport.MaximumSessionsPerConnection = 2);
            using var server = new WebServer(options);
            server.WithModule(
                new ActionModule("/", HttpVerbs.Any, context => ServeAsync(context, authorization, counts))
            );
            var running = server.RunAsync(stop.Token);
            var ready = new
            {
                ready = true,
                port,
                authorization
            };
            Console.WriteLine(JsonSerializer.Serialize(ready));

            // The verifier closes standard input once the peer has finished.
            var input = Task.Run(() => Console.In.ReadToEnd(), CancellationToken.None);
            await Task
                .WhenAny(input, Task.Delay(Timeout.Infinite, stop.Token))
                .ConfigureAwait(false);
            Log("stopping");
            await stop
                .CancelAsync()
                .ConfigureAwait(false);

            try
            {
                await running.ConfigureAwait(false);
            }

            catch (OperationCanceledException) { }

            Console.WriteLine(JsonSerializer.Serialize(counts.Report()));
            return 0;
        }

        // Base routes end with a slash, so the peer's /echo is matched here.
        private static async Task ServeAsync(IHttpContext context, string authorization, Counts counts)
        {
            var webTransport = context as IWebTransportContext;
            Log(
                $"request {context.Request.HttpMethod} {context.RequestedPath} webtransport={webTransport?.IsWebTransportRequest}"
            );

            if (context.RequestedPath != "/echo") throw HttpException.NotFound();

            if (webTransport is not { IsWebTransportRequest: true })
                throw HttpException.BadRequest("Expected a WebTransport session request.");

            if (
                context.Request.Headers["authorization"] != "Bearer " + authorization
                || context.Request.Headers["origin"] != "https://localhost"
            )
            {
                Interlocked.Increment(ref counts.Refused);
                throw HttpException.Forbidden();
            }

            var token = context.CancellationToken;
            WebTransportSession session;
            try
            {
                session = await webTransport
                    .AcceptWebTransportAsync(token)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // The connection can end while the 200 is still being sent.
                Log($"session accept failed: {error.GetType().Name}");
                throw;
            }

            Interlocked.Increment(ref counts.Accepted);
            Log(
                $"session accepted: datagrams can send={session.Datagrams.CanSend} limit={session.Datagrams.MaximumPayloadLength}"
            );
            var datagrams = EchoDatagramsAsync(session, counts, token);
            var streams = new ConcurrentBag<Task>();

            while (
                await session
                    .AcceptStreamAsync(token)
                    .ConfigureAwait(false) is { } stream
            )
                streams.Add(EchoStreamAsync(session, stream, counts, token));

            await session.Completion.ConfigureAwait(false);
            await Task
                .WhenAll(streams.Append(datagrams))
                .ConfigureAwait(false);
            Log($"session ended: close code {session.CloseCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
            counts.Ended(session.CloseCode);
        }

        private static async Task EchoDatagramsAsync(
            WebTransportSession session,
            Counts counts,
            CancellationToken token
        )
        {
            try
            {
                while (
                    await session.Datagrams
                        .ReceiveAsync(token)
                        .ConfigureAwait(false) is { } datagram
                )
                    using (datagram)
                    {
                        var status = session.Datagrams.TrySend(datagram.Payload);
                        Log($"datagram of {datagram.Payload.Length} bytes echoed: {status}");

                        if (status == HttpDatagramSendStatus.Queued) Interlocked.Increment(ref counts.Datagrams);
                    }

                Log("datagram receive ended");
            }

            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }

        private static async Task EchoStreamAsync(
            WebTransportSession session,
            WebTransportStream stream,
            Counts counts,
            CancellationToken token
        )
        {
            WebTransportStream? output = stream.IsBidirectional ? stream : null;
            var buffer = new byte[16384];
            long total = 0;

            try
            {
                while (true)
                {
                    var count = await stream
                        .ReadAsync(buffer, token)
                        .ConfigureAwait(false);

                    if (count == 0) break;

                    total += count;
                    output ??= await session
                        .OpenStreamAsync(false, token)
                        .ConfigureAwait(false);
                    await output
                        .WriteAsync(buffer.AsMemory(0, count), token)
                        .ConfigureAwait(false);
                }

                if (output != null)
                {
                    await output
                        .CompleteWritesAsync(token)
                        .ConfigureAwait(false);

                    if (stream.IsBidirectional) Interlocked.Increment(ref counts.Bidirectional);

                    else Interlocked.Increment(ref counts.Unidirectional);
                }

                Log($"{(stream.IsBidirectional ? "bidirectional" : "unidirectional")} stream echoed {total} bytes");
            }

            catch (Exception error) when (
                error is IOException
                    or InvalidOperationException
                    or OperationCanceledException
            )
            {
                Interlocked.Increment(ref counts.StreamErrors);
                Log($"stream ended with {error.GetType().Name} after {total} bytes: {error.Message}");
            }

            finally
            {
                if (output != null && !ReferenceEquals(output, stream))
                    await output
                        .DisposeAsync()
                        .ConfigureAwait(false);

                await stream
                    .DisposeAsync()
                    .ConfigureAwait(false);
            }
        }

        private static void Log(string message) =>
            Console.Error.WriteLine(
                $"{Clock.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms {message}"
            );

        private static int FreePort()
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)(socket.LocalEndPoint
                ?? throw new InvalidOperationException("UDP endpoint unavailable."))).Port;
        }

        private sealed class Counts
        {
            internal int Accepted;
            internal int Refused;
            internal int Bidirectional;
            internal int Unidirectional;
            internal int Datagrams;
            internal int StreamErrors;
            private readonly ConcurrentQueue<string> _closes = new();

            internal void Ended(uint? closeCode) =>
                _closes.Enqueue(closeCode?.ToString(CultureInfo.InvariantCulture) ?? "abrupt");

            internal object Report() => new
            {
                acceptedSessions = Volatile.Read(ref Accepted),
                refusedSessions = Volatile.Read(ref Refused),
                bidirectionalEchoes = Volatile.Read(ref Bidirectional),
                unidirectionalEchoes = Volatile.Read(ref Unidirectional),
                datagramsEchoed = Volatile.Read(ref Datagrams),
                streamErrors = Volatile.Read(ref StreamErrors),
                sessionEnds = _closes.ToArray()
            };
        }
    }
}
