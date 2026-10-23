using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    // A WebTransport session needs QUIC datagrams and reliable stream reset, which the
    // .NET QUIC client never negotiates. These cases cover what a server with WebTransport
    // enabled does for every other peer and request; test/EmbedIO.Quic drives sessions
    // with an independent quic-go peer.
    public partial class Http3ListenerTest
    {
        private const long WebTransportEnabledSetting = 0x2c7cf000;
        private const long WebTransportDraft02Setting = 0x2b603742;
        private const long WebTransportDraft14MaxSessionsSetting = 0x14e9cd29;
        private const long WebTransportInitialMaxDataSetting = 0x2b61;
        private const long WebTransportInitialUnidirectionalSetting = 0x2b64;
        private const long WebTransportInitialBidirectionalSetting = 0x2b65;
        private const long H3MessageError = 0x10e;
        private const long WebTransportBufferedStreamRejected = 0x3994bd84;

        [TestCase(false, false, false, 0, 1)]
        [TestCase(true, true, true, 100, 1)]
        [TestCase(true, false, true, 7, 3)]
        [TestCase(true, true, false, 7, 3)]
        public async Task TheServerAdvertisesWebTransportOnlyWhenItIsEnabled(
            bool enabled,
            bool draft02,
            bool draft14,
            int streams,
            int sessions
        )
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            using var certificate = Certificate();
            var prefix = Prefix();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Action<WebTransportOptions>? configure = null;

            if (enabled)
                configure = webTransport =>
                {
                    webTransport.AcceptBrowserDraft02 = draft02;
                    webTransport.AcceptBrowserDraft14 = draft14;
                    webTransport.MaximumIncomingStreamsPerSession = streams;
                    webTransport.MaximumSessionsPerConnection = sessions;
                };

            using var server = WebTransportServer(prefix, certificate, configure, _ => Task.CompletedTask);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(new Uri(prefix), certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                var settings = await ReadServerSettingsAsync(connection, peers, stop.Token);
                long Value(long id) => settings.TryGetValue(id, out var value) ? value : 0;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(Value(WebTransportEnabledSetting), Is.EqualTo(enabled ? 1 : 0));
                    Assert.That(Value(WebTransportDraft02Setting), Is.EqualTo(enabled && draft02 ? 1 : 0));
                    // Draft-14's SETTINGS_WT_MAX_SESSIONS carries the session limit.
                    Assert.That(
                        Value(WebTransportDraft14MaxSessionsSetting),
                        Is.EqualTo(enabled && draft14 ? sessions : 0)
                    );
                    Assert.That(Value(WebTransportInitialUnidirectionalSetting), Is.EqualTo(streams));
                    Assert.That(Value(WebTransportInitialBidirectionalSetting), Is.EqualTo(streams));
                    Assert.That(
                        Value(WebTransportInitialMaxDataSetting) > 0,
                        Is.EqualTo(enabled),
                        "session flow control"
                    );

                    if (enabled)
                    {
                        // Section 3.1: the settings WebTransport depends on.
                        Assert.That(Value(8), Is.EqualTo(1), "extended CONNECT");
                        Assert.That(Value(0x33), Is.EqualTo(1), "HTTP datagrams");
                    }
                }
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        public static IEnumerable<TestCaseData> UnnegotiatedWebTransportCases()
        {
            foreach (var profile in new[] { "plain", "draft-16", "draft-02", "draft-14" })
                foreach (var protocol in new[] { "webtransport-h3", "webtransport" })
                    yield return new TestCaseData(profile, protocol);
        }

        // Draft-16 section 3.1: requests on a connection whose SETTINGS or transport
        // parameters miss a requirement are malformed (RFC 9114 section 4.1.2).
        [TestCaseSource(nameof(UnnegotiatedWebTransportCases))]
        public async Task AWebTransportRequestTheConnectionDidNotNegotiateIsMalformed(string profile, string protocol)
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var calls = 0;
            using var server = WebTransportServer(
                prefix,
                certificate,
                _ => { },
                _ =>
                {
                    Interlocked.Increment(ref calls);
                    return Task.CompletedTask;
                }
            );
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, ClientProfile(profile), stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                );
                (string, string)[] fields =
                [
                    (":method", "CONNECT"),
                    (":scheme", "https"),
                    (":authority", uri.Authority),
                    (":path", "/session"),
                    (":protocol", protocol)
                ];
                // Leave the request open: only the server may end it.
                await SendFieldSection(request, fields, false, stop.Token);
                Assert.That(await ObserveRequestAsync(request, stop.Token), Is.EqualTo(Reset(H3MessageError)));
                await HealthySibling(connection, uri.Authority, stop.Token);
                Assert.That(calls, Is.Zero, "A malformed session request must not reach the application.");
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        // :protocol webtransport names draft-02 or draft-14. With draft-02 off it is an ordinary
        // request unless the client declared draft-14 and the server accepts it.
        [TestCase(false, false, "draft-02")]
        [TestCase(false, false, "draft-14")]
        [TestCase(false, true, "draft-02")]
        [TestCase(false, true, "plain")]
        public async Task TheBrowserDraftTokenIsAnOrdinaryRequestWhenTheDraftIsOff(
            bool draft02,
            bool draft14,
            string profile
        )
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var calls = 0;
            bool? webTransportRequest = null;
            using var server = WebTransportServer(
                prefix,
                certificate,
                webTransport =>
                {
                    webTransport.AcceptBrowserDraft02 = draft02;
                    webTransport.AcceptBrowserDraft14 = draft14;
                },
                async context =>
                {
                    Interlocked.Increment(ref calls);
                    webTransportRequest = (context as IWebTransportContext)?.IsWebTransportRequest;
                    context.Response.StatusCode = 404;
                    await context.Response.OutputStream.WriteAsync("no-session"u8.ToArray(), context.CancellationToken);
                }
            );
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(
                    connection,
                    ClientProfile(profile),
                    stop.Token
                );
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);
                await using var request = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                );
                (string, string)[] fields =
                [
                    (":method", "CONNECT"),
                    (":scheme", "https"),
                    (":authority", uri.Authority),
                    (":path", "/session"),
                    (":protocol", "webtransport")
                ];
                await SendFieldSection(request, fields, false, stop.Token);
                Assert.That(await ObserveRequestAsync(request, stop.Token), Is.EqualTo("404 no-session"));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(calls, Is.EqualTo(1));
                    Assert.That(webTransportRequest, Is.False);
                }
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        [Test]
        public async Task OrdinaryRequestsAreServedBesideWebTransport()
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            WebTransportOptions? settings = null;
            var webTransportRequests = 0;
            var refusedAcceptances = 0;
            var otherContexts = 0;
            using var server = WebTransportServer(
                prefix,
                certificate,
                webTransport => settings = webTransport,
                async context =>
                {
                    if (context is not IWebTransportContext webTransport)
                    {
                        Interlocked.Increment(ref otherContexts);
                        return;
                    }

                    if (webTransport.IsWebTransportRequest) Interlocked.Increment(ref webTransportRequests);

                    try
                    {
                        _ = await webTransport.AcceptWebTransportAsync(context.CancellationToken);
                    }

                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref refusedAcceptances);
                    }

                    using var body = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(body, context.CancellationToken);
                    context.Response.ContentType = "application/octet-stream";
                    await context.Response.OutputStream.WriteAsync(body.ToArray(), context.CancellationToken);
                }
            );
            var running = server.RunAsync(stop.Token);

            try
            {
                using var client = Client(certificate);
                var payload = RandomNumberGenerator.GetBytes(240_000);
                const int count = 12;
                // Bodies from empty to 220,000 bytes, sent at once on one connection.
                var requests = Enumerable
                    .Range(0, count)
                    .Select(async index =>
                    {
                        var content = payload
                            .AsSpan(0, index * 20_000)
                            .ToArray();
                        using var request = Request(HttpMethod.Post, prefix + "echo/" + index, new ByteArrayContent(content));
                        using var response = await client.SendAsync(request, stop.Token);
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                        Assert.That(await response.Content.ReadAsByteArrayAsync(stop.Token), Is.EqualTo(content));
                    });
                await Task.WhenAll(requests);
                Assert.That(await client.GetStringAsync(prefix + "healthy", stop.Token), Is.EqualTo("healthy"));

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(webTransportRequests, Is.Zero);
                    Assert.That(refusedAcceptances, Is.EqualTo(count), "Accepting an ordinary request must fail.");
                    Assert.That(otherContexts, Is.Zero, "HTTP/3 contexts implement IWebTransportContext.");
                    Assert.Throws<InvalidOperationException>(
                        () =>
                            (settings ?? throw new AssertionException("Missing settings.")).AcceptBrowserDraft02 = false
                    );
                }
            }

            finally
            {
                stop.Cancel();
                await running.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        // With WebTransport enabled the server reads the first integer of every request
        // stream to recognize the 0x41 signal, then replays it to the request reader.
        // Whatever the stream holds, the request must end exactly as it does without.
        [Test]
        public async Task RequestStreamsEndTheSameWithWebTransportEnabled()
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            var plain = await ObserveRequestShapesAsync(false);
            var enabled = await ObserveRequestShapesAsync(true);
            static string Describe(Dictionary<string, string> outcomes) =>
                string.Join(
                    "; ",
                    outcomes
                        .OrderBy(outcome => outcome.Key, StringComparer.Ordinal)
                        .Select(outcome => outcome.Key + ": " + outcome.Value)
                );

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Describe(enabled), Is.EqualTo(Describe(plain)));
                Assert.That(plain["reserved frame split before HEADERS"], Is.EqualTo("200 healthy"));
                Assert.That(plain["no bytes"], Does.StartWith("reset "));
                Assert.That(plain["half a frame type"], Is.EqualTo("connection 0x106"), "H3_FRAME_ERROR");
            }
        }

        // The connection records which request streams can no longer name a session, in a
        // bounded list of ranges. A request that fails before it is classified must not
        // leave a hole in that record: alternating failures and successes would otherwise
        // fragment it until the connection is closed as excessive load.
        [Test]
        public async Task RequestsThatFailBeforeClassificationKeepTheConnectionUsable()
        {
            if (!QuicConnection.IsSupported) { Assert.Ignore("QUIC unavailable."); return; }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var server = WebTransportServer(prefix, certificate, _ => { }, _ => Task.CompletedTask);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);

                // More rounds than the record holds ranges. Even rounds send an empty request,
                // which fails while its headers are read; odd rounds reset the stream inside its
                // first integer, before a request can be told from a session stream.
                for (var round = 0; round < 300; round++)
                {
                    await using (var failed = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, stop.Token))
                    {
                        if (round % 2 == 0)
                        {
                            await failed.WriteAsync(ReadOnlyMemory<byte>.Empty, true, stop.Token);
                            Assert.That(await ObserveRequestAsync(failed, stop.Token), Is.EqualTo(Reset(0x10d)), $"round {round}");
                        }

                        else
                        {
                            // 0x40 begins a two-byte variable-length integer.
                            await failed.WriteAsync((byte[])[0x40], stop.Token);
                            failed.Abort(QuicAbortDirection.Write, 0x10c);
                        }
                    }

                    try
                    {
                        await HealthySibling(connection, uri.Authority, stop.Token);
                    }

                    catch (QuicException error)
                    {
                        Assert.Fail($"round {round}: {error.Message}");
                    }
                }
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        // Section 4.6 buffers streams that name a session not seen yet. A connection whose
        // SETTINGS lack WebTransport will never have one, so its streams are refused.
        [Test]
        public async Task WebTransportStreamsOnAConnectionWithoutWebTransportAreRefused()
        {
            if (!QuicConnection.IsSupported)
            {
                Assert.Ignore("QUIC unavailable.");
                return;
            }

            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var server = WebTransportServer(
                prefix,
                certificate,
                _ => { },
                _ => Task.CompletedTask
            );
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, ClientProfile("plain"), stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);
                // Both name session 8, a request stream this client has not opened.
                await using var bidirectional = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                );
                await bidirectional.WriteAsync(Integers(0x41, 8), false, stop.Token);
                await using var unidirectional = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional,
                    stop.Token
                );
                await unidirectional.WriteAsync(Integers(0x54, 8), false, stop.Token);
                var writesClosed = unidirectional.WritesClosed;
                var stopped = await Assert.CatchAsync<QuicException>(
                    async () => await writesClosed.WaitAsync(stop.Token)
                );

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(
                        await ObserveRequestAsync(bidirectional, stop.Token),
                        Is.EqualTo(Reset(WebTransportBufferedStreamRejected))
                    );
                    Assert.That(stopped?.QuicError, Is.EqualTo(QuicError.StreamAborted));
                    Assert.That(stopped?.ApplicationErrorCode, Is.EqualTo(WebTransportBufferedStreamRejected));
                }

                await HealthySibling(connection, uri.Authority, stop.Token);
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<Dictionary<string, string>> ObserveRequestShapesAsync(bool webTransport)
        {
            using var certificate = Certificate();
            var prefix = Prefix();
            var uri = new Uri(prefix);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Action<WebTransportOptions>? configure = null;

            if (webTransport)
                configure = _ => { };

            using var server = WebTransportServer(prefix, certificate, configure, _ => Task.CompletedTask);
            var running = server.RunAsync(stop.Token);
            var peers = new List<QuicStream>();
            var outcomes = new Dictionary<string, string>();

            try
            {
                await using var connection = await ConnectRawAsync(uri, certificate, stop.Token);
                await using var control = await SendClientSettingsAsync(connection, [], stop.Token);
                _ = await ReadServerSettingsAsync(connection, peers, stop.Token);

                await using (var request = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                ))
                {
                    // A reserved frame type (RFC 9114 section 7.2.8) whose two-byte integer arrives in two parts.
                    await request.WriteAsync(new byte[] { 0x40 }, false, stop.Token);
                    await Task.Delay(100, stop.Token);
                    await request.WriteAsync(new byte[] { 0x5f, 0x00 }, false, stop.Token);
                    (string, string)[] fields =
                    [
                        (":method", "GET"),
                        (":scheme", "https"),
                        (":authority", uri.Authority),
                        (":path", "/healthy")
                    ];
                    await SendFieldSection(request, fields, true, stop.Token);
                    outcomes["reserved frame split before HEADERS"] = await ObserveRequestAsync(request, stop.Token);
                }

                await using (var request = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                ))
                {
                    await request.WriteAsync(ReadOnlyMemory<byte>.Empty, true, stop.Token);
                    outcomes["no bytes"] = await ObserveRequestAsync(request, stop.Token);
                }

                await HealthySibling(connection, uri.Authority, stop.Token);

                // RFC 9114 section 7.1: a truncated last frame ends the connection, so this goes
                // last. The stream's own reset can overtake the close, so watch the connection.
                await using (var request = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Bidirectional,
                    stop.Token
                ))
                {
                    await request.WriteAsync(new byte[] { 0x40 }, true, stop.Token);
                    outcomes["half a frame type"] = await ObserveConnectionAsync(connection, peers, stop.Token);
                }
            }

            finally
            {
                await StopAsync(stop, running, peers);
            }

            return outcomes;
        }

        private static WebServer WebTransportServer(
            string prefix,
            X509Certificate2 certificate,
            Action<WebTransportOptions>? configure,
            Func<IHttpContext, Task> handler
        ) => new WebServer(options =>
            {
                options
                    .WithUrlPrefix(prefix)
                    .WithMode(HttpListenerMode.EmbedIOHttp3)
                    .WithCertificate(certificate);

                if (configure != null) options.WithWebTransport(configure);
            }).WithAction("/", HttpVerbs.Any, async context =>
            {
                if (context.Request.Url.AbsolutePath == "/healthy")
                {
                    await context.SendStringAsync("healthy", "text/plain", WebServer.Utf8NoBomEncoding);
                    return;
                }

                await handler(context);
            });

        // The client's SETTINGS pairs: none of WebTransport's, draft-16's, Chromium's draft-02 or
        // Safari's draft-14, which also sends draft-07's max-sessions setting and flow-control limits.
        private static long[] ClientProfile(string profile) => profile switch
        {
            "plain" => [],
            "draft-16" => [0x33, 1, WebTransportEnabledSetting, 1],
            "draft-02" => [0x33, 1, WebTransportDraft02Setting, 1],
            "draft-14" =>
            [
                0x33, 1, 0xc671706a, 1, WebTransportDraft14MaxSessionsSetting, 1,
                WebTransportInitialMaxDataSetting, 8388608, WebTransportInitialUnidirectionalSetting, 100,
                WebTransportInitialBidirectionalSetting, 100
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        private static string Reset(long code) => "reset 0x" + code.ToString("x", CultureInfo.InvariantCulture);

        private static byte[] Integers(params long[] values)
        {
            using var output = new MemoryStream();

            foreach (var value in values) WriteInteger(output, value);

            return output.ToArray();
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<QuicConnection> ConnectRawAsync(
            Uri uri,
            X509Certificate2 certificate,
            CancellationToken token
        ) =>
            await QuicConnection.ConnectAsync(
                new QuicClientConnectionOptions
                {
                    RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, uri.Port),
                    DefaultCloseErrorCode = 0x100,
                    DefaultStreamErrorCode = 0x10c,
                    MaxInboundUnidirectionalStreams = 8,
                    MaxInboundBidirectionalStreams = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        ApplicationProtocols = [new SslApplicationProtocol("h3")],
                        RemoteCertificateValidationCallback = (_, peer, _, errors) => peer != null
                            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                            && peer.GetCertHashString() == certificate.GetCertHashString()
                    }
                },
                token
            );

        // Opens the client's control stream with one SETTINGS frame. Keep it open:
        // closing a critical stream is a connection error.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<QuicStream> SendClientSettingsAsync(
            QuicConnection connection,
            long[] pairs,
            CancellationToken token
        )
        {
            var payload = Integers(pairs);
            using var frame = new MemoryStream();
            WriteInteger(frame, 0);
            WriteInteger(frame, 4);
            WriteInteger(frame, payload.Length);
            frame.Write(payload);
            var control = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token);
            await control.WriteAsync(frame.ToArray(), token);
            return control;
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<Dictionary<long, long>> ReadServerSettingsAsync(
            QuicConnection connection,
            List<QuicStream> peers,
            CancellationToken token
        )
        {
            while (true)
            {
                var stream = await connection.AcceptInboundStreamAsync(token);
                peers.Add(stream);

                if (await ReadInteger(stream, token) != 0) continue;

                Assert.That(
                    await ReadInteger(stream, token),
                    Is.EqualTo(4),
                    "The control stream starts with SETTINGS."
                );
                var size = await ReadInteger(stream, token);
                Assert.That(size, Is.InRange(0, 65536));
                var payload = new byte[(int)size];
                await stream.ReadExactlyAsync(payload, token);
                using var input = new MemoryStream(payload);
                var settings = new Dictionary<long, long>();

                while (input.Position < input.Length)
                {
                    var id = await ReadInteger(input, token);
                    settings[id] = await ReadInteger(input, token);
                }

                return settings;
            }
        }

        // How the server ended a request stream: its status and body, or the reset code.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<string> ObserveRequestAsync(QuicStream stream, CancellationToken token)
        {
            try
            {
                var fields = await ReadResponseFields(stream, token);
                using var body = new ClientDataStream(stream);
                using var received = new MemoryStream();
                await body.CopyToAsync(received, token);
                return fields[":status"] + " " + Encoding.UTF8.GetString(received.ToArray());
            }

            catch (QuicException error) when (error.QuicError == QuicError.StreamAborted)
            {
                return Reset(error.ApplicationErrorCode ?? -1);
            }

            catch (QuicException error) when (error.QuicError == QuicError.ConnectionAborted)
            {
                return Closed(error);
            }
        }

        // How the server ended the connection. Its QPACK streams may still be waiting to be accepted.
        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task<string> ObserveConnectionAsync(
            QuicConnection connection,
            List<QuicStream> peers,
            CancellationToken token
        )
        {
            try
            {
                while (true) peers.Add(await connection.AcceptInboundStreamAsync(token));
            }

            catch (QuicException error) when (error.QuicError == QuicError.ConnectionAborted)
            {
                return Closed(error);
            }
        }

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static string Closed(QuicException error) =>
            "connection 0x" + (error.ApplicationErrorCode ?? -1).ToString("x", CultureInfo.InvariantCulture);

        [SupportedOSPlatform("windows")]
        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        private static async Task StopAsync(CancellationTokenSource stop, Task running, List<QuicStream> peers)
        {
            foreach (var peer in peers) await peer.DisposeAsync();

            stop.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
