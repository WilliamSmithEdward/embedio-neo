# Upgrading from EmbedIO 3.x

This page is for applications built on the archived
[unosquare/embedio](https://github.com/unosquare/embedio) library, version 3.x,
that are moving to EmbedIO-Neo v2. It lists each step in order and links to the
detailed guide for that step. Two guides hold the details:

- The [general migration guide](migration.md) covers the move from upstream
  EmbedIO to Neo: package names, SWAN removal and JSON. Neo v1 made those
  changes, so an upstream application meets them now.
- The [v1-to-v2 migration guide](neo-v1-to-v2.md) covers the new HTTP engine in
  v2: listener changes, request limits, stricter framing and response behavior.

Namespaces, assembly names and the fluent `WebServer` pipeline are unchanged.
Controllers, routing attributes, `FileModule`, `WebSocketModule` and custom
`WebModuleBase` modules carry over in source. A simple application usually needs
the package swap, a rebuild and testing; most of the work is in the SWAN, listener
and behavior items below.

These steps target EmbedIO-Neo 2.0.0 or later; the 1.0.x packages are Neo v1.

## 1. Swap the packages

- [ ] Replace `EmbedIO` with `EmbedIO-Neo`. The other packages are
      `EmbedIO-Neo.Testing`, `EmbedIO-Neo.JsonServer`,
      `EmbedIO-Neo.DependencyInjection` and the `EmbedIO-Neo.Cli` tool.
- [ ] Target .NET 10 for HTTP/2 over TLS and for HTTP/3. Older frameworks use
      the .NET Standard 2.0 asset, which serves HTTP/1.1 and cleartext HTTP/2.
- [ ] Rebuild everything that references EmbedIO, including your own libraries
      and CLI plugins. Neo is not binary-compatible with 3.x. See
      [API replacements](migration.md#api-replacements) and the
      [CLI migration](../guides/cli.md#provenance-and-migration).

## 2. Remove SWAN

EmbedIO 3.x depended on Unosquare SWAN. Neo removes that dependency.

- [ ] Replace each SWAN type from the
      [API replacement table](migration.md#api-replacements), for example
      `Swan.Formatters.Json` with `EmbedIO.Serialization.Json` and
      `Swan.Configuration.ConfiguredObject` with
      `EmbedIO.Configuration.ConfiguredObject`.
- [ ] Move logging to `EmbedIO.Diagnostics.Log.Source`, a standard
      `TraceSource`. See [logging and configuration](migration.md#logging-and-configuration),
      or forward it to `ILogger` with the
      [logging provider guide](../user-reports/logging-provider-integration.md).
- [ ] Remove the SWAN package reference if nothing else uses it. SWAN extension
      methods are no longer available through EmbedIO.

## 3. Check JSON

Serialization now uses System.Text.Json, with defaults that keep the legacy
tolerances: case-insensitive names, public fields, quoted numbers, trailing
commas and similar. No opt-in is needed.

- [ ] Run your request and response contract tests. The
      [JSON section](migration.md#json) lists what changed, such as numeric
      token types, timestamps, circular references and derived members.
- [ ] Review request types for over-posting: public fields and properties with
      private setters bind from the request body. See
      [over-posting](../user-reports/json-migration-compatibility.md#over-posting-and-the-binding-surface).

## 4. Choose a listener

- [ ] Remove `HttpListenerMode.Microsoft` and `WithMicrosoftHttpListener()`.
      The HTTP.sys backend is gone, so URL ACL reservations and kernel
      certificate bindings no longer apply. Supply the HTTPS certificate to
      EmbedIO directly. See
      [listener selection and deployment](neo-v1-to-v2.md#listener-selection-and-deployment).
- [ ] Choose `EmbedIO` (HTTP/1.1 and HTTP/2 over TCP), or opt in to HTTP/3 with
      `EmbedIOHttp3` or `EmbedIOCombined` and the [HTTP/3 guide](../guides/http3.md).
- [ ] Make sure HTTPS clients, proxies and gateways use TLS 1.2 or later. See
      [minimum TLS version](neo-v1-to-v2.md#minimum-https-tls-version).

## 5. Fix source changes

- [ ] Replace `IHttpRequest.RawUrl` with `RawTarget`, and update code that
      expected a string from `Validate.Url`, `RedirectModule.RedirectUrl` or
      `ITestWebServer.BaseUrl`; they return `Uri`. See
      [rebuild and update source references](neo-v1-to-v2.md#rebuild-and-update-source-references).
- [ ] Review the [API cleanup notes](migration.md#warning-free-api-cleanup-neo-v2-owner-approved)
      for renamed parameters, `Uri` overloads and nullability annotations.
- [ ] Update custom `IHttpContext`, request or response implementations. The
      `SystemHttpRequest` and `SystemHttpResponse` adapters are removed.

## 6. Review behavior that still compiles

These changes do not break the build but can change what clients see.

- [ ] **Request limits.** Bodies default to 30,000,000 bytes, headers to 100
      fields and 32 KiB, and WebSocket messages to 4 MiB. Raise them where your
      application needs more. Set a body read timeout for servers that face
      untrusted clients. See [request limit defaults](neo-v1-to-v2.md#finite-request-limit-defaults).
- [ ] **Unhandled exceptions** return an empty 500 instead of an HTML page. See
      [empty 500](neo-v1-to-v2.md#unhandled-exceptions-return-an-empty-500).
- [ ] **Malformed requests** are rejected rather than normalized, and chunked
      uploads are decoded. See [request framing](neo-v1-to-v2.md#request-framing-and-client-behavior).
- [ ] **Response fields** containing control characters, and invalid reason
      phrases, now throw. See
      [field validation](neo-v1-to-v2.md#response-field-and-reason-phrase-validation).
- [ ] **Encoded separators** such as `%2F` no longer select nested files. See
      [encoded separators](neo-v1-to-v2.md#encoded-separators-in-served-paths).
- [ ] **Streaming responses**: flushing sends headers, so set status and headers
      before the first flush. See
      [response flushing](neo-v1-to-v2.md#direct-http1-response-flushing).

## 7. Check WebSockets

- [ ] Set `MaxMessageSize` for your messages; the default is now 4 MiB.
- [ ] Check close codes passed to `CloseAsync`, which now validates them, and
      test how sends behave while a socket closes. See
      [WebSocket closes and keep-alive](neo-v1-to-v2.md#websocket-closes-sends-and-keep-alive)
      and [WebSocket applications](neo-v1-to-v2.md#websocket-applications).

## 8. Test before deploying

- [ ] Follow [validate an application upgrade](neo-v1-to-v2.md#validate-an-application-upgrade):
      routes, authentication, sessions, static files, uploads and WebSockets,
      with your real clients and reverse proxy.
- [ ] Skim the remaining sections of the [v1-to-v2 guide](neo-v1-to-v2.md) for
      features you use, such as cookies, shared HTTPS endpoints or IP banning.
