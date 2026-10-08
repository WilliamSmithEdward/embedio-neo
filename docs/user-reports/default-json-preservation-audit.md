# Default JSON preservation audit

Requested by William on October 7, 2026: assess whether database results returned
as a list of class instances are preserved exactly and deterministically, and
compare the defaults with archived EmbedIO main.

## Outcome

A materialized, unchanged list of concrete DTOs preserves the tested item order,
nulls, duplicate values, repeated references as repeated values, Unicode strings,
decimal values, signed 64-bit IDs, and timestamp values through Neo's default
JSON response path. This is not a guarantee of compatibility with every upstream
JSON contract. The SWAN replacement changes member selection, representations,
reference handling, and failure behavior.

The most consequential migration risks are silently omitted derived members and
unintentionally exposed members. There is no production change in this audit.
William accepted retaining the declared-type contract on October 7, 2026, with
concrete response DTO mapping recommended to include intended derived data.
See the [migration example](../compatibility/migration.md#derived-members-in-base-typed-responses).
The other characterized differences do not establish a decision to change or
endorse every behavior.

## Current-main reconciliation

The original audit below describes the older inspected checkout, not every later Neo version. Before preparing this PR, current main (`7b412440715f674d791f7b0d67478c1d330d394c`) was checked: its defaults already emit NaN/infinities as named strings through the separately merged JSON compatibility work. The migration guide and permanent regressions reflect that current behavior; fresh strict .NET options still reject nonfinite values. All forty comparison fixtures were rerun against the built current-main assembly: NaN and both infinities now match upstream; the other listed differences remain. Fourteen focused regressions pass on current main. Its real HTTP failure checks cover depth, getters, arrays and cycles.

## Comparison baseline and method

Archived [EmbedIO main (named `master`)](https://github.com/unosquare/embedio/tree/6a6bb4117ae6073779049fafb681c76970aa7314)
uses `Swan.Formatters.Json.Serialize(data)` in its default response serializer.
Its [dependency pins](https://github.com/unosquare/embedio/blob/6a6bb4117ae6073779049fafb681c76970aa7314/Directory.Packages.props)
specify Unosquare.Swan.Lite 3.1.0. The comparison executed the saved SWAN 3.1.0
baseline assembly against Neo's actual `Serialization/Json.cs`, rather than an
approximation of either serializer. Neo's inspected checkout was
`16d8effc64d58fd7c7e6dbdb6538309423aaf90d`; its existing documentation edits were
already present before this audit.

Forty fixtures were compared on Windows .NET 10.0.11 with SDK 10.0.400. The
probe records both outputs, exceptions, parsed JSON equality, and twenty repeat
serializations for every successful Neo fixture. It includes ordinary and
inherited DTOs, fields and attributes, numeric boundaries, dates, collections,
shared references, cycles, depth, and failing getters. Parsed equality ignores
formatting and object property order; array order and member/value changes
remain significant.

Fourteen permanent cases in `DefaultJsonContractTest` additionally cover the
current contract. Six cases use a real managed localhost listener: buffered and
default unbuffered DTO responses, plus four serialization failures followed by
a successful request. Successful responses are compared at the byte level over
three requests and parsed using exact .NET numeric and timestamp types.

The focused fourteen cases passed. The full Windows suite passed with 431
successes and two existing platform skips (433 total), without failures.

Local probe source, JSON results, runtime details, build logs, and TRX reports
are under ignored `TestResults/json-parity`. SWAN remains outside production and
permanent test dependencies.

## Original-checkout comparison results

| Surface | Archived upstream defaults | Original inspected Neo checkout | Implication |
| --- | --- | --- | --- |
| Derived instances inside `List<Base>`, base-typed properties, or dictionary values | Runtime-derived members included | Only declared base members included | Silent omission of derived data unless polymorphism is explicitly configured |
| Derived instances inside `List<object>` | Runtime-derived members included | Runtime-derived members included | This particular shape retained the tested members; it is not a general polymorphism solution |
| SWAN rename/ignore attributes | Honored | Not honored | Renamed keys change; previously ignored members can appear |
| System.Text.Json rename/ignore attributes | Not honored by SWAN in the probe | Honored | Apply the new attributes deliberately when migrating DTOs |
| Public fields | Omitted | Included | Payload expands; inspect fields for unintended exposure |
| `DateTime` | Tested UTC and unspecified values lose fractional seconds; UTC output has no `Z` | Fractional seconds retained; UTC has `Z` | Better value fidelity, but an observable string contract change |
| `DateTimeOffset` | Object containing date/time properties | ISO timestamp string | Object-to-string contract change |
| `Uri`, `DateOnly`, `TimeOnly` | Reflected objects | Strings | Object-to-string contract change; modern types were tested on a modern host |
| Repeated reference to the same object | Later occurrence becomes a `$circref` marker | Full value repeated | Same-instance duplicates differ from distinct objects with equal values |
| Object cycle | `$circref` marker | `JsonException` | Default endpoint serialization fails; explicit cycle policy is required |
| Getter that throws | Property omitted | Exception propagated | Neo fails the response instead of silently omitting the property |
| NaN and positive/negative infinity | Strings | `ArgumentException` | Nonfinite numeric payloads fail by default |
| Finite `double.MaxValue` and `double.Epsilon` | Strings | JSON numbers | Numeric token type changes even though .NET parses the tested numeric values exactly |
| Multidimensional array | Flattened array | `NotSupportedException` | Requires an explicit representation |
| Depth | The tested 32-link chain fails; 8 links succeed | 32 links succeed; 64 and 70 links fail | Limits differ; both serializers have bounds |
| Escaping and spacing | Different escaping and spaces | System.Text.Json escaping and compact spacing | Bytes differ even where parsed data is equal |

These differences arise from the previously approved SWAN migration. They are
not evidence that Neo's HTTP transport corrupts arbitrary lists. See
[migration guidance](../compatibility/migration.md) and
[circular-reference guidance](../guides/json-circular-references.md).

The probe also found `DBNull.Value` becomes `{}` in **both** serializers. A
direct database mapping must normalize database nulls into CLR `null` when JSON
`null` is intended. Both serializers failed for the tested `DataTable`; this is
not a newly supported or newly broken DTO-list case.

## What retained its tested meaning

Distinct concrete DTOs, including duplicate values, retain the same parsed
content in both serializers. Empty lists, a null root, null elements, conventional
integer and decimal values, numeric enums, GUIDs, Base64 byte arrays, time spans,
read-only properties, string/integer dictionary keys, and the tested stable LINQ
enumerable also retained their parsed meanings.

Every successful Neo fixture produced identical JSON across twenty repeated
serializations of that unchanged fixture. The actual HTTP DTO body also repeated
identically. This does not establish canonical JSON across runtime versions,
concurrent object mutation, custom getters, unordered collections, or repeated
database queries.

The failing HTTP fixtures returned 500 and the next valid request returned 200
with the expected ID while the listener remained Listening. The failures were
observable response errors rather than silent list truncation in these cases.

## Recommended consumer contract

Use a materialized `List<ConcreteResponseDto>` whose public members contain only
the intended response data. Project database results into that DTO, normalize
`DBNull`, and avoid navigation cycles and getters that perform database work.
Check derived-type requirements explicitly rather than assuming runtime members
will be included everywhere.

Replace SWAN member attributes with System.Text.Json attributes, and inspect
public fields. Specify timestamp and numeric representations in the client
contract. A JavaScript client can lose precision after receiving correct JSON;
these tests use .NET parsing and do not validate a browser's numeric model.

For deterministic query results, specify an explicit database order including a
unique tie-breaker and use an appropriate database consistency policy. Once
materialized, keep both the list and its objects stable until serialization ends.

Do not silently restore upstream defaults globally: current Neo consumers may
depend on current behavior. Review any opt-in compatibility serializer against
these verified cases, especially member exposure, polymorphism, and reference
handling, before proposing a production change.

## Validation limits

This audit compares serializer behavior on Windows .NET 10 and exercises Neo's
real managed HTTP listener. It does not run an actual database/ORM, JavaScript
client, historical Mono runtime, or upstream HTTP server. SWAN reflection over
framework types can depend on the host runtime; reflected `Uri` and modern
date/time-type results should not be generalized to older runtimes. Cross-platform
CI and other target-runtime checks were not run for this local audit.
