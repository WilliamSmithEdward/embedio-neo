# Migrating to EmbedIO-Neo 1.0.0

William approved full SWAN removal on October 4, 2026. This is a source and
binary breaking change. Rebuild downstream applications and libraries against
this fork. Neo starts its independent version sequence at 1.0.0; this is not a
compatible upstream 3.x update.

## API replacements

Neo's approved NuGet identities are `EmbedIO-Neo`, `EmbedIO-Neo.JsonServer`,
`EmbedIO-Neo.Testing`, and `EmbedIO-Neo.Cli`. Replace corresponding archived
package references when adopting Neo. Assembly names, namespaces and the CLI
command remain unchanged. Neo packages start at version 1.0.0.

| Previous dependency/API | Replacement |
| --- | --- |
| `Swan.Configuration.ConfiguredObject` | `EmbedIO.Configuration.ConfiguredObject` |
| JSON `SerializerOptions` / `JsonSerializerCase` overloads | `System.Text.Json.JsonSerializerOptions` |
| `Swan.Formatters.Json` | `EmbedIO.Serialization.Json` or `System.Text.Json.JsonSerializer` |
| SWAN logging configuration | `EmbedIO.Diagnostics.Log.Source` (`TraceSource`) |
| SWAN internal-error exception surfaced by EmbedIO | `EmbedIOInternalErrorException` |

Remove the SWAN package reference if your application no longer uses it directly.
SWAN extension methods and attributes are no longer available transitively.
The .NET 10 core requires no runtime NuGet packages. The .NET Standard 2.0 build
uses Microsoft's pinned System.Text.Json package and its support dependencies.
Build analyzers and test tools remain. The console sample has been removed, including its Tubular, Dynamic LINQ, and frontend dependencies. The legacy Xamarin sample and its platform/WebView dependencies have also been removed. Both .NET Standard 2.0 and .NET 10 library targets are retained.

## JSON

Default HTTP serialization now uses System.Text.Json. EmbedIO's defaults enable
public fields, case-insensitive property matching, and reading quoted numbers.
Untyped objects become dictionaries, arrays become lists, and numbers become
`decimal`, preserving the shapes used by JsonServer. Numeric and boolean JSON
values can still be read into string properties. Default Neo deserialization also
preserves reasonable legacy EmbedIO acceptance: raw controls inside strings,
trailing commas, lossless leading-plus/zero/decimal-point number forms, quoted
booleans, named enums, culture-valid dates and public properties with private
setters. Empty/whitespace input returns the target's default value. Non-finite
floating-point values use named JSON strings. These defaults require no opt-in
and apply to `[JsonData]`, `GetRequestDataAsync<T>` and JsonServer through
`EmbedIO.Serialization.Json`. Unrelated malformed input and failed value
conversions still return 400 in request binding. See the maintained
[JSON compatibility guide](../user-reports/json-migration-compatibility.md) for
verified behavior and deliberate exceptions; arbitrary SWAN tolerance and silent
conversion failures are not reproduced.

Use `Json.CreateOptions()` to retain these defaults when customizing:

```csharp
using EmbedIO;
using EmbedIO.Serialization;
using System.Text.Json;

var options = Json.CreateOptions();
options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
var serialize = ResponseSerializer.Json(options);
var deserialize = RequestDeserializer.Json<MyRequest>(options);
```

Explicitly supplied options replace the defaults. A fresh
`new JsonSerializerOptions()` retains strict System.Text.Json parsing; options
created by `Json.CreateOptions()` retain Neo's compatibility behavior even when
copied with the .NET options copy constructor. Directly calling the .NET parser
with those options does not normalize raw string controls or legacy number
syntax; that normalization belongs to Neo's `Json.Deserialize` entry points.
Application converters added to `Json.CreateOptions()` take precedence over
its boolean, enum and date defaults. Replace SWAN JSON attributes
with System.Text.Json attributes such as `JsonPropertyName` and `JsonIgnore`.
Use converters for application-specific enum, date, and number representations.
Escaping, whitespace, date formatting, constructor selection, unsupported types,
and error details follow System.Text.Json rather than SWAN. Scalar JSON roots
are now accepted. Untyped numbers outside decimal range throw `JsonException`.
Direct JSON utility failures use `JsonException` rather than `FormatException`.
Do not assume byte-for-byte JSON compatibility: validate stored documents and
client contracts, especially custom SWAN attributes or serializer options.

### Nesting depth and database nulls

An acyclic response graph can still exceed the serializer's nesting limit. In
the audit, a chain constructed with 32 `Next` links failed under SWAN but
succeeded under Neo. Chains with 64 and 70 links failed under both; an eight-link
chain succeeded under both. These fixture results are not universal maximum
object counts: JSON arrays and objects contribute to nesting, and the location
of a value within the response affects its depth.

Neo retains System.Text.Json's depth policy. Its default
[`MaxDepth`](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth?view=net-10.0)
setting of zero uses the serializer's effective limit of 64. If deeper nesting is an
intentional API requirement, configure an explicit `MaxDepth` on the serializer
options and validate the complete payload. Increasing the limit does not resolve
an actual cycle. Prefer deliberately bounded response DTO graphs so both server
and client can process their intended structure reliably.

Database null mapping is a separate caveat, not a new Neo regression. In the
audit, `DBNull.Value` serialized as `{}` under both serializers, rather than JSON
`null`. Normalize database nulls to CLR `null` when preparing a nullable response
member. Do not pass `DBNull.Value` through an object-typed DTO property expecting
the serializer to infer the intended JSON null.

These findings complete the verified default-serialization compatibility topics
in the [audit](../user-reports/default-json-preservation-audit.md). They do not
establish actual database/ORM or client behavior beyond the documented fixtures.
Serializer defaults remain unchanged.

### JSON text, escaping, and exact bytes

Neo retains System.Text.Json's formatting and escaping. Upstream and Neo can
produce different JSON text while preserving identical parsed values. For
example, the audited serializers represent the same string differently:

| Serializer | JSON string |
| --- | --- |
| Upstream SWAN defaults | `"café <>&"` |
| Neo defaults | `"caf\u00E9 \u003C\u003E\u0026"` |

After JSON parsing, both values are `café <>&`. Neo also uses compact spacing
where the audited SWAN output included spaces. Member ordering can differ;
JSON object member order is not the same contract as JSON array element order.
These textual differences do not imply changed string values or reordered list
elements. Parse JSON before interpreting escape sequences.

