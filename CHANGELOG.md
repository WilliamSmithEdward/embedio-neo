# Changelog

## [Unreleased]

- HTTP/1 responses encode their head straight into the pooled array that carries the
  first body segment, instead of into a `MemoryStream` whose bytes were then copied into
  that segment, and the status code text is shared between responses. A small response
  allocates about 6% less (5,119 -> 4,802 bytes per request in the load harness); the
  wire bytes and write boundaries are unchanged. See the
  [load comparison](docs/project/http-engine-load-comparison.md#http1-response-head-in-the-first-write-segment-2026-10-15).

## [2.0.0] - 2026-10-15

Major release. The inherited HTTP listeners are replaced by an independently
authored managed engine with HTTP/1.1 and HTTP/2; HTTP/2 over TLS and opt-in
HTTP/3 need the .NET 10 asset. The Microsoft `HttpListener` backend is removed, request
limits have finite defaults modelled on Kestrel, malformed requests are rejected
rather than normalized, and HTTPS requires TLS 1.2 or later. Namespaces, assembly
names and the fluent `WebServer` pipeline are unchanged, but this is not a
binary-compatible upgrade: rebuild dependent assemblies and read the
[v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md). Applications
coming from the archived EmbedIO 3.x should start with the
[upgrade checklist](docs/compatibility/upgrading-from-embedio-3.md).

- Behavior change: request limits have finite defaults, modelled on Kestrel's
  `KestrelServerLimits` in ASP.NET Core 10.0.13. A request body may carry 30,000,000
  bytes (`MaximumRequestBodyBytes`, as Kestrel's `MaxRequestBodySize`), header fields may
  total 32 KiB (`MaximumRequestHeaderBytes`, as `MaxRequestHeadersTotalSize`) in at most
  100 lines (`MaximumRequestHeaderCount`, as `MaxRequestHeaderCount`), a new connection
  must deliver its first request head within 30 seconds instead of 90
  (`RequestHeadersTimeout`, as Kestrel's), and a WebSocket message may be 4 MiB
  (`WebSocketModule.MaxMessageSize`, previously unlimited). With
  `SupportCompressedRequests`, the decoded body limit follows the request's body limit,
  including a route's, until `MaximumDecompressedRequestBodyBytes` is set. Every default
  is replaced as before, and null still removes a limit (zero for `MaxMessageSize`).
  `RequestBodyReadTimeout`, `MaximumConcurrentRequests` and `RequestTimeout` stay off, so
  no default bounds how long a body the application is reading takes to arrive; set one
  of them to bound it. A minimum data rate like Kestrel's is tracked in #424. Because a
  body limit now applies by default, `100 Continue` is sent on a handler's first read of
  the body rather than before dispatch, so on HTTP/1.1 a handler that ignores an
  `Expect: 100-continue` body answers with `Connection: close`; and on HTTP/1.1 the
  request stream is closed once disposed, as on HTTP/2 and HTTP/3. A body the handler
  never opens no longer holds back the response: request cleanup used to read it first,
  under the body limit and read deadline, and turn an uncommitted response into 408 or
  413 when the body stalled or ran over. Now the handler's response goes out first.
  HTTP/1.1 then discards the body under the keep-alive deadline, the body limit and any
  read deadline, and closes the connection when one of them ends the discard; HTTP/2 and
  HTTP/3 end the stream with `RST_STREAM` `NO_ERROR` or `STOP_SENDING` `H3_NO_ERROR`. See
  [migration](docs/compatibility/neo-v1-to-v2.md#finite-request-limit-defaults).
- Correct three request-body defects. On HTTP/1.1, a handler that disposed the request
  stream while a body read deadline was set made the server close the connection after a
  response that had promised to keep it open, so a client that reused it saw a reset; the
  server now discards the rest of the body and keeps the connection. The counted view of
  a body under a body limit reported `CanRead` as true after its transport stopped
  reading, as an HTTP/3 body does once a read is canceled; it now reports the transport's
  state. With a read deadline set, a body read that failed after the deadline, a
  cancellation or the end of its request had already answered the call went unobserved
  and could surface later through `TaskScheduler.UnobservedTaskException`; it is now
  observed.
- Two more controller binding generator corrections. Two binding contexts whose full names
  differed only in case, such as `One.Bindings` and `ONE.Bindings`, gave the generator two
  hint names that Roslyn considers equal; it threw, and a generator that throws contributes
  nothing, so every generated binding in that compilation disappeared. Generated file names
  now carry a short fingerprint of the exact name, so a project that pins generated file
  paths, such as one building with `EmitCompilerGeneratedFiles` or keeping a checked-in
  snapshot of them, sees new file names. A `CultureInfo` query or form field was
  rejected with EMBEDIO009 although the reflection-based binding converts it, so it joins the
  intrinsic converters, with the converter-parity test covering it.
- The controller binding generator no longer emits code a consumer cannot build. A binding for
  a controller or route marked `[Obsolete]` warned (CS0612, CS0618) in the generated file, which
  fails a build that treats warnings as errors; the generated file now suppresses those two
  warnings, since calling the deprecated member is the binding's job. A binding context whose
  name matches its controller's emitted a member named after its own type (CS0542) and is now
  reported as a duplicate binding instead. A `partial record` or `partial struct` context was
  ignored in silence although the emitter writes its declaration back correctly, so it now
  generates like a class.
- Behavior change: `IHttpRequest.CheckIfNoneMatch` compares entity tags weakly, the comparison
  `If-None-Match` requires (RFC 9110, Section 13.1.2) and the one `EvaluatePreconditions`
  already applied. A client that sends `If-None-Match: W/"abc"` for a resource tagged `"abc"`
  now receives 304 instead of the whole representation; `If-Match` stays strong. A condition
  the method cannot parse still matches nothing rather than rejecting the request.
- The Basic authentication challenge escapes its realm. A realm containing a quote or a
  backslash produced a malformed `WWW-Authenticate` header, because the realm is a
  quoted-string (RFC 9110, Section 5.6.4; RFC 7617, Section 2.1). The `Realm` property
  still reports the value as given.
- `FileModule` bounds the mapping cache it keeps for an immutable provider. The cache is
  keyed by the requested path, and a case-insensitive file system maps every spelling of a
  name to the same file, so a client could grow the cache without bound by varying the
  case it asked for. Past 1024 entries a mapping still succeeds, uncached.
- Security: nothing an application puts in an HTTP/1 response head can start a field of its
  own. The reason phrase was written into the status line unvalidated, so assigning
  untrusted text to `IHttpResponse.StatusDescription` injected response headers, and
  `System.Net.HttpListenerResponse`, which the managed response mirrors, has always refused
  that. A cookie reached the field section the same way, because `System.Net.Cookie` does
  not refuse control characters in a value and a cookie is serialized without passing
  through `WebHeaderCollection`. `WebHeaderCollection` itself refuses a bare CRLF in a
  value but accepts one followed by a space, which is obs-fold, and RFC 9112, Section 5.2
  says a server must not generate it. The reason phrase is now refused where it is
  assigned, on both response types so that one `IHttpResponse` contract behaves the same
  on every protocol, and the field section, cookies included, is held to the character rule
  that HTTP/2, HTTP/3, informational sections and trailers already enforce. See
  [migration](docs/compatibility/neo-v1-to-v2.md#response-field-and-reason-phrase-validation).
- Security: a ban falls on the client that earned it. `IPBanningRegexCriterion` reads
  process-wide diagnostic messages and charged each match to `IPBanningModule.ClientAddress`,
  which holds whichever request passed through the module last, from any thread. A message
  produced by one client could therefore ban another, and an offender's own matches could be
  charged elsewhere. Matches are now charged to the request a message names, so a message
  naming no request the module handled is charged to nobody. `ClientAddress` is unchanged
  and still public, and nothing decides anything by reading it.
- `BasicAuthenticationModule` compares a password in time that does not depend on how many
  characters matched, and an unknown user now costs the same work as a known one.
- WebSocket closes end in time, and stopping the server closes WebSockets cleanly. A close
  on a protocol error or a rejected message, and `Dispose`, now give up after one second
  when the close frame cannot be written, for example because the client stopped reading,
  and release the connection. They could wait without limit before, holding a thread. A
  WebSocket finalized without being disposed releases its connection without a closing
  handshake. `RunAsync` cancellation and `WebServer.Dispose` send each open WebSocket a
  1001 close, then wait at most one second for those closes and any close already under
  way before the listener stops; clients previously saw the connection end without a
  close (1006).
- `WebSocketModule.KeepAliveInterval` takes effect. The server pings each client at that
  interval (30 seconds by default) and closes a connection whose client has not answered
  the previous ping when the next one is due. `TimeSpan.Zero` turns it off. See
  [migration](docs/compatibility/neo-v1-to-v2.md#websocket-closes-sends-and-keep-alive).
- `IWebSocket.CloseAsync` throws `ArgumentException` for a status a server cannot send
  (RFC 6455 section 7.4), a comment with `NoStatus`, or a comment longer than 123 UTF-8
  bytes, as `System.Net.WebSockets` does; these calls were ignored or sent as given.
  `IWebSocket.SendAsync` throws `WebSocketException` when the WebSocket closes before the
  whole message is sent, where it returned as if the message had been sent. See
  [migration](docs/compatibility/neo-v1-to-v2.md#websocket-closes-sends-and-keep-alive).
- Security: a file provider decodes a served path one segment at a time, so an encoded
  separator can no longer reach a resource whose modules never ran. Modules are selected
  from the undecoded path, because the route is built from `Url.AbsolutePath` and a `%2F`
  stays encoded there (RFC 3986, Section 2.2), while `FileSystemProvider` and
  `ZipFileProvider` decoded the whole path and turned that `%2F` into a separator. A
  request for `/admin%2Fsecret.txt` therefore mapped to `admin/secret.txt` although the
  modules mounted on `/admin`, an authentication module among them, never matched it.
  Each segment now decodes on its own, and a segment that decodes to a separator, to a
  `.` or `..` segment, or to a control character names no resource. Containment inside
  the served root, which already held, is unchanged. See
  [migration](docs/compatibility/neo-v1-to-v2.md#encoded-separators-in-served-paths).
- An HTTP/1 connection bounds the two steps of its request cycle that were unbounded.
  Discarding a request body a handler never read now honors the keep-alive deadline, so
  a client that declares a body, takes its response and then stops sending no longer
  holds the connection, and its admission slot, with no timer running. A connection that
  has answered a `101 Switching Protocols` is no longer treated as reusable for another
  HTTP/1 message (RFC 9110, Section 7.8), so a handler that returns while its WebSocket
  is still open cannot leave the request cycle reading frames as a request head. See
  [migration](docs/compatibility/neo-v1-to-v2.md#request-and-response-corrections).
- Correct defects found by a second review sweep. On HTTP/1, a request field sent on
  several lines combines them in order (RFC 9110, Section 5.3) instead of keeping only
  the last, as HTTP/2 and HTTP/3 already did; Cookie lines join with `; `. A quality
  value's weight name matches case-insensitively (RFC 9110, Section 12.4.2), so
  `gzip;Q=0` refuses gzip as `gzip;q=0` does. The OpenAPI builder describes strings
  under EmbedIO's default JSON options, documents operations with `[FromServices]`
  parameters, declares parameters of a module's base route, lists a form field bound
  twice as required once, keeps component keys within OpenAPI's allowed characters,
  and reports instead of throwing or misdescribing what it cannot describe: options the
  schema exporter refuses (such as `ReferenceHandler.Preserve`) and polymorphic result
  types, which are written without their discriminator. See
  [migration](docs/compatibility/neo-v1-to-v2.md#request-and-response-corrections).
- Security: a request handler that is still running counts against the connection's
  128-request limit even after its stream is reset or its response has finished, on
  HTTP/2 and HTTP/3. Before this, a client could reset streams to start an unbounded
  number of concurrent handlers on one connection (the CVE-2023-44487 "Rapid Reset"
  pattern). On HTTP/2 a new stream over the limit is refused with `REFUSED_STREAM`,
  which clients retry, and a client refused more than 128 times within one second
  receives `GOAWAY ENHANCE_YOUR_CALM`. HTTP/3 now allows 128 outstanding request
  handlers per connection instead of 256, matching HTTP/2 and the QUIC stream limit;
  a request over it is refused with `H3_REQUEST_REJECTED`. Graceful shutdown still
  waits only for responses, not for handlers that outlive them.
- Allocate less per request. An uncontended asynchronous response write no longer
  allocates a task, a release delegate and a lock object. The per-request start and
  completion log messages are built only when a trace listener or a
  `Log.MessageWritten` observer will receive them. Request completion no longer creates
  an unused `Items` dictionary. HTTP/1 responses reuse the cached `Date` and charset
  `Content-Type` values that HTTP/2 and HTTP/3 already used, context IDs are built
  without intermediate arrays, and HTTP/1 header encoding no longer allocates
  `AllKeys`. Behavior is unchanged; see the
  [load comparison](docs/project/http-engine-load-comparison.md#request-path-allocation-october-2026).
- Behavior change: on the .NET 10 asset, a client that accepts Brotli at the same weight as
  gzip or deflate now gets Brotli, as with ASP.NET Core's response compression. Browsers send
  `Accept-Encoding: gzip, deflate, br` and got gzip: a tie went to the coding the client listed
  first, and browsers list `br` last. Client weights still decide (`gzip;q=1, br;q=0.5` gets
  gzip). Ties with `identity`, the choice between gzip and deflate, and the .NET Standard 2.0
  asset are unchanged. Dynamic responses (`OpenResponseStream`, `OpenResponseText` and the
  helpers built on them, such as `SendDataAsync` and `SendStringAsync`) compress Brotli at
  quality 1 (`CompressionLevel.Fastest`, ASP.NET Core's default) instead of `BrotliStream`'s
  default quality 4. `FileModule` keeps quality 4 for cached and streamed files, about the size
  of the previous gzip responses. William accepted on 2026-10-14 the 6-17% larger dynamic
  responses than the previous gzip default for 56-77% less compression CPU. One thread,
  runtime 10.0.13, a JSON document compressed once per response (`--compression-timing`):

  | JSON | gzip default level (before) | Brotli quality 1 (now) | Brotli quality 4 |
  | --- | --- | --- | --- |
  | 1 KiB | 514 B, 9.3 us | 545 B, 4.1 us | 499 B, 13.5 us |
  | 64 KiB | 12,128 B, 347 us | 13,672 B, 81 us | 12,288 B, 329 us |
  | 1 MiB | 174,771 B, 6.43 ms | 203,734 B, 1.66 ms | 174,491 B, 5.08 ms |

  End to end over HTTP/1.1 with a browser's `Accept-Encoding` (load benchmark `-json`
  scenarios, roles alternated), requests per second rose 1.23x, 3.08x and 3.62x for the
  1 KiB, 64 KiB and 1 MiB documents, and server CPU per request fell to 0.82x, 0.32x and 0.28x.
- Refuse to share an HTTPS endpoint between listeners that present different server
  certificates. Owners share an endpoint only with the same `ServerCertificateSource`
  instance or with certificates that compare equal by hash (a combined listener's internal
  source counts as its fixed certificate); a second owner with another certificate, or a
  fixed certificate beside a source the application can rotate, is refused before its routes are
  published, with "different server certificates" in the `HttpListenerException`, and
  the first owner keeps serving. Before, the endpoint presented its opener's certificate
  and TLS policy to every owner, even after the opener left. The TLS-policy
  compatibility rules are unchanged.
- Refuse to start an https prefix on a managed listener that has neither a certificate
  nor a certificate source: `Start` throws `InvalidOperationException`, and
  `WebServer.RunAsync` faults with it, instead of opening the port and dropping every
  connection at the TLS handshake. The HTTP/3 and combined listeners already refused a
  missing certificate at construction.
- HTTP/2 and HTTP/3 requests with a request deadline keep hearing the server's stop token
  through their context token, in the handler and in `OnRequestCompleted` callbacks after it.
  Installing the deadline disposed the token source behind the token the server captures
  and restores afterwards. With `WebServer` such a request still ended at shutdown, because
  stopping the listener cancels its stream, but a host that stops the server without
  cancelling the stream left the request waiting. HTTP/1 is unaffected.
- Fix: a graceful HTTP/2 end (a drain or the idle GOAWAY) no longer resets the
  connection. The peer may still be sending, for example WINDOW_UPDATE for the final
  DATA or HEADERS it sent before reading GOAWAY, and closing both directions with
  those bytes unread sent a TCP reset that discarded responses the peer had not read
  yet. The connection now half-closes after its final frames and reads and discards
  input until the peer closes, for at most 2 seconds, before it closes (RFC 9112
  section 9.6).
- Dispatch each admitted request straight from its connection to the server's
  handler instead of queueing it for the accept loop, in plain, HTTP/3 and combined
  mode. The HTTP/1.1 actor starts the handler on its own thread and then yields to
  the thread pool's global queue once per request so that no connection monopolizes
  a worker, HTTP/2 exchanges hand off on their own work item, and HTTP/3 requests
  start the handler on their stream's own task instead of passing through the
  listener's context channel, the combined listener's second channel and a
  `Task.Run`. `IHttpListener.GetContextAsync` is unchanged and still serves
  standalone listeners; admission, drain and stop semantics are unchanged, and a
  handler may still run on after completing its response without holding the
  connection's next request.
- Fix: a request whose dispatch fails before the handler starts (an exception while the
  server prepares the context) now closes its HTTP/1.1 connection, or resets its HTTP/2
  stream, after logging the failure. Previously that request was never answered and its
  connection stayed open until the client gave up. If closing that request's context
  fails as well, its response is aborted instead of leaving the connection waiting for
  a response that never ends. The server also observes and logs a fault a handler
  leaves uncaught after its response, instead of leaving it unobserved.
- Behavior change: a response trailer section refuses all seven response control data
  fields of RFC 9110, Section 6.6.1, which Section 6.5.1 keeps out of a trailer. `Age`,
  `Date`, `Expires` and `Vary` join `Cache-Control`, `Location` and `Retry-After`, which
  were already refused. A recipient ignores control data in a trailer, so an application
  that sent one of the four was losing the value silently; declaring one now throws
  `InvalidDataException`. See
  [migration](docs/compatibility/neo-v1-to-v2.md#request-and-response-corrections).

- Build and test with .NET SDK 10.0.402 and runtime 10.0.13, the 13 October 2026
  security release. Package dependency minimums move from 10.0.12 to 10.0.13:
  `System.Threading.RateLimiting` on both core targets, `System.Text.Json` on the
  core .NET Standard 2.0 asset, and `Microsoft.Extensions.DependencyInjection` and
  `Microsoft.Extensions.Hosting.Abstractions` in the DI package. An application
  that references one of these packages directly at an earlier version must move
  it to 10.0.13 or later; NuGet otherwise reports a package downgrade (NU1605).
  On Windows, run HTTP/3 on runtime 10.0.13 or later because of CVE-2026-105016;
  see [HTTP/3 requirements](docs/guides/http3.md#requirements).
- `WebApiModule.RegisterController(Type, Func<WebApiController>)` and
  `WithController(Type, Func<WebApiController>)` accept a lambda factory. A lambda converted to
  `Func<WebApiController>` is compiled with that return type, so every lambda was rejected with
  "Factory method has an incorrect return type", as in upstream EmbedIO 3.5.2. An unrelated declared
  return type is still rejected; a factory that creates a different controller now fails with an
  `InvalidOperationException` naming both types. `RegisterController<TController>(Func<TController>)`
  is unchanged.
- `TestWebServer.RunAsync` (EmbedIO.Testing) now returns its task to the caller instead of
  blocking it until the server stops, and a started test server no longer pins a thread-pool
  thread. Awaiting `RunAsync` directly previously deadlocked. The blocking wait was inherited
  unchanged from upstream EmbedIO 3.5.2.
- Correct defects found by a review sweep. `ISession.Delete` and `Regenerate` now end
  a session that holds data, so an old cookie cannot resume it, and an expired session
  is no longer revived before the purge runs. A module whose own exception handler
  answers a request ends dispatch instead of falling through to later modules or a 404.
  An `HttpException` with a status missing from the standard table (such as 499) keeps
  that status instead of becoming 200. `If-None-Match` lists and `*` match, and date
  validators compare whole seconds, so echoing `Last-Modified` revalidates and resumes.
  Complete WebSocket messages report `EndOfMessage`, quality-value names compare
  case-insensitively, ZIP entries report UTC times, and `{{` in a route matches a
  literal `{` as documented.
- Approved behavior corrections: a `+` in a route parameter stays a literal `+`;
  required query and form fields are found regardless of name case; a byte range
  ending past the file selects the remainder (206) instead of 416; a restricted
  `CorsModule` sends `Vary: Origin`; the Basic challenge uses the standard
  `Basic realm="...", charset="UTF-8"` form; and directory listings requested
  without a trailing slash link inside the directory. See
  [migration](docs/compatibility/neo-v1-to-v2.md#request-and-response-corrections).
- HTTP/1 status lines carry the standard reason phrase for every status that
  `HttpStatusDescription` lists. 103, 208, 226, 308, 421, 426, 428, 429, 431, 451,
  506, 508, 510 and 511 used to go out with an empty phrase; the same phrases are
  now also their default `StatusDescription`, problem-details `title` and OpenAPI
  response description. See
  [migration](docs/compatibility/neo-v1-to-v2.md#request-and-response-corrections).

- Require explicit AcceptTunnelAsync before successful HTTP/2 or HTTP/3 extended
  CONNECT output. An ordinary catch-all 2xx becomes an empty 501; explicit errors,
  application dispatch, plain CONNECT and actual WebSocket acceptance remain.
  Direct-stream custom handlers must migrate to HttpTunnel. See
  [migration](docs/compatibility/neo-v1-to-v2.md#explicit-extended-connect-acceptance).

This major HTTP-engine upgrade requires rebuilding consumers. Follow the
[Neo v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md) for the approved
API and behavior changes: the Microsoft listener backend is retired, `RawUrl`
becomes `RawTarget`, selected APIs return `Uri`, named parameters change, HTTPS
requires TLS 1.2 or later, and unhandled exceptions return an empty 500 by default.
HTTP/1.1 and HTTP/2 use the replacement engine; opt-in HTTP/3 uses `System.Net.Quic`
on the .NET 10 asset. Strict framing, first-flush commitment and lifecycle rules
are documented in the migration guide.

The tested .NET 10.0.12 HTTP/3 transport has a per-stream native send-buffer
retention defect in `System.Net.Quic`. The upstream fix merged into runtime
`main` on 13 October 2026. Runtime 10.0.13 does not contain it, and a .NET 10
servicing backport and released-runtime verification remain unconfirmed. See
[HTTP/3 deployment guidance](docs/guides/http3.md#runtime-memory-limitation).

Release acceptance remains in progress. The retained Windows HTTP/2 drain
response-loss failure has no established cause or repair despite later passing
validation. Native application-provider selection, unreliable datagrams and
WebTransport remain outside public deployment support, with their investigation
deferred. Combined mode requires TCP and UDP bindings, and Alt-Svc discovery is
not automatic. Performance results apply only to their recorded source and host.

- Replace the new one-argument certificate-source listener constructor with
  `HttpListener.CreateWithCertificateSource(source)`, preserving published v1
  positional null/default certificate calls. The source and its certificates
  remain borrowed; the two-argument source/TLS-policy constructor is unchanged.
  See [Neo v1-to-v2 migration](docs/compatibility/neo-v1-to-v2.md#rebuild-and-update-source-references).
- Refuse shared HTTPS endpoint registration with incompatible TLS policies, rather than silently
  applying the physical opener's policy to another logical listener. Compatible/default policies and
  fixed-certificate sharing remain supported. Use separate ports for different requirements;
  see [migration](docs/compatibility/neo-v1-to-v2.md#shared-https-endpoints-require-compatible-tls-policies).
- Owner-approved Neo v2 candidate: completed HTTP/1 keep-alive idle exchanges
  attempt orderly send shutdown at the existing deadline instead of inherited
  reset closure. Partial requests/responses retain abort behavior; TLS capability
  and interruption fallbacks are documented in the v1-to-v2 migration guide.
  This is unreleased and changes no public APIs or timeout values.

- Add opt-in WebSocket closing during graceful drain from Neo #280. With `WebSocketModule.CloseOnDrain`,
  `WebServer.DrainAsync` closes the module's WebSockets with 1001 Going Away as it starts, including ones
  accepted afterwards, after any message already being sent, so the drain ends once clients close instead
  of at its deadline. It works over HTTP/1.1, HTTP/2 and HTTP/3, and in module groups. `WebServer.DrainStarted`
  is canceled when the drain starts, for ending other long-lived work. By default drain still waits for
  WebSockets until its deadline. See [WebSockets during a graceful drain](docs/guides/graceful-drain-websockets.md).
- Add opt-in TLS policy from Neo #148. `ServerTlsPolicy`, configured with `WithTlsPolicy` or passed to
  `HttpListener`, asks clients for a certificate (`ClientCertificateMode.Allow` or `Require`), validates it
  with an application callback or, by default, normal trust, optionally checks revocation, limits the TLS
  protocol versions, and on the .NET 10 asset chooses the server certificate by server name (SNI).
  `context.Request.GetClientCertificate()` returns the client's certificate over HTTP/1.1, HTTP/2 and
  HTTP/3. A server refuses settings its listener cannot honor when it is created: SNI on the .NET
  Standard 2.0 asset, client certificates and SNI on the native HTTP/3 binding, and protocol limits
  excluding TLS 1.3 for HTTP/3. Without a policy nothing changes. See
  [TLS policy](docs/guides/tls-policy.md).
- Use an empty 500 response for unhandled application exceptions by default in Neo v2,
  without exposing their type or message. Explicit HTML and diagnostic-header handlers,
  custom handlers, logging and application-authored HTTP errors remain available. See
  [Neo v1-to-v2 migration](docs/compatibility/neo-v1-to-v2.md#unhandled-exceptions-return-an-empty-500).
- Add opt-in server certificate rotation from Neo #148. `ServerCertificateSource`, configured with
  `WithCertificateSource` or passed to `HttpListener`, holds the server certificate; `Publish` replaces
  it while the server runs. New TLS handshakes over TCP (HTTP/1.1 and HTTP/2) and QUIC (HTTP/3, both
  bindings) use the current certificate, and open connections keep theirs. Certificates without a usable
  private key, outside their validity period or not allowed for server authentication are rejected and
  the current one stays. Servers configured with `Certificate` are unchanged. See
  [Rotating the server certificate](docs/guides/certificate-rotation.md).
- Add opt-in precompressed static files from Neo #144. With `FileModule.PrecompressedVariants`, a
  file module sends `app.js.br` or `app.js.gz`, stored next to `app.js`, to clients that accept
  that encoding at least as well as the one it would otherwise compress with. A variant keeps the
  original's content type and MIME compression policy, has its own entity tag and length, is
  ignored when older than the original, and is never used for `Range` requests. It works on both
  assets, since sending a `.br` file needs no encoder. Defaults are unchanged. See
  [Precompressed static files](docs/guides/precompressed-files.md), which also records measured
  CPU and size for gzip and Brotli levels.
- Correct content negotiation when a client refuses every coding EmbedIO could produce but not
  `identity`, for example `Accept-Encoding: br;q=0`, or `br` alone on the .NET Standard 2.0 asset.
  Such requests for content that prefers compression now get the uncompressed response, as
  RFC 9110 section 12.5.3 requires, instead of 406 Not Acceptable. Headers that refuse identity,
  such as `identity;q=0` or `*;q=0`, still get 406.
- Add opt-in RFC 9457 problem details from Neo #143. `WithProblemDetails()`, or
  `ProblemDetailsHandler.ForHttpExceptions()` and `ForUnhandledExceptions()` on a server or module,
  answer errors with `application/problem+json`: HTTP exceptions and Web API binding failures keep
  their status and message, a thrown `ProblemDetailsException` is sent as is, and unhandled
  exceptions reveal nothing beyond a 500 unless enabled for development. Every problem carries the
  `requestId` EmbedIO logs for the request. Clients that prefer HTML still get the HTML pages, and
  defaults are unchanged. See [Problem details for API errors](docs/guides/problem-details.md).
- Add opt-in trusted-proxy request metadata from Neo #142. `ForwardedHeadersModule` reads
  `Forwarded` or the selected `X-Forwarded-*` headers only from connections in its trusted proxy
  networks, walks at most `ForwardLimit` hops while each named sender is itself trusted, and uses a
  forwarded host only if it is allowed. The result, `context.GetForwardedRequest()`, gives the
  effective client address, scheme and host beside the connection's own values, which are not
  changed. Malformed, oversized or conflicting chains apply nothing. See
  [Behind a reverse proxy](docs/guides/forwarded-headers.md).
- Add opt-in rate limiting from Neo #141. `RateLimitingModule` admits the requests under its
  base route through a `PartitionedRateLimiter<IHttpContext>` from Microsoft's
  `System.Threading.RateLimiting` (fixed window, sliding window, token bucket, concurrency or a
  chain), holds each permit until the request completes, and answers rejections with 429 and
  `Retry-After` when the limiter knows the wait. `RejectionStatusCode` and `OnRejected` customize
  rejections; `ClientKey` keys by connection address, IPv6 by /64, without trusting forwarded
  headers. EmbedIO-Neo now depends on the `System.Threading.RateLimiting` package (10.0.13) on
  both targets. See [Rate limiting](docs/guides/rate-limiting.md).
- Require TLS 1.2 or later for Neo v2 HTTPS, retaining TLS 1.3 where supported.
  TLS 1.0 and TLS 1.1 clients must update their TLS stack or intermediary backend
  settings. This owner-approved compatibility change preserves application APIs
  and OS certificate/cipher policy. See the
  [v1-to-v2 migration guide](docs/compatibility/neo-v1-to-v2.md#minimum-https-tls-version).
- Complete OpenAPI support from Neo #139. `AddSecurityScheme` adds Basic, Bearer and API key
  schemes, and `RequireSecurity` requires one on every operation an authentication module
  handles, decided by the module's own route matching. `AddBasicAuthentication(name, module)`
  describes a `BasicAuthenticationModule`, including the 401 response and `WWW-Authenticate`
  header it sends. `AddOperation` declares routes outside generated bindings, such as
  `RoutingModule` handlers, with typed path, query, header and cookie parameters, bodies and
  responses; declarations the document cannot hold are reported. `[OpenApiResponse]` media
  types other than JSON now describe text and binary bodies. See
  [OpenAPI documents](docs/guides/openapi.md).
- Write OpenAPI 3.1 documents from generated controller bindings, from Neo #139.
  `OpenApiDocumentBuilder` describes each route's path, query and form parameters, JSON
  body and response types from what the binding binds and returns, with schemas from the
  JSON serializer options, and `WithOpenApiDocument` serves the result. `[OpenApiOperation]`,
  `[OpenApiResponse]` and `[OpenApiIgnore]` add summaries, tags and declared responses or
  leave routes out. Routes OpenAPI cannot express, such as base routes and `HttpVerbs.Any`,
  are reported rather than approximated. Works in trimmed and Native AOT applications. See
  [OpenAPI documents](docs/guides/openapi.md).
- Complete Native AOT support from Neo #138. `EmbedIO.DependencyInjection` adds
  `WithControllerServices(binding)`, which activates a controller from request services
  through its generated binding without reflection, and its .NET 10 asset is now trim- and
  AOT-analyzed: `WithControllerServices<T>()` and `WithControllerServices(Type)` carry
  `RequiresUnreferencedCode` and `RequiresDynamicCode`. The Native AOT smoke host now also
  serves HTTPS with HTTP/1.1, HTTP/2 and HTTP/3, WebSockets over TLS, local sessions and a
  DI-activated controller, on Windows and Linux. A Windows Native AOT publish omits
  `msquic.dll`; the guide shows how to publish it for HTTP/3.
- Direct HTTP/1 response `Flush` and `FlushAsync` now commit the final response
  headers in write order without ending the response. HEAD and bodyless responses
  retain their payload suppression. Disposed output rejects flushes; automatic
  request cleanup preserves empty-response framing and higher-level buffering behavior.
- Add a controller-binding source generator from Neo #138, shipped in the EmbedIO-Neo
  package. `[GeneratedControllerBinding(typeof(TController))]` on a partial class makes it
  generate a `WebApiControllerBinding<TController>`, registered with new
  `WithController(binding)`, `RegisterController(binding)` and
  `RegisterControllerWithContext(binding, ...)` overloads that use no reflection, so Web
  API controllers work in trimmed and Native AOT applications. Bindings keep the
  reflection-based registration's routes, parameter rules, converters, serialization and
  controller lifetime; `[JsonData]` can use a `JsonSerializerContext`. Signatures the
  binding cannot reproduce are reported at build time (EMBEDIO001 to EMBEDIO012).
  `[QueryField]`, `[FormField]` and `[JsonData]` no longer carry trimming annotations;
  their runtime-type paths, used only by reflection-based registration, are behind
  feature switches.
- Add the optional `IHttpRequestTrailers` capability on built-in request adapters.
  Complete valid input exposes an immutable trailer snapshot, separate from
  initial headers, without reading or draining input. Existing `IHttpRequest`
  implementations remain compatible. See [request trailers](docs/guides/request-trailers.md).
  Disposing pending HTTP/3 input cannot publish a successful snapshot on late FIN;
  snapshots from already completed input remain available after application disposal.
- Give the new unreleased explicit JSON metadata APIs distinct `WithMetadata` names.
  This restores legacy null-options calls and serializer callbacks with null data;
  reflection defaults and the Native AOT implementation are preserved.
- Make the .NET 10 asset trim- and Native-AOT-analyzable from Neo #138. Reflection-based
  APIs (Web API controller registration and its request-data attributes,
  `RoutingModule.AddFrom`/`WithHandlersFrom`, EmbedIO's default JSON options) carry
  `RequiresUnreferencedCode` and `RequiresDynamicCode`. New AOT-safe methods take
  source-generated metadata: `SendDataWithMetadataAsync(data, JsonTypeInfo<T>)`,
  `GetRequestDataWithMetadataAsync(JsonTypeInfo<T>)`, `Json.SerializeWithMetadata(data, JsonTypeInfo<T>)` and
  `Json.DeserializeWithMetadata(json, JsonTypeInfo<T>)`. Options-based JSON helpers now resolve
  metadata through the options, so a `JsonSerializerContext` works without reflection.
  The package adds a build file that disables EmbedIO's reflection JSON with the
  application's `JsonSerializerIsReflectionEnabledByDefault`; those paths then throw
  `NotSupportedException` with guidance. A Native AOT host is published and exercised in
  CI on Windows and Linux. See [trimming and Native AOT](docs/guides/trimming-and-native-aot.md).
- Release both cancellation registrations when an uncommitted HTTP/2 output write
  is canceled and removed from the queue. An independent uncanceled token no longer
  retains the dropped write and its retired connection. Wire ordering, cancellation
  behavior and flow-control refunds are unchanged.
- Extend the opt-in telemetry from Neo #137 with `http.server.request.body.size`,
  `http.server.response.body.size`, `embedio.server.active_connections` and
  `embedio.server.connection.duration` (TCP and QUIC), and
  `embedio.server.active_websockets` and `embedio.server.websocket.duration`. The
  telemetry guide records a measured allocation and latency comparison.
- Add opt-in HTTP server telemetry from Neo #137: `WithTelemetry(serverName)` publishes
  the `EmbedIO.Server` meter (`http.server.request.duration`,
  `http.server.active_requests`) and activity source (server spans with W3C trace
  context), following the OpenTelemetry HTTP semantic conventions, with route templates
  as `http.route`. Off by default; .NET 10 asset only. See
  [metrics and tracing](docs/guides/telemetry.md).
- Keep request body read buffers owned until underlying I/O actually settles after
  an optional read timeout or caller cancellation. Application buffers are no
  longer retained by abandoned reads, and pending pooled storage is not returned
  prematurely. Deadlines, HTTP 408 responses and default-off behavior are unchanged.
- Add per-route request limit policies from Neo #136:
  `WithRouteRequestLimits(baseRoute, configure)` and `RequestLimitPolicy` replace the
  server-wide `MaximumRequestBodyBytes`, `RequestBodyReadTimeout` and `RequestTimeout`
  under a base route, matched like a module's base route; the first matching policy
  applies. `long.MaxValue` or `Timeout.InfiniteTimeSpan` lifts a server-wide limit for
  the route. `WebServerOptionsBase.RouteRequestLimits` lists them.
- Add opt-in connection limits from Neo #136 on `WebServerOptions`:
  `MaximumConnections` (connections beyond it closed at once),
  `RequestHeadersTimeout` (replaces the fixed 90-second first-request deadline) and
  `KeepAliveTimeout` (replaces the fixed 15-second HTTP/1.1 idle deadline and adds a
  GOAWAY idle deadline for HTTP/2). They apply to the managed TCP listener; a server
  that sets them cannot share a port, and an HTTP/3-only server rejects them. Add
  `RequestBodyReadTimeout` (HTTP 408 when one body read waits too long) for every
  protocol. Defaults are unchanged.
- Add opt-in request header limits from Neo #136: `MaximumRequestHeaderBytes` and
  `MaximumRequestHeaderCount` (HTTP 431 before any module runs or a request slot is
  taken). While `MaximumRequestBodyBytes` is set, `100 Continue` is sent when a
  handler first reads the body instead of before dispatch, so a refused body is
  never invited. HTTP/3 is now covered by the request-limit tests.
- Keep the internal native QUIC module mapped on Linux through process teardown,
  while closing API tables and child handles. Add fresh-process native-peer and
  collectible-context checks; see [lifetime guidance](docs/project/native-quic-module-lifetime.md).
- Add opt-in, server-wide request limits from Neo #136:
  `MaximumRequestBodyBytes` (HTTP 413 on a declared or counted over-limit body,
  with HTTP/1 connection reuse stopped), `MaximumConcurrentRequests` with a bounded
  `RequestQueueLimit` (HTTP 503 when slots and queue are full) and `RequestTimeout`
  (cooperative cancellation of the context token, HTTP 503 before commitment). All
  default to off. See [request limits](docs/guides/request-limits.md).
- Document and verify compressed controller response completion and encoded
  response lengths for upstream #394, plus its separate POST control, without
  changing production behavior. Historical Xamarin validation is not claimed.
- Verify the historical WebSocket accept-to-subscription race against the
  existing startup fix, including application handshake bursts over HTTP/1 and
  HTTP/2 TCP/TLS. Extend the startup guide without changing production behavior.
- Return read-only wrappers from `WebServerOptions.UrlPrefixes`,
  `IComponentCollection.WithSafeNames` and `IComponentCollection.Named`, evaluated
  in Neo #49. The views previously were the backing collections, so a cast could
  add URL prefixes after the options locked or insert a module into a running
  server. Declared types are unchanged; see
  [configuration locking](docs/user-reports/configuration-locking.md).
- Document and verify endpoint retirement and fresh listener replacement for the
  upstream iOS shutdown report, preserving current production behavior. Actual
  iOS background/suspension recovery remains a separate validation gap.
- Let unmatched descendants of WebSocket endpoints reach later modules or the
  ordinary 404 fallback, rather than returning an empty HTTP 200. Successful
  managed HTTP/1 upgrades now expose their committed 101 status in response
  metadata; HTTP/2 extended CONNECT retains 200. See the
  [handshake guide](docs/guides/websocket-handshake-status.md).
- Give the server its own lifetime token, evaluated in Neo #43. Modules, session
  managers and request contexts now receive a token that is canceled when the
  server is disposed or its run completes, not only when the caller cancels, so
  disposal no longer leaves session purging or in-flight HTTP/1 handlers running.
  Stopping the listener directly ends `RunAsync` normally, disposal is idempotent
  and a restart attempt reports that a stopped server is final; see the
  [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown)
  and [server lifetime](docs/user-reports/server-lifetime.md).
- Correct response-time diagnostics to measure WebServer handling rather than
  context lifetime, excluding HTTP/1 keep-alive idle/header waiting while preserving
  public context age and included handler/completion work.
- Preserve reusable HTTP/1 connections when one logical listener stops on a
  shared endpoint. Active incomplete requests still abort, and retiring the final
  listener still releases idle TCP/TLS transports.
- Enforce the response commitment contract evaluated in Neo #42. Exception
  handlers run only while the response is replaceable and start from a response
  without the failed handler's representation fields, so a stale Content-Length
  no longer truncates the error body. A failure after commitment closes an HTTP/1
  body without its terminator and resets an HTTP/2 or HTTP/3 stream instead of
  ending a partial body cleanly; see the
  [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown)
  and [deferred responses](docs/user-reports/deferred-responses.md).
- Add an internally selectable native HTTP/3 listener binding for application
  context validation, retaining the existing public provider selection. Preserve
  stream/time limits, handshake backlog ownership, routing and graceful drain.
- Replace remaining TCP request stream/completion coordination with original
  ownership transitions. Concurrent first property access shares one stream owner;
  completion disposes output outside the connection gate and retires failed output.
- Replace inherited prefix, cookie and header-string helpers with original code.
  Correct CommentURL and comma-free Expires attribute parsing while preserving
  cookie identity and lookup ordering; see the Neo v1-to-v2 migration guide.
- Replace inherited HTTP/1 context completion with independently authored
  ownership. Repeated/concurrent close dispatches completion callbacks once,
  preserving their order and awaited tunnel cleanup; see the
  [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown).
- Replace inherited HTTP/1 response metadata with independently authored state
  and cookie formatting while retaining the bounded header encoder. Suppress
  attempted 205 response content and send zero-length framing, matching HTTP/2
  and HTTP/3; see the [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown).
- Replace the inherited HTTP/1 request model with independently authored parsed
  head ownership and an application view, preserving request-local collections,
  strict framing, target/version semantics and borrowed body transport behavior.
- Replace the inherited listener frontend and prefix adapter with independently
  authored lifecycle coordination and accept generations. Preserve valid public
  behavior while preventing a stopped accept from claiming a restarted listener's
  request, and a stale context withdrawal from removing a different context.
- Replace inherited per-connection transport cleanup with an independently authored
  lifetime owner. Drain waits for transport and protocol detachment, recoverable
  cleanup faults do not skip later resources, and stopped or renewed request
  deadlines reject stale timer callbacks without changing configured timeouts.
- Replace inherited TCP registration coordination with an independently authored
  endpoint index and prefix-lease ledger. Release accepted sessions outside the
  registration gate so one stalled cleanup does not block an unrelated listener
  start, while preserving shared endpoints and registration rollback.
- Replace inherited HTTP/1 response output with an independently authored owner
  for ordered writes and completion. Enforce the owner-approved declared-length
  contract and close incomplete or failed output without connection reuse,
  preserving transport-error suppression and valid HEAD/bodyless metadata. See
  the [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown).
- Complete the server accept loop normally when explicit disposal wins between
  the listening check and accept. Independent listener failures still propagate.
- Preserve HTTP/3 response output after caller-cancelled input reads, while
  abandoning input that may have been partly consumed. See the
  [Neo v2 migration guide](docs/compatibility/neo-v1-to-v2.md#response-statuses-output-and-shutdown).
- Restrict public response StatusCode assignments to final statuses (200-599),
  as explicitly approved. Use informational-response and protocol-handoff APIs
  for 1xx; automatic Continue and negotiated 101 remain supported. See the
  [migration note](docs/compatibility/migration.md#final-response-status-validation-neo-v2).
- Enable TCP_NODELAY on accepted managed TCP sockets to avoid delayed small
  writes while preserving protocol framing and writer batching. See the
  [controlled latency comparison](docs/project/http-tcp-latency.md).
- Add an isolated internal WebTransport over HTTP/3 session core (draft-ietf-webtrans-http3-16 framing, session association, capsules, flow control and bounded buffering) with unit coverage only. No listener advertises it, no public API or dependency changes; see [WebTransport session core development](docs/project/http-webtransport-core.md).
- Preserve committed managed HTTP/2 control output when the last admitted response
  completes during graceful drain. Stop input separately and wait for queued output;
  forced cancellation and drain deadlines remain. See the
  [lifetime evidence](docs/project/http2-drain-output-lifetime.md).
- Add the optional managed `IHttpResponseSections` capability for awaited informational responses and declared response trailers, preserving the existing response interface and documenting opt-in framing and ownership. This increment remains under validation.
- Reduce managed HTTP/3 per-request cost: connection-owned request cancellation
  scopes, one continuation per transport direction, fewer thread-pool hops,
  separate QPACK gates and one QUIC write per small frame, with final headers and
  a small first body write coalesced. A request read to its FIN no longer emits a
  QPACK Stream Cancellation, and an already received end of input is consumed
  instead of aborting the read direction. See
  [HTTP/3 request path performance](docs/project/http3-performance.md).
- Batch managed HTTP/2 output in wire order, coalesce initial headers with body
  data and retire streams when END_STREAM commits. Guard application cancellation
  callbacks so cleanup and connection shutdown continue after callback failures.
- Start managed HTTP/2 and HTTP/3 response-output stream closure without blocking
  synchronous Dispose, as explicitly approved. Context completion still awaits
  closure; see the [migration note](docs/compatibility/migration.md#multiplexed-response-stream-disposal-neo-v2).
- Reserve the bounded first-body prefix in managed HTTP/1 response-header buffers,
  preserving framing and write boundaries while avoiding fitting-header growth.
- Defer unused managed HTTP/1 query/item collections while preserving populated
  query initialization, stable mutable collections and request-local state.
- Commit bounded asynchronous managed HTTP/1 chunks with one transport write,
  preserving byte framing, immediate application writes and cancellation.
- Distinguish managed HTTP/1 request-head limit failures with 414 for oversized
  targets and 431 for oversized field sections, retaining limits and connection
  closure; see the [migration note](docs/compatibility/migration.md#http1-request-head-limit-statuses-neo-v2).
- Process valid higher HTTP/1 minor versions using HTTP/1.1 framing while retaining
  received request metadata and advertising only the supported response version.
  Preserve strict framing and HTTP/1.0/1.1 behavior; see the
  [migration note](docs/compatibility/migration.md#higher-http1-minor-versions-neo-v2).

- Remove the approved managed HTTP/1 keep-alive cap of 100 requests. Connections
  can remain reusable beyond that count; the 15-second idle timeout, explicit
  close, cancellation and graceful drain remain. The Keep-Alive header no longer
  advertises max. Clients relying on count-based rotation should request closure
  explicitly; see the [migration notes](docs/compatibility/migration.md#managed-http1-keep-alive-request-cap-removed).

- Prevent bodyless managed HTTP/1 responses from leaking payloads or chunk
  terminators, while preserving header commitment and explicit 304 representation
  lengths. Preserve accepted HTTP/2 responses during drain when a refused upload
  was already in flight, retaining connection flow credit and invalid-ID checks.

- Isolate oversized HTTP/3 request field sections to their streams, preserving
  healthy requests and QPACK state while retaining fatal compression-error checks.

- Return a stream-local 400 with the supported version for malformed HTTP/2
  WebSocket version negotiation instead of an application 500.

- Close unknown-length HTTP/1.0 response bodies to delimit them by transport EOF,
  while retaining persistence for fixed-length and bodyless responses.

- Reject malformed managed HTTP/1 request-target forms before URI reconstruction,
  including query-only and leading-@ targets. Valid origin paths, absolute HTTP(S)
  URLs and OPTIONS asterisk-form remain supported; see the migration notes.

- Reject new HTTP/2 requests that reuse a completed stream identifier or try to
  open a skipped lower identifier, while retaining minimal closed-stream
  processing and the advertised modern priority policy.

- Reject incomplete managed Content-Length bodies as errors rather than successful
  short uploads. Map body-framing failures to generic 400 responses before headers
  commit, and abort that connection after response commitment. Preserve ordinary
  application error handling and keep other clients' listener available.

- Preserve sibling HTTP/2 streams when a request is reset during response output.
  Cancel queued writes without poisoning the shared connection, return unsent
  DATA credit, and finish encoded HPACK blocks to keep peer tables synchronized.

- Keep recoverable HTTP/2 and HTTP/3 request-stream failures from disposing the
  listener shared by other clients. Response completion and context cleanup
  still run after cancellation or output failure; nonrecoverable errors retain
  their existing propagation behavior (engine program #181).

- Begin the owner-approved modern HTTP engine replacement: managed chunked request
  decoding, asynchronous body reads and draining, strict framing validation,
  pipeline buffer adoption and reduced response-header allocations. Malformed or
  ambiguous requests previously tolerated are rejected; see the
  [migration notes](docs/compatibility/migration.md#managed-http-framing-neo-v2)
  and [protocol roadmap](docs/project/http-engine.md). The development branch now
  includes HTTP/2, HPACK and RFC 8441 WebSockets, plus internal HTTP/3/QPACK/QUIC
  connection dispatch. Public HTTP/3 and combined listeners are now integrated.
  See the roadmap for target restrictions and outstanding conformance work.

- Reject malformed managed WebSocket frame metadata before consuming payloads,
  enforce minimal wire-length encoding, and reject lengths that cannot fit the
  current payload representation before integer conversion. Validate incoming
  close status codes and UTF-8 reasons before processing or echoing them (#190). See the
  [compatibility notes](docs/compatibility/migration.md#managed-websocket-framing-neo-v2).

- Enforce `WebSocketModule.MaxMessageSize` on the managed listener before payloads
  are buffered, complete the close handshake after rejecting oversized or invalid
  text messages, deliver messages received just before the peer's close, and stop
  failed close writes from escaping as unobserved task exceptions. Buffered frame
  reads, 64 KiB send frames and fewer copies raise managed echo throughput by
  about 8x for 64 KiB messages and 11x for 1 MiB messages in the documented
  benchmark. A bounded stateful fuzzer now runs in the Fuzz workflow (#190). See the
  [migration notes](docs/compatibility/migration.md#managed-websocket-limits-delivery-and-send-framing-neo-v2)
  and [benchmark](test/EmbedIO.Performance/README.md#managed-websocket-echo).

- Keep the managed WebSocket receiver active during local closing so valid peer acknowledgements complete promptly. Use asynchronous receive-completion signaling while preserving close payloads, cancellation and shutdown limits (issue #184).

- Remove compiler/analyzer suppressions and null-forgiving operators, correct nullable
  contracts and resource ownership, and enforce warnings as errors in ordinary and
  platform builds. CI checks formatting and rejects new suppressions. The owner-approved
  API migration uses `RawTarget`, `Validate.RoutePath`, and URI-valued URL APIs while
  retaining compatible string inputs where possible; see the
  [migration guide](docs/compatibility/migration.md#warning-free-api-cleanup-neo-v2-owner-approved).
  Recovery boundaries propagate process/resource corruption exceptions rather than
  treating them as ordinary request failures.

- Add the owner-approved opt-in ClientBanningModule with context criteria, ordinal client keys, permanent IPv4/IPv6 CIDR policies, live temporary/permanent controls and optional atomic file-backed permanent-ban persistence. Existing IP-banning APIs/defaults/targets/dependencies remain unchanged (upstream #438).

- Add the owner-approved WithBasicAuthentication fluent helper with explicit scope, configure-before-registration and optional realm, preserving existing authentication behavior and manual registration (upstream #439).

- Document existing streaming write-error controls, with reverse-proxy disconnect, compression and buffering compatibility coverage (upstream #457).

- Retain URL-prefix configuration and document endpoint binding, path routing and certificate ownership, with real listener compatibility coverage (upstream #464).

- Add owner-approved opt-in ILoggerFactory diagnostics forwarding in the optional DI package, with registration disposal and failure/recursion counters. Core APIs, defaults and dependency groups remain unchanged (upstream #475).

- Add opt-in SameSite controls for application and local session cookies, with existing defaults retained. Preserve independent raw cookie attributes and WebSocket response cookies, including mixed native typed/raw headers and long-lived expiration (upstream #479).
- Correct inherited suffix byte ranges: select final bytes, clamp oversized suffixes, reject zero suffixes, and ignore positive suffixes for empty files. Retain Int64 skip offsets for non-seekable resources beyond 2 GiB. Existing explicit-range, HEAD and validator policies remain; see suffix-range migration guidance (issue #170).

- Owner-approved compatibility change: malformed nonempty values rejected by supported controller route converters now return HTTP 400 instead of 500, before handler invocation. Preserve valid conversions, controller/configuration errors, optional/missing routes, query/body binding and transport policies; see the migration guide (issue #163, upstream #505 follow-up).

- Avoid ZIP archive reader thread-pool starvation on modern runtimes by using task-based synchronous gate waits; preserve archive serialization, cancellation, stream ownership and defaults (upstream #491 follow-up).

- Documented default JSON migration contracts and audited DTO fidelity, derived
  members, member exposure, timestamps, references, numeric tokens, formatting
  and depth. Added fourteen default-contract regressions including real HTTP
  preservation and failure recovery. Current serializer behavior, public APIs
  and dependencies are unchanged. Increased the full-suite time budget to five
  minutes after two coverage runs ended near three minutes with no failing
  assertions but incomplete case counts; the 1717-case minimum remains required.

- Serialize shared ZIP archive lookup/open and entry reads without whole-entry buffering. Preserve stream ownership/capabilities and cancellation, and wake pending archive operations during disposal (upstream #491).

- Preserve managed-listener bytes for pipelined requests, including successors after drained Content-Length bodies. Add isolated JSON/plaintext benchmark endpoints and protocol regressions (upstream #495).

- Restore reasonable upstream EmbedIO JSON behavior transparently: preserve raw string controls, accept trailing commas and lossless legacy number forms, bind quoted booleans/enum names/culture-valid dates/public properties with private setters, return legacy defaults for empty input, and serialize non-finite floating values as named strings. Keep explicit custom options, accurate Unicode/UTC handling and rejection of failed conversions/trailing garbage. See [JSON compatibility](docs/user-reports/json-migration-compatibility.md) for boundaries after SWAN removal; no production dependency or API/target change.

- Stop native queued/new WebSocket data and application callbacks after a valid close request, preserve invalid-parameter and secondary-close cancellation behavior, and observe caller cancellation during the managed acknowledgement wait. Keep the public state enum, listener defaults and existing shutdown policies unchanged (upstream #502).

- Serialize managed WebSocket messages/control-frame writes and native data/Windows close-output sends, honor send cancellation with safe partial-write cleanup, and validate IPv6/default-port prefixes and colon-bearing paths correctly after a selective upstream #534 audit. Preserve public APIs, listener defaults, targets and dependencies.

- Read managed response keep-alive defaults from the parsed request instead of caching them during context construction. Honor connection-close requests and HTTP/1.0 close-delimited responses, preserve explicit application overrides and response-helper ownership, and document one-writer chunked streaming (upstream #510).

## [1.0.3] - 2026-10-07

Patch release with native Basic-authentication and WebSocket lifecycle fixes,
plus the approved opt-in routing, MIME policy, pre-request and WebSocket-message
APIs. Existing defaults, package identities, legacy targets and production
dependencies are retained. See the user-report guides for each API and runtime
limitation. No breaking change is introduced.

- Add optional application-owned FileModule.MimeTypeProvider and awaited non-final PreRequestModule callbacks. Preserve local MIME overrides, fallback/compression negotiation, cache ownership, existing request data and defaults (upstream #521 follow-up).

- Add per-WebApiModule CaseInsensitiveRoutes for invariant-culture controller-route literals, preserving sensitive defaults/mounts and request data. Isolate matchers, reject conflicting case-equivalent declarations and deduplicate same-handler aliases (upstream #521).

- Set Basic authentication challenges through the native response API so Microsoft listeners on .NET Framework do not return 500 for valid credentials. Retain challenge/replacement and credential behavior; add real modern/legacy listener regressions (upstream #524).

- Document host-to-Android-emulator forwarding and extend API 29 CI with distinct host/device ports, remove/recreate checks and owned mapping cleanup; retain production APIs and defaults (upstream #536).

- Document and verify anonymous reads/protected writes in one controller using existing pre-handler hooks, separate authentication/permission checks and untouched JSON binding. Provide explicit demo-token and production-provider boundaries without adding authorization attributes or changing defaults/dependencies (upstream #538).

- Document and verify serving application-owned OpenAPI documents and optional local Swagger UI through existing APIs, including published 1.0.2 binding/validation/asset checks and browser GET/POST execution. Automatic generation is not implemented; production APIs and dependencies remain unchanged (upstream #539).

- Isolate IP-banning configuration, criteria, duration, bans and disposal by module instance. Add scoped TryBanClient/TryUnbanClient/BannedIPs controls while preserving route-wide static methods; keep surviving modules registered for purging and avoid retaining abandoned modules (upstream #545).

- Add opt-in WebSocketMessageModule with separate complete text/binary callbacks, strict UTF-8, per-connection ordering and documented rejection/error handling. Preserve legacy subclasses/frame callbacks and correct native close mappings for unsupported data (1003) and going away (1001). Correct a concurrent test-address allocation race exposed during validation (upstream #547).

- Verify existing SWAN removal and document an application-owned bridge to ILogger providers, with explicit filtering, exception-text and ownership limits and unchanged production dependencies (upstream #548).

- Document the modern SDK/legacy library target policy, published-package asset selection and consumer validation without removing legacy compatibility (upstream #549).

- Evaluate standalone utility packaging, document the current public/dependency inventory and utilities-only reuse, and retain existing assembly/package identities without production changes (upstream #550).

- Evaluate the fluent argument-validator proposal, document built-in .NET guards and legacy-compatible checks, and protect existing validation contracts without adding an API or dependency (upstream #551).

- Document custom trace destinations, bounded UI handoff and managed private-key HTTPS diagnostics for the Unity report, with real diagnostics/TLS regression coverage and unchanged production behavior (upstream #553).

- Document inbound versus outbound UWP/AppContainer loopback access, real HTML/API/session diagnostics and a scoped Windows isolation CI probe without changing production defaults (upstream #554).

- Avoid the Windows native HttpListener WebSocket full-close lock inversion by coordinating close-frame output and a single receiver through public .NET APIs. Preserve completed handshakes, close status/reason and cancellation; recheck against released .NET 11 before considering removal (#105).

- Retain WebSocket messages received before connection initialization completes, synchronize managed dispatch wake-ups, clean up failed initialization and emit one disconnection notification. Guard asynchronous managed callback errors without changing callback APIs or defaults (upstream #556).

- Document and verify early responses with unread request bodies, including the legacy HttpClient sequence and fixed-length connection reuse, without changing production defaults (upstream #558).

## [1.0.2] - 2026-10-05

Patch release with HTTP response framing, HEAD metadata, cookie, static-file and
listener lifetime fixes, plus the merged opt-in SSE and response-preparation APIs.
The five package identities, target frameworks and runtime dependencies are retained.

- Suppress HEAD response bodies and chunk bytes on both listeners while preserving stream validation and asynchronous completion. Synchronize native HEAD length metadata and last property/header assignment; preserve normal GET and connection-close behavior (upstream #564).

- Reduce chunk-prefix and charset-extraction allocations while preserving exact
  wire bytes and legacy attribute selection/errors. Add 42 compatibility cases,
  allocation budgets and chunked HTTP/HTTPS response verification.

- Omit `Content-Encoding: identity` when response negotiation selects no transformation, clearing stale coding metadata while retaining `Vary: Accept-Encoding`, compressed headers and existing negotiation results (upstream #566). Add real-listener media, range, cache and text/JSON regressions.

- Honor configured managed-response encodings and explicit charset parameters. Add the opt-in `FileModule.OnPrepareResponse` callback for per-resource headers before transmission, including cached, conditional and range responses (upstream #567). Preserve unconfigured defaults and file bytes.
- Preserve the selected range length when a static-file request populates a cold content cache; cached full-file bytes no longer overwrite the partial response length.

- Reduce managed listener body-drain and header serialization allocations while
  preserving request/response bytes, framing, cancellation and connection policies.
  Add POST benchmarks, allocation budgets and 26 focused compatibility cases.

- Make managed response close/dispose atomic and idempotent so stale disposal cannot close a newer keep-alive request. Add 15 response-ownership/concurrency cases and document JSON/binary response ownership and existing controller lifetime options.

- Drain synchronous managed socket accepts iteratively; preserve rearming and the
  macOS IPv6 worker. Add 42 framing, fragmentation, burst/reset and queued-accept
  cases. Document confirmed header/framing gaps without changing parsing policy.

- Release managed listener timers, transport wrappers and connection-owned buffers
  on terminal closure; preserve keep-alive and prevent reader restart during forced
  shutdown. Add 22 HTTP/HTTPS and WS/WSS lifetime cases and a dependency-free
  end-to-end benchmark with bounded cleanup verification in desktop CI.

- Open immutable ZIP archives with shared read-only access and release owned handles when archive validation fails. Add 13 regression cases covering multiple hosts/readers, read-only files, disposal and HTTP parity.
- Reduce managed listener queue snapshots and duplicated endpoint bookkeeping.
  Preserve replacement endpoint ownership during alias shutdown; isolate request
  bodies to Content-Length and keep connection disposal from disposing its listener.
  Preserve APIs, defaults and supported request/routing behavior; add measured queue
  allocation budgets and focused compatibility coverage.

- Use true asynchronous managed response writes with caller cancellation; preserve write-error policy. Repair premature chunk termination on empty writes and large-header body-prefix calculations. Add 42 regression cases for 1-6 MB files, compression/cache variants, action/JSON responses and backpressure.
- Avoid compiling unused parameterless base-route regexes during configuration.
  Signal synchronous startup readiness without polling; stop failed listeners,
  honor startup cancellation and enforce the documented single RunAsync call.
  Add 31 cold-start and port-reuse regression cases and fresh-process allocation budgets.

- Add MimeType.Xml and MimeType.TextXml constants and document opt-in controller XML responses using existing serializers; preserve default JSON behavior.
- Reduce content-negotiation, header/token parsing, WebSocket metadata and frame-read
  allocations. Avoid rate-history snapshots and preserve request records during
  concurrent purges. Keep existing thresholds, shared history ownership, parser
  behavior, APIs, defaults and dependencies; add 49 regression cases and CI budgets.

- Preserve pre-existing endpoint prefixes when a live alias registration fails; roll back only the prefixes actually added by that attempt.

- Register managed localhost prefixes on both enabled loopback families, with rollback and ownership-safe endpoint cleanup. Keep failed live additions retryable and reject duplicate ownership or incompatible HTTP/HTTPS endpoint registrations.
- Repair static-file cache eviction, replacement accounting and cleaner ownership.
  Reduce HTTP, routing, diagnostics and WebSocket allocations; remove dormant
  internal WebSocket compression paths while preserving compressed-frame rejection.
  Add 50 regression cases and reproducible allocation budgets in desktop CI.
  Public APIs, target frameworks, defaults and dependencies are unchanged.

- Emit configured response cookies during managed WebSocket upgrades (#64),
  preserving their HTTP attributes and separate header lines. Explicit response
  cookies take precedence over legacy request-cookie echoes of the same name;
  unrelated request cookies retain their existing behavior. Add 13 regression cases.

- Preserve separate `Set-Cookie` fields in managed WebSocket upgrade responses
  instead of comma-folding multiple cookies (upstream #583). Legacy request-cookie
  echo and other handshake-header behavior are unchanged.
- Added an opt-in SSE event writer that prepares headers, safely frames multiline
  UTF-8 events and heartbeat comments, flushes each frame and observes cancellation.
  Added a runnable progress server/browser guide for upstream #587. Existing APIs,
  listener defaults, target frameworks and dependencies are unchanged.
- Preserve configured HttpOnly and Secure cookie attributes with the Microsoft
  listener (#59), including late cookie configuration, empty responses and
  successful WebSocket upgrades. Add 22 real-listener regression cases.

- Ensure request-completion callbacks run after final response flush or close
  failures, including canceled streaming handlers (upstream #588). Document SSE
  framing, server cancellation and transport-error handling without changing
  listener defaults or treating client disconnects as server cancellation. Reuse
  the native response stream and close idempotently so Unix response disposal
  does not escalate into fatal listener cleanup.
- Fixed query-data parsing overwriting cached form data within a request (#57).
- Fixed WebSocket negotiation when clients offer multiple subprotocols in one
  header value (#58).
- Fixed static-file HEAD response framing: known representation lengths are
  preserved, and unknown compressed lengths are omitted (#60).
- Fixed native Unix WebSocket completion reacquiring a disposed HTTP response
  stream and shutting down the listener (#62).
- Added 106 regression and boundary cases for listener adapters, WebSocket
  messages, HTTP modules, request binding/bodies, sessions, files and utilities.

## [1.0.1] - 2026-10-05

Patch release with managed-listener shutdown, HTTPS and macOS accept fixes.
The four existing packages retain their identities and target frameworks; the
optional `EmbedIO-Neo.DependencyInjection` package joins the release family.

- Added required native MAUI HTTPS fixtures for Windows, iOS, Mac Catalyst and
  Android, with disposable CA provisioning, normal client/WebView trust checks,
  external HTTPS verification and a reusable physical-device probe for feature #26.
  Simulator/emulator results do not establish legacy Xamarin or physical-device support.

- Mitigated macOS IPv6 accept-completion process crashes after immediate client
  resets (#38). A dedicated background accept worker catches invalid peer-address
  errors and continues accepting, while request/TLS I/O remains asynchronous.
  macOS IPv6 endpoints use one additional thread each; other endpoints retain
  asynchronous accept. Added reset stress and worker shutdown regressions.

- Fixed managed-listener HTTPS requests reporting HTTP URLs (upstream #593).
  Request URL reconstruction now preserves absolute-target path/query case and
  bracketed IPv6 hosts without a port; added real HTTP/TLS regressions and
  platform HTTPS URL-scheme validation.

- Added optional `EmbedIO-Neo.DependencyInjection` integration with per-request
  scopes, constructor and explicit handler-argument injection, awaited resource
  cleanup, and Generic Host lifecycle support. Existing registration APIs and
  core dependency requirements are unchanged. See
  `docs/architecture/dependency-injection.md` for setup, ownership, and limits.

- Fixed internal listener stop/dispose leaving pending accepts and missing Stopped
  notifications; shutdown now drains stale queued contexts without spinning.
  Added cancellation, queue concurrency, disconnect, and two-server regressions
  plus Android two-listener load/lifecycle coverage for upstream #595. The original
  device stall cause is not confirmed.

- Moved managed-listener TLS authentication out of the socket accept callback
  and under the first-request timeout, with pending handshakes tracked for shutdown.
  Added six desktop HTTPS regression cases, Android and Mac Catalyst HTTPS smoke
  coverage, and certificate/trust guidance for feature #26. Platform execution
  evidence and remaining MAUI app-model work are tracked in that feature.

- Added MAUI Android lifecycle and supervised-background-work guidance for upstream
  #597, four real-listener regressions, and a required Android 10 emulator smoke.
  Production behavior is unchanged; the original application crash has no supplied
  reproduction or stack trace.

- Grouped documentation into guides, platforms, compatibility, and project
  folders, with updated navigation and redirects for existing links.

- Organized documentation with a central index and CLI/migration guides under
  docs; retained redirects for previous entry points and removed unused legacy
  branding and IDE settings.

- Added asynchronous outbound HTTP guidance and six real-listener integration
  cases for upstream #598. The original empty-response diagnosis still requires
  a complete reproduction; production behavior is unchanged.

- Documented multiple static-folder mounts and explicit missing-file pass-through
  for upstream #599, with eight regression cases. Routing defaults are unchanged.

- Added upstream #601 regression coverage and MAUI Mac Catalyst startup guidance.
  The reported SWAN Console.WindowHeight failure path was already removed in
  1.0.0. The sandboxed MAUI Mac Catalyst HTTP/WebView smoke passed in CI;
  confirmation on the reporter's original environment remains outstanding.
- Documented opt-in .NET JSON cycle handling for upstream #600 and added eight
  response-serialization regression cases. Existing default behavior is unchanged.

## [1.0.0] - 2026-10-04

First independent Neo release, continuing Unosquare's EmbedIO, Extras JsonServer,
and CLI. Thank you to Unosquare, Mario A. Di Vece, Giovanni Perez, and the original
contributors for the foundation we can maintain and build upon. Original MIT
copyright and embedded third-party notices remain in the source and packages.

The package family is `EmbedIO-Neo`, `EmbedIO-Neo.JsonServer`,
`EmbedIO-Neo.Testing`, and `EmbedIO-Neo.Cli`. Existing assembly names, namespaces,
and the `embedio-cli` command are retained. Neo's version sequence starts at
1.0.0 independently of upstream's 3.x versions.

- Consolidated EmbedIO, JsonServer and the CLI into one repository.
- Added .NET 10 while retaining .NET Standard 2.0 library targets.
- Removed SWAN with approval; see MIGRATION.md for source and binary changes.
- Ported the CLI with approved plugin migration; see CLI.md.
- Added locked builds and the repository-standard CI, security, malware scan,
  update and release-preview workflows.
- Escaped carriage returns and newlines in trace output to prevent request data
  from forging additional log records; diagnostic observers retain the original data.
- Added daily URL path/query fuzzing with a committed corpus and signed package
  provenance through GitHub attestations in the release workflow.

### Approved migration changes

SWAN removal changes configuration, JSON options, logging and exception APIs.
Rebuild downstream applications and follow [MIGRATION.md](docs/compatibility/migration.md), including
validation of JSON client contracts. Archived EmbedIO 2.x CLI plugins must be
rebuilt and adapted as described in [CLI.md](docs/guides/cli.md#provenance-and-migration).
These migrations were approved before implementation; this release adds no
further runtime behavior changes.

### Validation and limits

The 367-case regression suite passes on Windows, Linux and macOS with .NET 10.
Locked package builds, security and malware scans, and the release preview are
required before publication. .NET Standard 2.0 is retained; older runtimes and
.NET 8 have not received equivalent runtime validation. Inherited analyzer
warnings remain. Fuzzing covers URL paths and query data, not listener or
WebSocket-frame parsing. JsonServer's persistence limitations are documented in
CONTRIBUTING.md. No measured performance improvement is claimed.

The detailed initial baseline is recorded in docs/embed_io_neo.md.
