using System;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed partial class Http3QuicTest
    {
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task FlowControlledResponseDoesNotBlockSiblingAndReleasesWriter(bool reset, bool singleWrite)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await ExerciseResponseBackpressure(reset, singleWrite);
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public async Task ReservedHttp3TrailersSurviveBackpressureOrReleaseOnReset(bool reset, bool singleWrite)
        {
            if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            { Assert.Ignore("The host does not provide QUIC."); return; }
            if (typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection") == null)
            { Assert.Ignore("The legacy asset has no direct QUIC transport."); return; }
            await ExerciseResponseBackpressure(reset, singleWrite, true);
        }
        private sealed class BackpressuredApplication
        {
            internal bool SingleWrite;
            internal bool Trailers;
            internal readonly byte[] Large = new byte[8 * 1024 * 1024];
            internal readonly byte[] Small = { 17, 31, 47, 63 };
            internal readonly TaskCompletionSource<Task> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal async Task Handle<T>(T value)
            {
                object exchange = value ?? throw new AssertionException("Missing exchange.");
                var type = exchange.GetType();
                var id = (long)(type.GetProperty("Id", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing ID."));
                var token = (CancellationToken)(type.GetProperty("CancellationToken", Flags)?.GetValue(exchange) ?? throw new AssertionException("Missing cancellation."));
                var write = WriteResponse(exchange, id == 0 ? Large : Small, token, SingleWrite, Trailers && id == 0);
                if (id == 0) Pending.TrySetResult(write);
                await write;
            }
            private static async Task WriteResponse(object exchange, byte[] bytes, CancellationToken token, bool singleWrite, bool trailers)
            {
                if (trailers)
                {
                    var contextType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.MultiplexedContext", true)
                        ?? throw new AssertionException("Missing response adapter.");
                    var context = (IHttpContextImpl)(Activator.CreateInstance(contextType, Flags, null,
                        new object[] { exchange, new IPEndPoint(IPAddress.Loopback, 80), new IPEndPoint(IPAddress.Loopback, 12345), true }, null)
                        ?? throw new AssertionException("Missing adapter context."));
                    Exception? writeFailure = null;
                    try
                    {
                        context.Response.ContentLength64 = bytes.Length;
                        var sections = context.Response as IHttpResponseSections
                            ?? throw new AssertionException("Missing response capability.");
                        sections.DeclareTrailers("x-finished");
                        var responseQuantum = singleWrite ? bytes.Length : 16384;
                        for (var offset = 0; offset < bytes.Length; offset += responseQuantum)
                            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(offset, Math.Min(responseQuantum, bytes.Length - offset)), token);
                        sections.SetTrailers(new WebHeaderCollection { ["x-finished"] = "yes" });
                        await context.Response.OutputStream.DisposeAsync();
                    }
                    catch (Exception error) { writeFailure = error; throw; }
                    finally
                    {
                        try { context.Close(); }
                        catch (Exception cleanup) when (writeFailure != null && cleanup is IOException or OperationCanceledException or InvalidOperationException)
                        { TestContext.Out.WriteLine("Response cleanup after failed write: " + cleanup.GetType().Name); }
                    }
                    return;
                }
                var type = exchange.GetType();
                var fieldType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http2.HpackField", true)
                    ?? throw new AssertionException("Missing field type.");
                var fields = Array.CreateInstance(fieldType, 1);
                fields.SetValue(Activator.CreateInstance(fieldType, Flags, null, new object[] { ":status", "200", false }, null), 0);
                await (Task)(type.GetMethod("SendHeadersAsync", Flags)?.Invoke(exchange,
                    new object[] { fields, false, token }) ?? throw new AssertionException("Missing headers."));
                var quantum = singleWrite ? bytes.Length : 16384;
                for (var offset = 0; offset < bytes.Length; offset += quantum)
                {
                    var count = Math.Min(quantum, bytes.Length - offset);
                    await (Task)(type.GetMethod("WriteAsync", Flags)?.Invoke(exchange,
                        new object[] { bytes, offset, count, offset + count == bytes.Length, token })
                        ?? throw new AssertionException("Missing DATA writer."));
                }
            }
        }
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task ExerciseResponseBackpressure(bool reset, bool singleWrite, bool trailers = false)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var application = new BackpressuredApplication { SingleWrite = singleWrite, Trailers = trailers };
            new Random(9218).NextBytes(application.Large);
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); req.CertificateExtensions.Add(san.Build());
            using var generated = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var cert = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
            Assert.That(cert.Export(X509ContentType.Pkcs12).Length, Is.GreaterThan(0), "The OpenSSL QUIC backend must be able to export its synthetic TLS key.");
            await using var listener = await QuicListener.ListenAsync(new QuicListenerOptions
            {
                ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
                ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundBidirectionalStreams = 32,
                    MaxInboundUnidirectionalStreams = 8,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions { ApplicationProtocols = new() { new SslApplicationProtocol("h3") }, ServerCertificate = cert }
                })
            }, deadline.Token);
            var assembly = typeof(WebServer).Assembly;
            var exchange = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicExchange", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var connection = assembly.GetType("EmbedIO.Net.Internal.Http3.Http3QuicConnection", true) ?? throw new AssertionException("Missing HTTP/3 test target.");
            var handler = (typeof(BackpressuredApplication).GetMethod(nameof(BackpressuredApplication.Handle), Flags) ?? throw new AssertionException("Missing handler."))
                .MakeGenericMethod(exchange).CreateDelegate(typeof(Func<,>).MakeGenericType(exchange, typeof(Task)), application);
            var sessionReady = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = Task.Run(async () =>
            {
                await using var accepted = await listener.AcceptConnectionAsync(deadline.Token);
                var session = Activator.CreateInstance(connection, Flags, null, new object[] { accepted, handler, deadline.Token }, null)
                    ?? throw new AssertionException("Missing connection owner.");
                using var owner = (IDisposable)session;
                sessionReady.SetResult(session);
                await ((Task)(connection.GetMethod("RunCoreAsync", Flags)?.Invoke(session, null) ?? throw new AssertionException("Missing runner.")));
            });
            await using var client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                InitialReceiveWindowSizes = new QuicReceiveWindowSizes
                {
                    Connection = 1024 * 1024,
                    LocallyInitiatedBidirectionalStream = 65536,
                    RemotelyInitiatedBidirectionalStream = 65536,
                    UnidirectionalStream = 65536
                },
                DefaultCloseErrorCode = 0x100,
                DefaultStreamErrorCode = 0x10c,
                MaxInboundUnidirectionalStreams = 8,
                MaxInboundBidirectionalStreams = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    ApplicationProtocols = new() { new SslApplicationProtocol("h3") },
                    RemoteCertificateValidationCallback = (_, peer, _, _) => peer?.GetCertHashString() == cert.GetCertHashString()
                }
            }, deadline.Token);
            await using var control = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, deadline.Token);
            await control.WriteAsync(new byte[] { 0, 4, 0 }, deadline.Token);
            try
            {
                await using var stalled = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                Assert.That(stalled.Id, Is.Zero);
                var request = Convert.FromHexString("01100000D1D7C150096C6F63616C686F7374");
                await stalled.WriteAsync(request, true, deadline.Token);
                var pending = await application.Pending.Task.WaitAsync(deadline.Token);
                if (pending.IsFaulted) await pending;
                // Observe actual response bytes before checking blockage. The unread
                // 8 MiB body exceeds the advertised 64 KiB stream receive window.
                var first = new byte[1];
                await stalled.ReadExactlyAsync(first, deadline.Token);
                Assert.That(first[0], Is.EqualTo(1));
                await using (var sibling = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token))
                {
                    await sibling.WriteAsync(request, true, deadline.Token);
                    Assert.That(await ReadResponseData(sibling, deadline.Token), Is.EqualTo(application.Small));
                }
                Assert.That(pending.IsCompleted, Is.False, "The large writer must remain backpressured while its sibling finishes.");
                if (reset)
                {
                    stalled.Abort(QuicAbortDirection.Read, 0x10c);
                    Exception? failure = null;
                    try { await pending.WaitAsync(deadline.Token); }
                    catch (Exception error) when (error is QuicException or OperationCanceledException) { failure = error; }
                    Assert.That(failure, Is.Not.Null, "STOP_SENDING must terminate the outstanding writer.");
                }
                else
                {
                    Assert.That(await ReadResponseData(stalled, deadline.Token, first, trailers), Is.EqualTo(application.Large));
                    await pending.WaitAsync(deadline.Token);
                }
                // A reset or fully drained response must leave the connection useful.
                await using var subsequent = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, deadline.Token);
                await subsequent.WriteAsync(request, true, deadline.Token);
                Assert.That(await ReadResponseData(subsequent, deadline.Token), Is.EqualTo(application.Small));
            }
            finally
            {
                deadline.Cancel();
                try { await server.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
            }
        }
        private static async Task<byte[]> ReadResponseData(Stream stream, CancellationToken token, byte[]? prefix = null, bool trailers = false)
        {
            using var wire = new MemoryStream();
            if (prefix != null) wire.Write(prefix);
            await stream.CopyToAsync(wire, token);
            var bytes = wire.ToArray();
            using var body = new MemoryStream();
            var offset = 0;
            var headers = false;
            var trailerReceived = false;
            while (offset < bytes.Length)
            {
                var type = ReadResponseInteger(bytes, ref offset);
                var length = ReadResponseInteger(bytes, ref offset);
                Assert.That(length, Is.LessThanOrEqualTo(bytes.Length - offset));
                if (type == 1)
                {
                    if (!headers) headers = true;
                    else
                    {
                        Assert.That(trailers, Is.True);
                        Assert.That(trailerReceived, Is.False);
                        Assert.That(body.Length, Is.EqualTo(8 * 1024 * 1024), "Ending fields must follow the entire body.");
                        var decoderType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.QpackDecoder", true)
                            ?? throw new AssertionException("Missing field decoder.");
                        using var decoder = (IDisposable)(Activator.CreateInstance(decoderType, Flags, null,
                            new object[] { 0, 0, 65536, 65536, 0L, 65536 }, null) ?? throw new AssertionException("Missing decoder."));
                        var payload = bytes.AsSpan(offset, checked((int)length)).ToArray();
                        var fields = (Array)(decoderType.GetMethod("Submit", Flags)?.Invoke(decoder, new object[] { 0L, payload })
                            ?? throw new AssertionException("Missing decoded fields."));
                        Assert.That(fields.Length, Is.EqualTo(1));
                        var field = fields.GetValue(0) ?? throw new AssertionException("Missing trailer field.");
                        Assert.That(field.GetType().GetProperty("Name")?.GetValue(field), Is.EqualTo("x-finished"));
                        Assert.That(field.GetType().GetProperty("Value")?.GetValue(field), Is.EqualTo("yes"));
                        trailerReceived = true;
                    }
                }
                else
                {
                    Assert.That(type, Is.Zero);
                    Assert.That(headers, Is.True);
                    Assert.That(trailerReceived, Is.False, "DATA cannot follow ending trailers.");
                    body.Write(bytes, offset, checked((int)length));
                }
                offset += checked((int)length);
            }
            Assert.That(headers, Is.True);
            Assert.That(trailerReceived, Is.EqualTo(trailers));
            return body.ToArray();
        }
        private static long ReadResponseInteger(byte[] bytes, ref int offset)
        {
            Assert.That(offset, Is.LessThan(bytes.Length));
            var size = 1 << (bytes[offset] >> 6);
            Assert.That(size, Is.LessThanOrEqualTo(bytes.Length - offset));
            long value = bytes[offset++] & 63;
            for (var index = 1; index < size; ++index) value = (value << 8) | bytes[offset++];
            return value;
        }
    }
}