Update migration checks and response snapshots to compare parsed member values
when the contract concerns data. Continue checking array order, member presence,
nulls, and numeric precision; ignoring formatting must not conceal a real data
change. Assertions requiring string versus number tokens must also preserve that
distinction.

If raw text or bytes are part of the contract, specify the serialization format
explicitly. Hashes, signatures, and caches keyed by response-body bytes can change
even when parsed data is equal. Define the relevant encoding, spacing, escaping,
member ordering, and numeric/date representations, and use a defined
canonicalization procedure where required by a signing protocol. Do not assume
that enabling indentation or changing an encoder restores upstream byte identity.

The audit observed repeatable output for unchanged fixtures on the tested
runtime, including identical DTO response bodies across repeated HTTP requests.
That does not guarantee identical bytes across serializer or runtime upgrades.
Keep exact-byte checks when they are a real application requirement and validate
the required format against the actual producer and consumer.

These are existing serialization migration differences. See the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for comparison evidence and limits; serializer defaults remain unchanged.

### URI, date-only, and time-only values

Neo retains System.Text.Json's string representations for these supported
framework values. In the audit, SWAN reflected them into JSON objects with
multiple properties, while Neo emitted strings:

| Value | Audited upstream shape | Neo response example |
| --- | --- | --- |
| `Uri` | Object with properties such as `AbsoluteUri`, `Host`, and `Scheme` | `{"Link":"https://example.com/a?q=1\u0026b=2"}` |
| `DateOnly` | Object with properties such as `Year`, `Month`, and `Day` | `{"Date":"2026-10-07"}` |
| `TimeOnly` | Object with properties such as `Hour`, `Minute`, and `Second` | `{"Time":"12:34:56"}` |

Clients must parse the string instead of accessing nested members such as
`Date.Year` or `Link.Host`. The escaped `\u0026` in the URL is an ampersand after
JSON parsing; it does not change the URL's value. Parse JSON before interpreting
the URL, date, or time. Time-only values can include fractional seconds when
present, so do not assume a fixed string length.

Date-only and time-only values do not carry a timezone or UTC offset. Do not
silently interpret them as complete timestamps. If clients require structured
members, map the intended components to a concrete response DTO explicitly.
Likewise, a URI DTO can expose selected components when the API contract calls
for them rather than relying on reflection over the framework type.

These are verified serializer comparisons on a Windows .NET 10 host, not a
claim that all historical upstream runtimes produced the same reflected object.
Reflection-visible framework members vary with the runtime; `DateOnly` and
`TimeOnly` did not exist on older runtimes. Their availability also depends on
the consumer's target framework. See the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for baseline and validation limits. No serializer defaults are changed here.

### Numeric token types and client precision

Neo retains JSON-number output for supported finite numeric values. Upstream
SWAN emitted strings for the audited `double.MaxValue` and `double.Epsilon`;
Neo emits numeric tokens instead:

| Value | Audited upstream JSON | Neo JSON |
| --- | --- | --- |
| `double.MaxValue` | `"1.7976931348623157E+308"` | `1.7976931348623157E+308` |
| `double.Epsilon` | `"5E-324"` | `5E-324` |

