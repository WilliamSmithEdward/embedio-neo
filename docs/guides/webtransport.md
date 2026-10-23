# WebTransport over HTTP/3 (Neo v2)

WebTransport gives a client and a server a session over one HTTP/3 request: any
number of reliable streams in both directions, plus unreliable datagrams. The
HTTP/3 listener accepts sessions when the server opts in. It was added in
2.17.0 by [PR #506](https://github.com/WilliamSmithEdward/embedio-neo/pull/506).

The server speaks three drafts, none a published RFC:

- [draft-ietf-webtrans-http3-16](https://datatracker.ietf.org/doc/draft-ietf-webtrans-http3/16/),
  requested with `:protocol webtransport-h3`.
- [draft-ietf-webtrans-http3-02](https://datatracker.ietf.org/doc/draft-ietf-webtrans-http3/02/),
  requested with `:protocol webtransport`. Chromium's HTTP/3 stack
  ([QUICHE](https://github.com/google/quiche/blob/main/quiche/quic/core/http/http_constants.h))
  defines only the draft-02 and draft-07 settings, so Chromium-based browsers
  negotiate draft-02 with this server.
- [draft-ietf-webtrans-http3-14](https://datatracker.ietf.org/doc/draft-ietf-webtrans-http3/14/),
  also requested with `:protocol webtransport`, which Safari 27 speaks. The
  client's SETTINGS tell it from draft-02: see [Draft-14](#draft-14).

Either draft may still change on the wire before it becomes an RFC.

## Requirements

- The .NET 10 asset in `EmbedIOHttp3` or `EmbedIOCombined` mode, with the
  [HTTP/3 listener requirements](http3.md#requirements). Creating a server in any
  other mode with WebTransport configured throws `NotSupportedException`.
- A peer that negotiates QUIC datagrams ([RFC 9221](https://www.rfc-editor.org/rfc/rfc9221.html)).
  Draft-16 also needs reliable stream reset
  ([RESET_STREAM_AT](https://datatracker.ietf.org/doc/draft-ietf-quic-reliable-stream-reset/)).
  The .NET QUIC client negotiates neither, so `HttpClient` cannot open a session.

## Enabling sessions

```csharp
var server = new WebServer(options => options
    .WithUrlPrefix("https://localhost:4433/")
    .WithMode(HttpListenerMode.EmbedIOHttp3)
    .WithCertificate(certificate)
    .WithWebTransport(webTransport =>
    {
        webTransport.MaximumSessionsPerConnection = 4;
        webTransport.MaximumIncomingStreamsPerSession = 100;
        webTransport.AcceptBrowserDraft02 = true;
    }));
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `MaximumSessionsPerConnection` | 1 | Sessions one connection holds at once, 1 to 64. A session request beyond it gets 429 before any handler runs. |
| `MaximumIncomingStreamsPerSession` | 100 | Streams the peer may open in one session in each direction before earlier ones finish, 1 to 1024. |
| `AcceptBrowserDraft02` | `true` | Also accept the draft-02 handshake browsers use. Set `false` to accept draft-16 only. |

The settings are locked when the server is created. A server without
`WithWebTransport` advertises nothing and serves the same requests as before.

More than one session per connection needs session flow control, which a draft-16
peer enables by declaring its own limits. Draft-02 has no flow control, so its
connections hold up to `MaximumSessionsPerConnection` sessions with only the
stream count enforced.

Below these settings, each QUIC connection lets the peer hold 128 bidirectional
and 16 unidirectional streams open at once, for every session and request on it.
HTTP/3 itself uses three of the unidirectional streams. A peer that reaches either
cap waits for earlier streams to finish, however high the session limits are.

## Accepting a session

A session request is an ordinary request: it passes through every module before
the one that accepts it, so authentication modules registered earlier apply. HTTP/2
and HTTP/3 contexts implement `IWebTransportContext`; `IsWebTransportRequest` is
`true` only for a session request on a connection that negotiated WebTransport.

```csharp
if (context is not IWebTransportContext { IsWebTransportRequest: true } webTransport)
    throw HttpException.BadRequest("Expected a WebTransport session request.");

if (context.Request.Headers["origin"] != "https://app.example")
    throw HttpException.Forbidden();

await using var session = await webTransport.AcceptWebTransportAsync(context.CancellationToken);
```

Authorize before calling `AcceptWebTransportAsync`. To refuse a session, throw an
`HttpException` such as 403; the peer receives that status and no session starts.
Browsers send the session request with an `Origin` header and without cookies
(the [WebTransport API](https://www.w3.org/TR/webtransport/) uses credentials mode
`omit`), so check the origin and authenticate with a short-lived token in the URL
or inside the session.

`AcceptWebTransportAsync` answers 200 and starts delivering streams and datagrams,
including those the peer sent before the session was accepted. Keep the handler
running while the session is in use: when it returns, the server closes a session
that is still open, with code 0.

The server answers these requests itself, before any module sees them:

| Request | Answer |
| --- | --- |
| A session request on a connection whose SETTINGS or transport parameters do not meet its draft's requirements | Stream reset with `H3_MESSAGE_ERROR` (draft-16 section 3.1) |
| A session request beyond `MaximumSessionsPerConnection` | 429 |
| A session request that arrives during a graceful drain | Stream reset with `H3_REQUEST_REJECTED` |

## A complete echo server

The program below echoes every stream and datagram of each session. It reads a
PEM certificate and key, as a Linux or macOS host usually stores them. On Windows
use any certificate with its private key.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

// Usage: dotnet run -- <certificate.pem> <key.pem> <port> <allowed origin>
var allowedOrigin = args[3];
using var pem = X509Certificate2.CreateFromPemFile(args[0], args[1]);
// The Linux and macOS TLS runtime needs an exportable private key.
using var certificate = X509CertificateLoader.LoadPkcs12(
    pem.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var server = new WebServer(options => options
        .WithUrlPrefix($"https://localhost:{args[2]}/")
        .WithMode(HttpListenerMode.EmbedIOHttp3)
        .WithCertificate(certificate)
        .WithWebTransport(webTransport => webTransport.MaximumSessionsPerConnection = 4))
    // A module's base route ends with a slash, so this one checks the path itself.
    .WithModule(new ActionModule("/", HttpVerbs.Any, context => ServeAsync(context, allowedOrigin)));

var running = server.RunAsync(stop.Token);
Console.WriteLine($"Sessions at https://localhost:{args[2]}/echo");

try
{
    await running;
}
catch (OperationCanceledException)
{
}

static async Task ServeAsync(IHttpContext context, string allowedOrigin)
{
    if (context.RequestedPath != "/echo")
        throw HttpException.NotFound();

    if (context is not IWebTransportContext { IsWebTransportRequest: true } webTransport)
        throw HttpException.BadRequest("Expected a WebTransport session request.");

    if (context.Request.Headers["origin"] != allowedOrigin)
        throw HttpException.Forbidden();

    var token = context.CancellationToken;
    await using var session = await webTransport.AcceptWebTransportAsync(token);
    var echoes = new List<Task> { EchoDatagramsAsync(session, token) };

    // AcceptStreamAsync returns null once the session has ended.
    while (await session.AcceptStreamAsync(token) is { } stream)
        echoes.Add(EchoStreamAsync(session, stream, token));

    await Task.WhenAll(echoes);
}

static async Task EchoDatagramsAsync(WebTransportSession session, CancellationToken token)
{
    while (await session.Datagrams.ReceiveAsync(token) is { } datagram)
    {
        using (datagram)
        {
            // Datagrams are unreliable: an echo the send queue cannot take is dropped.
            _ = session.Datagrams.TrySend(datagram.Payload);
        }
    }
}

// A bidirectional stream answers on itself; a unidirectional one on a stream
// opened when its first bytes arrive.
static async Task EchoStreamAsync(WebTransportSession session, WebTransportStream stream, CancellationToken token)
{
    var output = stream.IsBidirectional ? stream : null;
    var buffer = new byte[16384];

    try
    {
        int count;

        while ((count = await stream.ReadAsync(buffer, token)) != 0)
        {
            output ??= await session.OpenStreamAsync(false, token);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }

        if (output != null)
            await output.CompleteWritesAsync(token);
    }
    catch (Exception error) when (error is IOException or InvalidOperationException)
    {
        // The peer reset the stream or the session ended. Reset the echo as well,
        // so the peer cannot mistake a partial echo for a complete one.
        output?.Abort(WebTransportAbortDirection.Write, 0);
    }
    finally
    {
        if (output != null && output != stream)
            await output.DisposeAsync();

        await stream.DisposeAsync();
    }
}
```

## Streams

`AcceptStreamAsync` returns the streams the peer opens, then `null` once the
session has ended. A unidirectional stream from the peer can only be read.
`OpenStreamAsync(true)` opens a stream both sides use and `OpenStreamAsync(false)`
one this side only writes. Under draft-16 flow control, opening waits while the
peer's stream limit is used up, and writes wait while the session's data credit is.

`WebTransportStream` is a `Stream`. Disposing it completes its writes and stops
reading what the peer has not sent yet. `CompleteWritesAsync` ends the send
direction with the data already written. `Abort(direction, code)` ends one or both
directions at once with a 32-bit application error code. Reads and writes on a
direction either side aborted throw `IOException`.

## Datagrams

`session.Datagrams` is an `HttpDatagramChannel`. `ReceiveAsync` returns one owned
`HttpDatagram`, which you dispose after reading its `Payload`, and `null` once the
session has ended. `TrySend` submits a payload without waiting and reports the
outcome: `Queued`, `Unavailable`, `TooLarge`, `QueueFull` or `Closed`.
`MaximumPayloadLength` is the current limit after the session identifier. Delivery
is neither reliable nor ordered.

In the browser, Safari 27 exposes only the newer `datagrams.createWritable()`, and
Chromium only `datagrams.writable`, so a page that sends datagrams should use
whichever exists.

## Closing

`CloseAsync(code, reason)` sends the close code and a message of up to 1024 UTF-8
bytes, then ends the session stream; the session's streams are reset.
`Completion` completes when the session has ended, however it ended. `CloseCode`
and `CloseReason` then hold what the peer or the server sent, or `null` when the
session ended without a close, for example when the connection was lost.

When the client's connection ends before the session does, the request is canceled
as well. A call waiting with the request's token then either returns as it does at
the end of a session, with `null`, or throws `OperationCanceledException`,
depending on which the server notices first. Firefox 157 ends sessions this way: it
closes its connection right after the page calls `close()`, so the server records
no close code. Safari 27 sends no close capsule either; its session stream ends,
and the server records no close code.

During a graceful drain the server sends each session a drain capsule, asking its
peer to finish, and lets the sessions run until the drain deadline. Draft-02 has no
drain capsule, so its peers ignore it. Stopping the server ends every session.

## Bounds

- A session's peer may have 256 KiB of unread stream data in flight under draft-16
  flow control. Credit is renewed as the application reads, so a handler that stops
  reading stops the peer.
- Streams and datagrams that name a session request the server has not processed
  yet are buffered per connection: 16 streams, 32 datagrams and 64 KiB of
  datagrams. Later arrivals are refused or dropped. When the request turns out not
  to be a session, its buffered streams are reset with `WT_SESSION_GONE`. On a
  connection that did not negotiate WebTransport they are reset with
  `WT_BUFFERED_STREAM_REJECTED`.
- QUIC limits of the connection still apply on top of these.

## Draft-02 differences

Draft-02 sessions have no session flow control, so the stream count is their only
per-session bound, and no reliable reset: a stream the peer resets can lose the
bytes that name its session, and the peer may then discard it without notice. A
draft-02 session request gets 200 with `sec-webtransport-http3-draft: draft02`.
If no browser needs to connect, set `AcceptBrowserDraft02` to `false`.

## Draft-14

Safari 27 speaks draft-14. Its sessions work as draft-16 sessions do, with session
flow control and reliable reset; the streams, capsules and flow-control settings are
the same. What differs is how a connection enables WebTransport:

- Both sides send `SETTINGS_WT_MAX_SESSIONS` (`0x14e9cd29`) instead of
  `SETTINGS_WT_ENABLED`. The server advertises it with
  `MaximumSessionsPerConnection`.
- Sessions are requested with `:protocol webtransport`, the draft-02 token. A
  connection whose client sent `SETTINGS_WT_MAX_SESSIONS`, and not the draft-16
  setting, uses draft-14; one whose client sent draft-02's setting uses draft-02.
- Reliable reset is negotiated with the `reset_stream_at` transport parameter of
  draft-ietf-quic-reliable-stream-reset-07, `0x17f7586d2cb571`; later drafts use
  `0x1d`. The frame is the same, and EmbedIO.Quic sends and accepts both codepoints
  on every connection.

Safari also needs `serverCertificateHashes`, or a certificate it trusts, to finish
the handshake. Set `AcceptBrowserDraft14` to `false` to refuse draft-14.

## Validation

The in-suite tests cover the settings the server advertises, malformed session
requests on connections that did not negotiate WebTransport, the draft-02 token
as an ordinary request when no browser dialect it could name is on, ordinary GET and POST requests on
a server with WebTransport enabled, request streams that end the same way with and
without it, and streams refused on connections without WebTransport. Sessions
themselves need a peer with QUIC datagrams and reliable reset, so the development
host drives draft-16 sessions with an independent
[quic-go](https://github.com/quic-go/quic-go) peer on Windows and Linux, and the
complete program above passes that peer's echo, reset, large-transfer and origin
cases. CI runs every peer mode in draft-16 and again in draft-14.

A Chromium 152 browser on Windows opened a draft-02 session from a page on
`http://localhost`, pinning a 10-day ECDSA certificate with
`serverCertificateHashes`: it echoed a datagram, a bidirectional stream and a
unidirectional stream, and both sides recorded the close code and reason the page
sent. On Linux, headless Chromium 156 and Firefox 157, driven by Playwright 1.64.0,
ran the same page against the packaged server and echoed all three; Chromium's
close code reached the server and Firefox's did not, as described under Closing.
Playwright's Linux WebKit build does not implement WebTransport.

On macOS 27.0.1, the same page ran against the complete program above:

| Browser | Dialect | Result |
| --- | --- | --- |
| Chromium 152 (the Claude desktop app's browser) | draft-02 | Passed with 2.17.0 and with draft-14 support; the server recorded the close code |
| Safari 27.0.1 | draft-14 | Passed with draft-14 support; failed with 2.17.0, which does not speak it |
| Playwright WebKit 27.2 | draft-14 | The same as Safari |

Each passing session echoed a datagram, a bidirectional stream and a unidirectional
stream and closed with the page's code and reason. Firefox on macOS was not tested:
Playwright 1.64.0's Firefox build did not start on macOS 27. The
[development record](../project/quic-development-and-testing.md) has the details.

Not implemented: application protocol negotiation with `WT-Available-Protocols` and
`WT-Protocol` (draft-16 section 3.3), keying-material exporters (section 4.8) and
WebTransport over HTTP/2 (draft-ietf-webtrans-http2).
