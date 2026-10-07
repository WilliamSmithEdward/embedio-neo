# JSON compatibility after SWAN removal

Investigation: [issue #153](https://github.com/WilliamSmithEdward/embedio-neo/issues/153).
The reported VBA macro used literal `vbCrLf` inside JSON strings. A differential
probe confirms the parser difference; the original macro and its request bytes
have not been supplied. This investigation does not implement a compatibility
mode or establish that the original application is repaired.

## Literal line breaks

The following C# program constructs actual CR/LF characters inside the quoted
JSON value, rather than JSON escape sequences:

```csharp
using EmbedIO.Serialization;

var body = "{\"Text\":\"first\r\nsecond\"}";
var data = Json.Deserialize<Payload>(body); // Throws JsonException in Neo.

public sealed class Payload
{
    public string? Text { get; set; }
}
```

SWAN 3.1.0 accepts that input and preserves its CRLF. Neo rejects it, including
when `AllowTrailingCommas` and comment skipping are enabled. Its `[JsonData]`
and `GetRequestDataAsync<T>` paths return HTTP 400. Changing the wire value to
`"first\r\nsecond"` (literal backslashes followed by `r` and `n` in the request)
returns HTTP 200 through both paths and decodes to the same CRLF string.

The JSON specification requires string control characters to be escaped;
[JSON section 7](https://www.rfc-editor.org/rfc/rfc8259#section-7) and the
[reader options](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonreaderoptions?view=net-10.0)
explain why comments/trailing-comma settings do not permit raw line breaks.
The input is nonstandard, but the acceptance change still affects existing
clients that depended on SWAN's tolerance.

A sender should serialize the complete value with a JSON encoder. Escaping
CRLF on the wire preserves the application's line breaks. Global newline
replacement is unsuitable: whitespace outside strings is valid, existing
escapes must stay intact, and quotes/backslashes determine string boundaries.
A server compatibility path needs a separately reviewed policy and focused
coverage rather than a blanket rewrite of malformed JSON.

## Additional observed differences

The probe uses identical inputs and DTO types. Its Neo relaxed-options branch
retains `Json.CreateOptions()` defaults and adds only trailing commas and comment
skipping. These results are observed behavior, not a recommendation to reproduce
every permissive or lossy legacy behavior.

| Input or operation | SWAN 3.1.0 | Neo defaults |
| --- | --- | --- |
| All raw string controls U+0000 through U+001F | Accepts and preserves each character | Rejects |
| Object/array trailing comma | Accepts | Rejects; explicit trailing-comma option permits it |
| Enum property `"Second"` | Binds to enum value 1 | Rejects; an explicit string-enum converter is a candidate remedy |
| Boolean property `"true"` | Binds to true | Rejects |
| Date property `"10/07/2026"`, invariant culture | Parses to October 7 | Rejects non-ISO date |
| UTC date `"2026-10-07T12:30:00Z"` | Produces a local DateTime | Produces a UTC DateTime representing the same instant |
| Serializing a UTC DateTime | Omits `Z` in this case | Emits `Z` |
| Lowercase `text` for DTO property `Text` | Leaves property null | Binds the value |
| Both `Text` and `text` in that order | Keeps the exact-case value | Later case-insensitive assignment wins |
| Integer property: null, fractional number, overflow, invalid quoted number | Accepts DTO with value 0 | Rejects each conversion |
| String property receiving object | Leaves property null | Rejects |
| String property receiving number/boolean | Leaves property null | Converts to string |
| Empty body | Returns null | Rejects |
| Numbers with leading plus or zero (`+1`, `01`) | Accepts as 1 | Rejects |
| Unknown string escape `\q` | Removes backslash, keeps `q` | Rejects |
| Object followed by non-JSON garbage | Ignores garbage in tested case | Rejects |
| Escaped surrogate pair `\uD83D\uDE00` | Produces two replacement characters | Decodes the emoji correctly |
| Unpaired escaped surrogate `\uD800` | Produces replacement character | Rejects |
| Serializing NaN / positive infinity | Emits strings `"NaN"` / `"Infinity"` | Throws ArgumentException |

The probe also records broader acceptance: scalar roots and exponent numbers
such as `1e3` are rejected by SWAN and accepted by Neo. Untyped `1e-100` becomes
zero in Neo's decimal representation. Both reject the tested integer beyond
decimal range and `1e100`. Single quotes, unquoted keys and comments are rejected
by both defaults; explicit Neo comment skipping accepts the comment cases.

Quoted integers, exact duplicate properties, array roots, null strings and
escaped CRLF preserve the tested results. Unicode/HTML escaping, spaces and
escaped slashes differ in output bytes while preserving the tested JSON values.
Numeric enum output, byte-array Base64 and integer dictionary keys retain their
representations apart from those formatting differences.

## Repeat the investigation

The standalone [probe source](../../test/JsonMigrationProbe/Program.cs) pins SWAN
3.1.0 with a committed package lock and references the current Neo projects.
It is outside the solution and shipped packages. SWAN is a private test-only
dependency; production dependency groups remain unchanged.

From the repository root, using the SDK selected by `global.json`:

```powershell
New-Item -ItemType Directory -Force TestResults/json-migration | Out-Null

dotnet restore test/JsonMigrationProbe/JsonMigrationProbe.csproj --locked-mode
dotnet run --project test/JsonMigrationProbe/JsonMigrationProbe.csproj --configuration Release --no-restore > TestResults/json-migration/probe.log
```

The probe logs acceptance, errors, decoded values, UTF-16 string units and
serialization output. Differences are recorded for review rather than treated
as automatic failures. The four HTTP checks assert the expected 400/200 status.

Local validation on Windows with .NET runtime **10.0.11** produced 237 input
observations (79 inputs across three configurations), 16 output observations
(eight values across two parsers), and four in-process HTTP observations.
The culture is explicitly invariant; DateTime local-zone conversion depends
on the host zone. This is not a real network listener, VBA, old .NET Framework,
Unity or mobile runtime reproduction. The .NET Standard 2.0 execution path and
other runtime/platform combinations remain follow-up work.

## Remaining decisions

Prioritize literal CR/LF compatibility and valid-JSON binding differences.
Decide which legacy behaviors should be supported explicitly and which require
migration guidance; silently defaulting failed conversions can conceal bad
requests. Existing per-request deserializer options do not automatically apply
to `[JsonData]`, which uses default options.

A proposed fix must test string values and property names, nested containers,
escaped quotes and backslashes, existing escapes, malformed surrounding syntax,
both request-binding paths and both library targets. Expand the audit to custom
attributes, constructor/read-only member behavior, recursion/cycles and additional
numeric/date types before making any broad parity claim. Keep issue #153 open
until the compatibility policy and relevant implementation/validation work are
settled. No release availability is implied by these findings.
