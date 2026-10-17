# Source layout

The source folders group code by responsibility. Folder names do not define new
namespaces or assemblies: existing `EmbedIO`, `EmbedIO.Internal` and
`EmbedIO.Net.Internal` type identities remain unchanged.

| Folder | Responsibility |
| --- | --- |
| `src/EmbedIO/Health` | Optional lifecycle/dependency health checks, readiness waits and health endpoint module. |
| `src/EmbedIO/Hosting` | Server lifecycle, listeners, options and server extension methods. |
| `src/EmbedIO/Http` | Public request/response contracts, helpers, headers, MIME types and HTTP policies. |
| `src/EmbedIO/Modules` | Module contracts, composition and registration extension methods. |
| `src/EmbedIO/Authentication` | Credential validation modules and bearer callbacks. |
| `src/EmbedIO/Authorization` | Controller access requirements and named policy evaluation. |
| `src/EmbedIO.Jwt` | Optional explicit-key JWT validation; IdentityModel dependencies stay in this package. |
| `src/EmbedIO/Tunnels` | Public tunnel, capsule and datagram contracts. |
| `src/EmbedIO/Internal/RequestBodies` | Request stream wrappers, decoding, accounting and policy enforcement. |
| `src/EmbedIO/Internal/Responses` | Response streams, write ownership, commitment and cookie metadata. |
| `src/EmbedIO/Internal/Compression` | Shared compression primitives. |
| `src/EmbedIO/Internal/Diagnostics` | Internal request and server telemetry. |
| `src/EmbedIO/Internal/Modules` | Internal module composition and control flow. |
| `src/EmbedIO/Internal/Conversion` | Value and MIME conversion helpers. |
| `src/EmbedIO/Net/Internal/Http1` | HTTP/1 framing, adapters and upgrade output. |
| `src/EmbedIO/Net/Internal/Http2` | HTTP/2 frames, streams and flow control. |
| `src/EmbedIO/Net/Internal/Http3` | HTTP/3 and native QUIC transport. |
| `src/EmbedIO/Net/Internal/Transport` | Shared TCP listener, endpoint, connection and TLS lifetime. |
| `src/EmbedIO/Net/Internal/Multiplexed` | Shared HTTP/2 and HTTP/3 request/response adapters. |
| `src/EmbedIO/Net/Internal/Headers` | Shared field parsing, framing metadata and cookie serialization. |
| `src/EmbedIO/Net/Internal/Tunnels` | Internal capsule transport. |
| `src/EmbedIO/Net/Internal/WebTransport` | WebTransport session core. |
| `src/Shared` | Source files explicitly linked into multiple projects, including request-abort token ownership shared by core and in-process testing. |

Existing feature folders, such as `Files`, `Routing`, `Sessions`, `WebApi` and
`WebSockets`, retain their boundaries. General internal helpers remain directly
under `Internal`. The companion projects retain their existing project and
package identities. `Shared` is a source directory, not a new project or package.

Keep partial declarations together and use `TypeName.Subject.cs` for their file
names. For example, the HTTP request extensions live together in `Http`, and all
`HttpConnection` partials live in `Net/Internal/Transport` even when a partial
implements one protocol's connection loop.

When moving source files, update explicit linked-source includes, workflow path
filters, comparison scripts and current documentation links. Historical evidence
manifests retain the paths belonging to their recorded commit. Git rename
history connects those paths to the current layout.

This reorganization moves or renames 186 files without changing any of the 434
production C# files' contents. Namespaces, public signatures, runtime behavior,
target frameworks and dependency groups are preserved. Debugger and Source Link
paths naturally follow the new physical locations.

Request-abort observation is lazy: public capability/extension contracts live in
`Http`, controller binding in `WebApi`, request ownership in
`Internal/RequestAbortScope.cs`, and the HTTP/1 socket observer in
`Net/Internal/Transport/Http1RequestAbortObserver.cs`.