Clients expecting a string must accept the new numeric token or the application
must explicitly retain a string representation in its response contract. Do not
generalize the two observed cases to every floating-point value: conventional
finite values were numbers in both serializers in the audit. NaN and infinities
are a separate case covered under
[payloads that now fail serialization](#payloads-that-now-fail-serialization).

Server serialization and client parsing are separate precision boundaries. The
audited .NET HTTP client preserved `long.MaxValue`, decimal values including
decimal scale, and UTC timestamp ticks. The direct serializer comparison also
round-tripped the two finite double boundaries through .NET JSON parsing. These
checks do not prove that every client retains the same values.

For example, a JavaScript client using ordinary `Number` values cannot represent
every signed 64-bit integer or every .NET decimal exactly. Sending a correct JSON
numeric token therefore does not by itself guarantee exact application values
after parsing. This is a client-contract consideration, not a newly detected
transport defect or a regression unique to Neo.

For IDs that require exact large-integer preservation, or decimal values that
require exact precision, agree on a representation supported by the client.
Options include strings with a documented format and typed parsing, or a client
parser that retains the required numeric precision. If strings are selected,
format them explicitly using invariant culture so deployment locale does not
change decimal separators or other numeric text. Specify the intended scale
where it matters, and test the actual client against representative boundaries.

These recommendations do not change default numeric serialization. See the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for comparison scope and evidence.

### Payloads that now fail serialization

Neo retains explicit System.Text.Json failures for the following payloads,
rather than upstream SWAN's tolerant representations. Review response preparation
when migrating: serialization failures can turn an otherwise successful endpoint
into HTTP 500.

| Payload | Audited upstream behavior | Neo defaults | Recommended migration |
| --- | --- | --- | --- |
| Property getter that throws | Property omitted | Getter exception propagates | Map to a DTO with reliable stored values; handle retrieval failures before serialization |
| Multidimensional array such as `int[,]` | Flattened JSON array | `NotSupportedException` | Map to a flat or nested collection deliberately |

Current-main HTTP regression checks returned 500 for throwing getters, unsupported arrays, cycles and excessive depth, then successfully served
the next valid request. This describes the tested default response path; custom
exception handlers can choose another response. Do not rely on an exception's
exact message as a client contract.

Avoid database-backed or computed getters that can fail during serialization.
Materialize the needed values and handle expected failures while preparing the
response DTO. Do not silently convert a retrieval failure to missing data unless
that behavior is explicitly part of the endpoint's contract.

Current main emits NaN and infinities as named JSON strings
(`"NaN"`, `"Infinity"`, and `"-Infinity"`), restoring upstream compatibility.
The older checkout used for the original audit rejected them. Fresh strict
`JsonSerializerOptions` still reject them; see
[JSON compatibility](../user-reports/json-migration-compatibility.md).
JSON numbers cannot represent these values. For an optional numeric result,
an application can deliberately map a nonfinite value to `null`. This is a
partial mapping snippet; it replaces the value assignment in an application
that has decided `null` means unavailable:

```csharp
double? responseValue = double.IsNaN(value) || double.IsInfinity(value)
    ? (double?)null
    : value;
```

If clients need to distinguish NaN from either infinity, use an explicit state
member, a documented string representation, or a custom converter instead.
Do not use `null` where it would erase a meaningful distinction. An explicit
System.Text.Json configuration can also permit named floating-point literals
as strings; clients must support that change in JSON token type.

For a matrix whose row structure matters, map to a jagged array or list of rows
so `{{1,2},{3,4}}` becomes `[[1,2],[3,4]]`. If the existing client contract expects
upstream's flattened `[1,2,3,4]`, flatten explicitly instead. The migration must
select the intended structure; the serializer should not guess it.

These recommendations document existing migration differences and do not change
the serializer or introduce a global error-suppression policy. See the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for evidence and validation limits.

### Shared references and circular references

Neo retains its default reference behavior rather than SWAN's `$circref`
markers. A repeated reference is not necessarily a cycle: two list entries can
point to the same object without that object pointing back to itself or an
ancestor.

For example, if `row` has `Id = 42`, serializing `new[] { row, row }` with Neo
defaults produces:

```json
[{"Id":42},{"Id":42}]
```

In the audited upstream SWAN defaults, the later occurrence instead became a
`$circref` object. Its marker value is not a stable application ID. Neo repeats
the value at each occurrence; JSON clients do not receive or recover the original
shared CLR object identity by default. Distinct objects with equal values are
also serialized as separate values.

An actual cycle is different. For example, an object whose `Next` points back
to itself, or an entity graph such as `Customer -> Orders -> Customer`, cannot be
represented by endlessly expanding values. Upstream emitted a `$circref` marker
in the audited self-cycle case. Neo defaults throw `JsonException`; the audited
default HTTP response path returns 500. The server still serves subsequent valid
requests in the regression checks.

For database endpoints, project entities into concrete response DTOs without
back-reference navigation properties. Include a related record's ID or a
deliberately bounded nested DTO where needed. This makes the response graph and
its contents explicit.

If an endpoint intentionally returns an object graph, choose a reference policy
explicitly in response `JsonSerializerOptions` and pass those options to
`ResponseSerializer.Json(options)`. Follow the circular-reference guide's fresh
response-options example, particularly for object-typed members or dictionaries;
custom converters can affect reference tracking.

- `ReferenceHandler.IgnoreCycles` writes `null` for a cyclic reference. It does
  not preserve the omitted relationship; shared references outside the current
  traversal path still serialize as repeated values.
- `ReferenceHandler.Preserve` uses System.Text.Json reference metadata such as
  `$id`, `$ref`, and `$values`. This changes the JSON contract, including collection
  shapes, and requires client support. It is not the old SWAN `$circref` format.

These are opt-in policies, not changes to the defaults. See the maintained
[circular-reference guide](../guides/json-circular-references.md) for
configuration examples and the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for comparison evidence and limits.

### Timestamp representations

Default timestamp serialization follows System.Text.Json rather than SWAN. Neo
retains this behavior; clients migrating from upstream must review timestamp
parsing and any assumptions about JSON member types or fixed string lengths.

For the audited UTC `DateTime` with seven fractional-second digits, the response
changes as follows:

| Serializer | JSON timestamp |
| --- | --- |
| Upstream SWAN defaults | `"café <>&"` |
| Neo defaults | `"2026-10-07T12:34:56.1234567Z"` |

Neo preserves the tested fractional seconds and marks the UTC value with `Z`.
Fractional digits vary with the value; they are not always present. A `DateTime`
with `Kind.Unspecified` does not acquire a UTC marker: the tested value becomes
`"2026-10-07T12:34:56.1234567"`. Set the intended `DateTime.Kind` in the database
mapping rather than assuming a timestamp without an offset means UTC. Do not
relabel local time as UTC without performing the intended conversion.

`DateTimeOffset` changes JSON shape as well as formatting. In the audit, upstream
SWAN produced an object containing members such as `DateTime`, `UtcDateTime`,
and `Offset`. Neo produces one ISO timestamp string. For a value at
`2026-10-07 12:34:56` with offset `+02:00`, Neo returns:

```json
{"Offset":"2026-10-07T12:34:56+02:00"}
```

Clients must parse that string instead of reading nested date/time members.
Accept ISO timestamps with optional fractional seconds and explicit offsets;
avoid substring-based parsing and exact string-length assumptions. In .NET,
deserialize into `DateTimeOffset` when the contract needs an explicit offset,
or into `DateTime` with an agreed interpretation of its kind.

The source value's precision is distinct from the client's precision. The audited
.NET HTTP client retained the UTC kind and timestamp ticks; other clients may
retain fewer fractional digits. Verify the client's behavior if submillisecond
precision matters. If an existing API requires a particular textual format,
select that representation explicitly in a response DTO or custom converter and
document its timezone and precision semantics.

These are existing SWAN migration changes, not a new serializer modification.
See the [default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for the tested runtime and validation limits.

### Ignored members, renamed members, and public fields

SWAN member attributes do not control Neo's System.Text.Json serializer. Replace
them explicitly when migrating; an old ignore attribute does not prevent a
property from appearing in a response. Old rename attributes also stop applying,
so client-visible keys can change back to the CLR member names.

For example, this legacy class uses SWAN attributes (legacy snippet; requires
the old SWAN package):

```csharp
public sealed class CustomerResponse
{
    [Swan.Formatters.JsonProperty("customer_id", false)]
    public int Id { get; set; } = 42;

    [Swan.Formatters.JsonProperty("InternalNotes", true)]
    public string InternalNotes { get; set; } = "Staff only";

    public string InternalCode = "INTERNAL-42";
}
```

In the audited upstream defaults, its JSON was `{"customer_id":42}` (ignoring
whitespace). Neo defaults instead produce
`{"Id":42,"InternalNotes":"Staff only","InternalCode":"INTERNAL-42"}`.
The SWAN attributes are not recognized, and Neo enables public fields by default.
Serialization can succeed with HTTP 200 despite the expanded response.

Replace the legacy class declaration with the following System.Text.Json
attributes. This is a class snippet, not a complete server:

```csharp
using System.Text.Json.Serialization;

public sealed class CustomerResponse
{
    [JsonPropertyName("customer_id")]
    public int Id { get; set; } = 42;

    [JsonIgnore]
    public string InternalNotes { get; set; } = "Staff only";

    [JsonIgnore]
    public string InternalCode = "INTERNAL-42";
}
```

Neo now returns `{"customer_id":42}`. Inspect public fields as well as
properties, and verify excluded members are absent from actual endpoint
responses. Prefer a concrete response DTO containing only intended client data;
internal members then need not be part of that response type at all.

These are existing SWAN migration changes, not newly introduced serializer
behavior. Explicit custom serializer options can select different field behavior;
the examples above describe Neo defaults.

### Derived members in base-typed responses

Neo intentionally retains System.Text.Json's declared-type contract for
base-typed collection elements and object members. This is a migration change
from upstream SWAN: a `List<Customer>` containing a `PreferredCustomer` includes
the members declared by `Customer`, but does not automatically include members
added by `PreferredCustomer`. The same applies to a property declared as
`Customer` or a value in `Dictionary<string, Customer>`. Omitted derived members
do not cause a serialization error; the endpoint can still return HTTP 200.

For example, if `Customer` declares `Id` and `PreferredCustomer` adds `Discount`,
upstream returned both members in the tested base-typed list. Neo defaults return
`[{"Id":42}]`. This behavior was reviewed and accepted on October 7, 2026;
runtime-derived member discovery is not being restored globally.

For database endpoints, map the intended members into a concrete response DTO.
This explicitly selects which derived values belong in the response and avoids
exposing unrelated internal members. The following is a complete console
mapping example, not a complete HTTP server. In a controller, return `response`
or pass it to `HttpContext.SendDataAsync` instead of printing it.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using EmbedIO.Serialization;

var customers = new List<Customer>
{
    new PreferredCustomer { Id = 42, Discount = 0.15m },
    new Customer { Id = 43 },
};

var response = customers.Select(customer => new CustomerResponseDto
{
    Id = customer.Id,
    Discount = customer is PreferredCustomer preferred
        ? preferred.Discount
        : null,
}).ToList();

Console.WriteLine(Json.Serialize(response));

public class Customer
{
    public int Id { get; set; }
}

public sealed class PreferredCustomer : Customer
{
    public decimal Discount { get; set; }
}

public sealed class CustomerResponseDto
{
    public int Id { get; set; }
    public decimal? Discount { get; set; }
}
```

Expected output with Neo defaults:

```json
[{"Id":42,"Discount":0.15},{"Id":43,"Discount":null}]
```

The DTO mapping adds derived data deliberately; it does not discover derived
members automatically. If inheritance is itself part of the response contract,
configure System.Text.Json polymorphism explicitly instead. This recommendation
does not require new library APIs. See the
[default JSON preservation audit](../user-reports/default-json-preservation-audit.md)
for verified differences and validation limits.

## Logging and configuration

Configure `Log.Source.Switch.Level` and `Log.Source.Listeners` using
System.Diagnostics. For example, add a `ConsoleTraceListener` for console output.
IP banning's internal log observation remains active even when trace output is
disabled. The old SWAN logger registry and terminal coloring are not retained.

Configuration remains mutable until locked; successful locking is idempotent,
validation runs before locking, and mutation after locking throws
`InvalidOperationException`. Derivations using the old base class must update
their namespace and rebuild. Background maintenance uses cancellable, sequential
tasks. Existing server, routing, HTTP, WebSocket, and JsonServer tests remain the
regression baseline; focused replacement tests cover additional boundary cases.

## Development tooling

The test suite now uses NUnit 5 and native Microsoft.Testing.Platform mode.
Contributor test commands and coverage options have changed; see
[CONTRIBUTING.md](../../CONTRIBUTING.md#development). StyleCop has been replaced by SDK
analyzers and EditorConfig settings, with explicit differences documented there.
These tooling changes do not change either library target or public APIs.

## Archived CLI consolidation

The CLI now lives in this repository and uses the current core. William approved
requiring legacy CLI plugins to be rebuilt against the current APIs. See
[CLI.md](../guides/cli.md#provenance-and-migration) for the command and plugin migration.


## Malformed typed route values (unreleased, owner-approved)

Supported controller route conversions that reject a nonempty malformed value
now produce HTTP 400 instead of the inherited 500. The handler method is not
called; its exceptions, successful conversions, unmatched routes,
optional/configuration errors and query/body binding keep their existing
behavior. This compatibility change was explicitly approved by William in
[#163](https://github.com/WilliamSmithEdward/embedio-neo/issues/163).

Clients should classify this response as invalid input and correct it rather
than retrying it as a server failure. Error callbacks can observe an
`HttpException` for this specific failure; the configured HTTP error handler
controls its body. Published 1.0.3 still has the old automatic-binding status.
Application-owned string parsing remains supported for custom validation.
Existing transport closure policies are unchanged. See
[typed route validation](../user-reports/typed-route-validation.md) for the
precise scope, tests and limitations. No release date/version is promised.


## Suffix byte ranges (unreleased)


The owner-requested correction in [#170](https://github.com/WilliamSmithEdward/embedio-neo/issues/170) changes `Range: bytes=-N` from an incorrect leading slice to the final N bytes, clamped to file size. `bytes=-0` now returns 416; a positive suffix on an empty file is ignored and returns 200. HEAD, If-Range validation, explicit/open-ended ranges and existing multipart handling retain their policies. Clients using explicit offsets may continue to do so; clients relying on wrong leading bytes must adopt correct suffix semantics. Published 1.0.3 retains the defect. No public API, dependency or target changes. See [suffix-range guidance](../user-reports/suffix-range-responses.md).

## Managed HTTP framing (unreleased)

The owner approved strict framing on 2026-10-08 as part of the modern engine
replacement. The managed listener now decodes chunked request bodies. Such
requests report `HasEntityBody = true` and `ContentLength64 = -1`; consume their
stream to completion instead of treating an unknown length as an empty body.
Trailer fields are validated and consumed without merging them into request
headers. The current public request interface does not expose trailers separately.

Send CRLF line endings, valid field names, decimal nonnegative lengths and one
unambiguous framing scheme. Conflicting Content-Length fields, Content-Length
together with Transfer-Encoding, repeated/unsupported transfer coding, duplicate
Host, invalid field controls and malformed chunk boundaries now terminate the
connection. Equal repeated decimal Content-Length is accepted. This intentionally
changes the permissive behavior recorded by earlier listener-boundary audits.

Fixed-length reads now use asynchronous transport I/O. Closing a response starts
asynchronous draining of unread request data before admitting a successor; it no
longer blocks the closing caller on that drain. The connection is closed if the
body is incomplete or malformed. Applications must finish their own body reads
before closing a response and must not concurrently read a request stream.

Header parsing enforces a cumulative 32768-byte budget even across fragmented
reads, and keeps the existing request-header deadline active until parsing is
complete. Chunk metadata lines are limited to 8192 bytes and trailers to 32768
bytes. The native Microsoft backend is unchanged. No package version or release
is implied; see the [engine program](../project/http-engine.md) for validation
status and the HTTP/2 and HTTP/3 milestones.

## Managed WebSocket framing (unreleased)

The managed WebSocket audit in [#190](https://github.com/WilliamSmithEdward/embedio-neo/issues/190)
now rejects invalid masking, reserved bits and fragmentation state as soon as the
base header is available. A continuation must follow an unfinished fragmented
message. New data messages cannot interrupt one; ping/pong control frames may
still occur between fragments. Clients that relied on silently discarded orphan
continuations must correct their frame sequence.

Use the shortest payload-length encoding required by RFC 6455 and leave the high
bit of a 64-bit length clear. Nonminimal or invalid encodings now fail with close
code 1002. Lengths greater than Int32.MaxValue cannot fit the engine's current
byte-array representation and are rejected with 1009 before any narrowing cast
or payload read. This is a representation check, not a newly configured message
budget or a guarantee that all smaller allocations will succeed. Resource limits
and memory/backpressure work remain tracked in #190.

These checks apply to the existing managed HTTP/1.1 WebSocket engine and its
HTTP/2 stream integration. Valid masked frames and interleaved control frames
remain supported. Public APIs, callback scheduling and message-size defaults
are unchanged. The native Microsoft WebSocket backend is unchanged. These
changes are unreleased.

Managed close frames now reject a one-byte status, forbidden/reserved codes and
codes outside the supported 1000-4999 ranges with 1002. Incoming 1012-1014 codes
and application/private-use 3000-4999 codes remain accepted. Close reasons must
be valid UTF-8; invalid sequences fail with 1007. Empty close payloads remain
valid. The registry snapshot is the [IANA WebSocket registry](https://www.iana.org/assignments/websocket)
checked on 2026-10-08; no extension currently negotiates additional reserved
codes. Rejection can occur before later bytes are consumed. Depending on TCP
shutdown state, an invalid peer may observe transport closure/reset rather than
a readable close frame. Applications must not rely on echoing malformed close
payloads. This change does not alter application text-message callback policy.

### Managed WebSocket limits, delivery and send framing (unreleased)

These changes from [#190](https://github.com/WilliamSmithEdward/embedio-neo/issues/190)
affect only the managed listener (`HttpListenerMode.EmbedIO`), over HTTP/1.1
and RFC 8441 HTTP/2 tunnels. Public APIs, defaults and the Microsoft backend
are unchanged.

- `WebSocketModule.MaxMessageSize` is now enforced. It was documented but the
  managed listener ignored it, so applications that set it received messages
  of any size. A message whose frames add up to more than the limit now closes
  the connection with 1009 before `OnMessageReceivedAsync` runs and before its
  payload is buffered. The default of 0 still means no limit. Review the value
  you set if you relied on larger messages arriving.
- After rejecting a message (1009, or 1007 for invalid UTF-8 text), the server
  skips the rejected payload, sends its close frame and keeps reading, without
  delivering anything, until the peer's close arrives or the existing
  one-second close timeout passes. Previously it closed the transport at once,
  and a peer that was still sending often saw a connection reset instead of
  the close status. Other protocol errors still close immediately.
- A message completed on the wire before the peer's close frame is now always
  delivered when the module has subscribed. The callback stopped as soon as the
  close was processed, and such messages were often dropped. The disconnect is
  reported after the last delivered message has been handed to
  `OnMessageReceivedAsync`. A message still queued when a connection closes
  before the module subscribes is discarded, as before.
- Outgoing messages are split into frames of up to 64 KiB instead of 1016
  bytes. RFC 6455 lets a sender fragment freely and every conforming client
  reassembles messages, but a client that read individual frames, rather
  than whole messages, now sees fewer, larger frames.
- A WebSocket frame sent in the same TCP segment as the upgrade request is
  still lost. RFC 6455 requires clients to wait for the 101 response, so
  conforming clients are unaffected; the correction belongs to the HTTP/1
  transport and is tracked in #190.

## Warning-free API cleanup (unreleased, owner-approved)

William approved the necessary source and binary changes for the compiler/analyzer
cleanup. These changes are in source and require consumers to rebuild before
adopting them. No release version or publication date has been selected.

| Previous API | Updated API and migration |
| --- | --- |
| `IHttpRequest.RawUrl` | `IHttpRequest.RawTarget`, still a string containing the received request target. Update custom request adapters and property references. Raw text retains escaping, case and query data; it is not coerced into a URI. |
| `Validate.Url(...)` returns `string` | Returns `Uri`. Use the object directly for URI APIs, or `.ToString()` where the previous normalized string is needed. Both validation overloads retain their null/error policies. |
| `Validate.UrlPath(...)` | `Validate.RoutePath(...)`, still returning a normalized route-path string. Route patterns and raw paths remain text. |
| `RedirectModule.RedirectUrl` is a string | It is a `Uri`. Use `.ToString()` when displaying or emitting the redirect location. Existing string constructors remain, with new URI overloads. |
| `ITestWebServer.BaseUrl` / `TestWebServer.BaseUrl` is a string | It is a `Uri`. Update custom implementations. URI constructors/factories are available; string constructors/factories remain. |
| Named parameters `urlPath`, `baseUrlPath`, `absoluteUrlPath` on route/file APIs | Use `requestPath`, `basePath`, `absolutePath`. Positional calls keep their CLR signatures. |
| `IHttpListener.AddPrefix(urlPrefix: ...)` | The interface parameter is named `listenerPrefix`. Its string representation still supports wildcard listener syntax. |

URI overloads also exist for `WebServer`, `WebServerOptions.AddUrlPrefix`,
`WithUrlPrefix`, `TestHttpClient.Create`, and the testing `HeadAsync`/`OptionsAsync`
extensions. String prefix APIs continue supporting `*` and `+`; ordinary URI
overloads retain `OriginalString`. When passing a null literal to an overloaded
API, specify the intended argument type to avoid overload ambiguity.

Argument-validation exceptions report the renamed parameter as well. For example,
invalid `UrlPath.Normalize` and `UrlPath.Split` inputs now have
`ArgumentException.ParamName` equal to `requestPath` rather than `urlPath`.
Update tests or error handling that compare parameter names; exception types and
the accepted/rejected input policies are retained.

Nullability metadata now describes values that already could be absent, including
user-agent/content-type headers, MIME lookup results, session values, file-provider
results, dictionary values, and JSON deserialization results. `Json.Deserialize<T>`,
the default/JSON request deserializers, and the default `GetRequestDataAsync<T>`
return a nullable result for reference types: JSON `null` and compatible empty input
already could produce null. Handle those nulls explicitly. Try-get annotations
identify successful non-null outputs. Controllers accessed before request
initialization now report `InvalidOperationException` instead of exposing an
uninitialized context/route. The obsolete formatter-based exception serialization
constructor is marked obsolete; use the ordinary exception constructors.

`FileCache` implements `IDisposable`. A module borrows its cache and transport;
disposing one module does not dispose resources shared with another module.
An owner should dispose a cache only after its modules have stopped, and must not
reuse the disposed cache. Async response-write synchronization stays alive until
all admitted writers, including queued/canceled writers, have exited. The periodic
worker owns its cancellation source until its callback finishes.

Request/plugin recovery boundaries still handle ordinary application errors.
`OutOfMemoryException`, `StackOverflowException` and `AccessViolationException`
are no longer converted into ordinary request failures or discarded by those
boundaries. They propagate through the caller/task; this does not imply that a
faulted background task automatically terminates the process. Known parser,
filesystem and DNS failures use specific exception handling. Cancellation and
configured write-error policies retain their existing contracts.

Listener-prefix paths now retain case rather than lowercasing the entire prefix.
Header comparisons remain explicitly case-insensitive. The supported library
targets and runtime dependency groups are unchanged. CI enables analyzers for
test/platform builds, treats warnings as errors, verifies formatting, and rejects
compiler/analyzer suppression directives, null-forgiving operators, and build opt-outs.

### Managed WebSocket text validation (unreleased)

Incoming text messages now require valid UTF-8 before application callbacks run,
as required by [RFC 6455 section 8.1](https://www.rfc-editor.org/rfc/rfc6455.html#section-8.1).
Malformed text closes the managed WebSocket with code 1007; this includes overlong
encodings, surrogate code points, values above U+10FFFF, stray continuation bytes
and incomplete final sequences. Partial UTF-8 sequences may span fragments;
interleaved ping/pong payloads do not affect the text decoder. Binary messages
continue accepting arbitrary bytes. Applications previously sending non-UTF-8
bytes as text must encode UTF-8 or use binary messages. These checks apply to the
managed engine over HTTP/1.1, HTTP/2 and HTTP/3; native-backend behavior, public
APIs, callback scheduling and message-size defaults are unchanged.

### Managed HTTP/1 WebSocket opening handshake (unreleased)

The managed HTTP/1 listener now requires GET over HTTP/1.1, the websocket Upgrade
and Connection Upgrade tokens, one base64 nonce representing 16 bytes, and one
WebSocket version 13 field before switching protocols. Invalid requests receive
HTTP 400 instead of an upgrade or an internal-server error. An unsupported or
missing version advertises `Sec-WebSocket-Version: 13`. Repeated nonce/version
fields are retained and rejected rather than silently taking the last value.

Repeated list-valued Upgrade and Sec-WebSocket-Protocol fields now retain all
values in order. This allows a supported subprotocol from the first field to be
selected when a later field lists another one. Ordinary case-insensitive protocol
tokens and field OWS remain accepted. Native handshakes and HTTP/2/HTTP/3 extended
CONNECT do not use this HTTP/1 nonce exchange. See
[RFC 6455 section 4.2](https://www.rfc-editor.org/rfc/rfc6455.html#section-4.2).

### Native Unix WebSocket response cleanup

On the tested .NET 10 Unix HttpListener runtime, a completed WebSocket upgrade
leaves HTTP response headers marked unsent internally. Stopping the listener can
write a second HTTP response into the upgraded stream; concurrent cancellation
can make that write throw from an already disposed NetworkStream and interrupt
listener cleanup. The native adapter now marks the successful handshake as sent
before delivering the WebSocket to application callbacks.

This mitigation uses a guarded internal boolean runtime property. It does not
change Windows behavior or IgnoreWriteExceptions. Unrecognized runtime shapes
retain their native behavior; use the managed listener to avoid this runtime
compatibility shim. It does not establish a general guarantee for cancellation
while a native upgrade itself is still in progress.


## HTTP/2 extensible priority settings (unreleased)

The new engine advertises RFC 9218 priority support by sending
SETTINGS_NO_RFC7540_PRIORITIES=1 in its initial SETTINGS frame. Peer values must
be 0 or 1; later changes from the initial effective value cause a connection
PROTOCOL_ERROR. Omission initially means 0. Repeated equal values are accepted;
duplicates in the initial frame use their last value, in wire order. This setting
was previously treated as unknown in the development engine.

Deprecated PRIORITY dependency/weight values and equivalent HEADERS fields are
ignored, including self-dependency values. Their frame shape, size and stream-ID
requirements remain checked, and HEADERS compression state remains synchronized.
Use the Priority header or PRIORITY_UPDATE for extensible urgency/incremental
signals. These are scheduling hints, not guaranteed response completion order.

## Default managed listener transition (planned, unreleased)

The completed modern engine will become the default managed listener. The old
Mono-derived implementation will be deprecated, with its migration and support
policy documented before that transition. Immediate removal is not implied.
Existing valid public entry points, including `HttpListenerMode.EmbedIO`, are to
remain usable; changing the implementation does not itself require renaming this
mode. The Microsoft listener remains an explicit compatibility option.

This transition is still in development. In the current branch HTTP/3 uses the
separate opt-in `EmbedIOHttp3` mode and its documented .NET 10, certificate and
native QUIC prerequisites. Do not interpret the planned default switch as current
combined HTTP/1, HTTP/2 and HTTP/3 hosting or identical capabilities across target
assets. See the [default listener transition acceptance criteria](../project/http-engine.md#default-listener-transition)
and [HTTP/3 guide](../guides/http3.md) for the implementation and validation scope.

## QUERY routing (unreleased, development increment)

`HttpVerbs.Query` adds explicit QUERY routes while preserving existing enum values.
Existing wildcard handlers can still receive the method. Method names are case
sensitive: lowercase `query` does not match a Query route. This increment provides
routing and request-body access, not complete RFC 10008 semantics. A QUERY handler
must perform a safe, idempotent operation and validate the request media type and
content. QUERY requests with missing or syntactically invalid Content-Type now
fail with 400 through the standard HTTP exception handler before application
modules run. Applications must still reject unsupported media types or content
inconsistent with their declared type. Complete QUERY support remains under
development; do not advertise full support based on the enum alone.

## HTTP/1 parser rejection responses (unreleased)

The managed listener now attempts a fixed, empty HTTP 400 response for request
line, header or framing initialization errors before closing the connection.
Previously these paths closed silently. Invalid input is not reflected in the
response, pipelined successor requests are not dispatched, and the existing
request deadline remains active through the write. A disconnected peer or expired
deadline can still prevent response delivery. Prefix-routing rejection remains
unchanged. This does not add CONNECT tunneling or accept authority-form targets.

## HTTP/1 request-target syntax (unreleased)

The managed listener accepts `OPTIONS *` for a root listener and retains `*` in
`Request.RawTarget`; `Request.Url` uses the local root URI for dispatch. An OPTIONS
handler can distinguish this server-wide request from `OPTIONS /` using RawTarget.
This does not automatically aggregate capabilities or generate an Allow header.

Literal fragments, backslashes, and incomplete/non-hex percent escapes in request
targets now produce 400 before dispatch, rather than being silently normalized by
URI parsing. Correctly escaped `%23` and `%25` remain accepted. Encode literal
reserved characters in client paths or query values. Existing transport scheme,
local port and valid path/query case behavior remain covered by regression tests.

### HTTP/1 Host authority syntax (unreleased)

The managed listener now rejects Host values containing userinfo, path/query/fragment
delimiters, control/non-ASCII characters, or non-digit ports before constructing
the application URL. Previously port stripping could hide malformed input, and
URI normalization could reinterpret delimiters. Send a valid host with an optional
decimal port; bracket IPv6 addresses. An empty port remains accepted as permitted
by RFC 3986. Valid requests retain the existing transport scheme and local listener
port in `Request.Url`. Rejections use the bounded empty 400 response and close the
connection before application dispatch.

HTTP/2 and HTTP/3 now also accept empty authority ports for ordinary requests and
extended CONNECT, treating them as the scheme default when comparing Host and
authority. Classic CONNECT still requires an explicit nonempty port. The original
authority text remains available through the request headers.

### HTTP/2 and HTTP/3 path/query syntax (unreleased)

The shared request parser now rejects incomplete/non-hex percent escapes and
characters outside the URI path/query grammar before application dispatch. Encode
literal brackets, braces, quotes, backticks, carets and vertical bars using their
percent-encoded forms. Valid escapes and URI delimiters remain unchanged, including
leading `//` paths and slash/question-mark characters within queries. HTTP/2
rejection resets the malformed stream with PROTOCOL_ERROR; other streams remain
usable. This increment does not finish the HTTP/1 absolute-target grammar audit.

HTTP/1 now applies the same path/query character grammar to origin-form and the
raw path/query component of absolute-form requests before URI normalization.
Literal brackets, braces, quotes, backticks, carets and vertical bars must be
percent-encoded. Valid encoded equivalents remain accepted. Invalid input receives
the bounded empty 400 response and closes before application dispatch. Existing
transport scheme/local-port URL behavior is preserved; absolute-form authority
precedence is a separate outstanding conformance item.

### HTTP/1 absolute-form host precedence (unreleased)

When an absolute request-target and Host header identify different hosts, the
managed listener now uses the target URI host for `Request.Url` and prefix routing,
as required by RFC 9112 section 3.2.2. The original Host header remains available
through `Request.Headers`; applications should use `Request.Url.Host` for the
effective routing host. Previously a conflicting Host could override the target.
Host field presence and syntax validation still apply. The existing transport
scheme/local-port URL behavior is unchanged in this increment; full absolute-URI
scheme/port handling remains under audit. Unregistered target hosts retain the
existing prefix-routing rejection behavior.

### HTTP/1 absolute-target userinfo (unreleased)

The managed listener rejects a userinfo component in an absolute request target,
including an empty component marked by `@`, with an empty 400 response followed
by connection closure. Put authentication information in the appropriate HTTP
authentication mechanism, not the request URI. At-signs in valid path/query
components and their percent-encoded equivalents remain accepted.

HTTP/1 Host numeric ports must now fit the transport endpoint range 0 through
65535 before the listener applies its existing local-port URL normalization.
Out-of-range values previously disappeared during port stripping; they now
receive the empty 400 response and connection closure. Empty ports and decimal
leading zeros remain accepted.


### Canceled response cleanup and combined drain (unreleased)

Closing a canceled managed HTTP context now aborts its unfinished response rather
than synthesizing an empty successful response during cleanup. Already written
headers or body bytes cannot be recalled; clients may see an incomplete response.
Completion callbacks still run. Normal response close/disposal remains unchanged,
and closing an old canceled HTTP/1.1 context does not close a successor request
on its reused connection. Do not rely on cancellation producing an HTTP status;
finish an intended response before canceling its context.

`WebServer.DrainAsync` supports combined TCP/QUIC hosting, including shared TCP
endpoints. It lets accepted responses finish within the shared deadline;
expiry or cancellation aborts unfinished and queued work. Immediate Stop and
server disposal remain abort operations. Shared TCP endpoint drain refuses new
requests for the draining owner while retaining sibling admission and traffic. See the
[HTTP/3 and combined-host guide](../guides/http3.md) for lifecycle
limits and cancellation behavior.

HTTP/2 and HTTP/3 context cleanup also preserves cancellation already requested
by the server or transport when cleanup runs before the linked cancellation
callback. Captured application tokens and close callbacks observe cancellation;
normal successful close does not cancel them. Recoverable exceptions from
application cancellation callbacks are logged and do not interrupt cleanup.

When TCP endpoint prefixes are shared, stopping or disposing one managed listener
now cancels only its HTTP/2 exchanges. Active sibling streams and later sibling
requests retain the same connection. Stopping the last endpoint owner still
closes its connections. Shared endpoint graceful drain waits for the selected
owner's accepted contexts without connection-wide HTTP/2 GOAWAY.

A stale managed HTTP/1 shutdown snapshot no longer closes a keep-alive connection
that has already transferred to a sibling listener. Connection ownership transfer
and scoped shutdown decisions now share a synchronization boundary.

A reset HTTP/2 context remains part of its owner's drain set until response
cleanup and close callbacks finish. A concurrent drain no longer reports
completion merely because request dispatch has started cleanup.

The opt-in HTTP/3 transport logs recoverable application cancellation callback
exceptions during its own shutdown and continues resource cleanup. Such callback
failures no longer replace the connection shutdown outcome with an aggregate
exception. This does not change exceptions raised when application code directly
cancels a token source it owns.

### Rejected HTTP/1.1 CONNECT persistence (unreleased)

A managed HTTP/1.1 response with status 300 or greater to the exact `CONNECT`
method now closes the connection even when application code requests keep-alive.
Bytes already sent after that request are not dispatched as another request.
This follows the rejection mitigation in [RFC 9931 section 8](https://www.rfc-editor.org/rfc/rfc9931.html#section-8).
After a rejection, send any subsequent request on a new connection. Rejected
WebSocket upgrades using GET retain their existing persistence behavior.

Connection reuse also honors the response's committed keep-alive decision after
headers have been sent. Mutating the public header collection afterward cannot
reopen a response that committed connection closure. This does not add a CONNECT
tunnel API or complete authority-form handling.

### HTTP/1 interim continue responses (unreleased)

The managed listener ignores `Expect: 100-continue` on HTTP/1.0 requests, as
required by [RFC 9110 section 10.1.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-10.1.1).
It also omits the interim response when request framing indicates no body.
Previously, HTTP/1.0 requests with a body and bodyless HTTP/1.1 methods other
than POST/PUT could receive an unsolicited `100 Continue` before the final
response. Clients must not wait for this interim response on HTTP/1.0 or
bodyless requests. HTTP/1.1 requests with a body retain the existing handshake.

The interim transport write is now asynchronous and remains covered by the
request-header deadline until it completes. This change does not yet implement
all expectation-list syntax, extension expectations or an application-controlled
early final-response policy. Public request/response APIs are unchanged.

### Managed WebSocket disconnection notification (unreleased)

Managed WebSocket processing now observes terminal transport cleanup directly
instead of checking state every 500 ms. Disconnection notifications can therefore
arrive sooner after a completed close. Public callback signatures and asynchronous
message/disconnection dispatch are unchanged; notification does not promise that
all asynchronous application message callbacks have finished. Close handshakes,
close deadlines and the native Microsoft backend are unchanged.

### Continue expectations across HTTP versions (unreleased)

HTTP/1.1, HTTP/2 and HTTP/3 now recognize a bare `100-continue` member in an
Expect list, including repeated members, case differences and optional whitespace.
Quoted extension values do not create synthetic members when they contain commas
or escaped quotes. Unknown expectations retain their existing ignored behavior;
this is recognition of the supported expectation, not full extension validation.
HTTP/1.0 still ignores the expectation and known-empty requests omit the interim
response.

HTTP/2 and HTTP/3 listener dispatch sends the informational headers before handing
an applicable request to the application, without consuming the request body or
ending the stream. A body-consuming handler can therefore receive a client that
waits for `100 Continue` before uploading. Earlier implementations could leave
both ends waiting until the client's fallback timer expired. Final response APIs
are unchanged. Multiplexed CONNECT tunnels are excluded from automatic continue handling.
The .NET Standard asset's existing cleartext HTTP/2 support and lack of TLS ALPN
and QUIC are unchanged.

## Unreleased modern engine: strict deflate request completion

When compressed requests are enabled, the request helpers now reject truncated
raw-DEFLATE data and bytes after its final block with HTTP 400. This applies to
single `Content-Encoding: deflate` requests and deflate layers within supported
coding chains, on both core target frameworks and listener modes. Valid existing
raw-DEFLATE content retains its selected format. No automatic zlib-header sniffing
or raw/zlib fallback is introduced.

A request that decodes to an empty body must still carry a valid compressed
stream. For raw DEFLATE, the empty fixed block `03 00` is valid; a zero-byte
compressed payload is truncated. Update clients that previously relied on runtime
acceptance of incomplete streams or trailing bytes to send one complete stream.
Streaming handlers must read through EOF before accepting the entire body:
application bytes can be returned before a later completion failure is detected.
Decoded-byte limits remain applied to final application bytes, and direct reads
of `Request.InputStream` bypass these helper policies.

The internal zlib decoder is being validated for RFC 1950 headers, window limits,
Adler-32 checksums and exact completion. It does not yet change public coding
selection, response encoding or cached variants. The broader standards-format
migration remains unfinished. These changes are development work and have not
been released.

### Buffered bytes at HTTP/1 WebSocket upgrade (unreleased)

An accepted managed WebSocket now receives any unread bytes already buffered by
its HTTP/1 connection. The handoff copies only that unread tail, replays it once,
and leaves transport disposal with the connection. Ordinary clients that send
frames after validating the handshake response retain their existing behavior.

The regression deliberately sends early data with the upgrade request. RFC 6455
section 4.1 requires clients to wait for the server response, so this is robustness
coverage rather than valid-client conformance. Accepting an upgrade no longer
silently discards those buffered bytes; it does not authorize clients to skip
handshake validation.

### Incomplete and malformed HTTP/1 request bodies (unreleased)

A managed fixed-length request body that ends before its declared Content-Length
now throws `EndOfStreamException` rather than returning normal EOF. Applications
must not treat a partial body as a complete upload. Subsequent positive reads
remain failed; empty reads, argument validation and cancellation retain their
existing behavior. Valid fixed-length and unknown-length stream reads are unchanged.

The server recognizes its own body-framing failures, including wrappers that
retain the original exception as their inner cause. Before response headers are
sent, the default request boundary sends 400 with a generic body-error message
and closes the connection. Existing chunk parser exception types remain intact.
Unrelated application `InvalidDataException` errors retain server-error handling.

If a response has already started, its status cannot be replaced: the connection
is aborted without a second error response or a clean chunk terminator. Completion
callbacks still run, and a malformed request cannot dispose the listener shared
by other clients. These changes apply to invalid/incomplete requests under the
approved strict-framing policy; custom application error handlers remain available.
