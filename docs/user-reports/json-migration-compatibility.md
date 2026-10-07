# JSON compatibility after SWAN removal

[Issue #153](https://github.com/WilliamSmithEdward/embedio-neo/issues/153) tracks a
VBA macro that used literal `vbCrLf` inside JSON strings. William requested
transparent alignment with upstream EmbedIO, within reason. Ordinary consumers
use the restored defaults through their existing APIs; no compatibility switch,
new attribute or package is required. This fix is not yet released.

## Transparent behavior

Default `EmbedIO.Serialization.Json.Deserialize`, `[JsonData]`,
`GetRequestDataAsync<T>` and JsonServer retain the following reasonable legacy
behaviors:

| Input or operation | Behavior |
| --- | --- |
| Raw CR, LF, CRLF, tabs and other controls inside strings | Preserve every character in string values and property names |
| Trailing comma in an object or array | Accept |
| Number forms such as `+001`, `-001`, `.5` and `1.` | Normalize the syntax without changing the value |
| Boolean property `"true"` or `"FALSE"` | Bind to boolean; output remains a JSON boolean |
| Named, numeric or nullable enums | Bind valid values; default output remains numeric |
| Valid non-ISO date strings | Parse with the application's current culture after trying ISO parsing |
| Public property with a private/protected setter | Bind, preserving ignored and genuinely read-only members |
| Empty/whitespace input | Return the target's default value, as SWAN did |
| Serializing NaN / infinities | Emit named JSON strings, which can be read back into floating-point types |

For example, this complete small program constructs actual CR/LF characters
inside the quoted input and receives the same line breaks in the result:

```csharp
using System;
using EmbedIO.Serialization;

var body = "{\"Text\":\"first\r\nsecond\"}";
var data = Json.Deserialize<Payload>(body);
Console.WriteLine(data.Text == "first\r\nsecond"); // True.

public sealed class Payload
{
    public string? Text { get; set; }
}
```

The default controller and callback request paths now return HTTP 200 for this
payload. Proper JSON escaping (`\r\n` on the wire) also works and preserves the
same decoded value. The original macro and its request bytes were not supplied;
these checks establish the compatibility behavior rather than confirming its
specific application has been repaired.

The [JSON specification](https://www.rfc-editor.org/rfc/rfc8259#section-7) requires
string controls to be escaped, and .NET has no raw-control
[reader option](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonreaderoptions?view=net-10.0).
Neo therefore performs a focused lexical normalization before .NET validates
the entire document. It preserves whitespace outside strings, existing escapes,
quotes/backslashes and nested values. Unchanged documents retain their original
string instance; rewritten documents allocate only after the first change.

## Deliberate boundaries

Closer compatibility does not mean reproducing all SWAN behavior:

- Failed conversions remain errors. SWAN sometimes silently kept zero, null or
  a constructor's initial value for invalid numbers, booleans, dates and object
  values assigned to strings. Neo's request binding returns 400 for those cases.
- Unknown escapes, garbage after a document, unquoted keys, single quotes,
  invalid numeric syntax and controls outside strings remain errors. Comments
  require explicit options; SWAN rejected the tested comment forms too.
- Valid Unicode surrogate pairs decode correctly; unpaired escaped surrogates
  are rejected rather than converted to replacement characters.
- ISO DateTime values retain accurate UTC/offset semantics and output `Z` where
  applicable. SWAN's tested UTC-to-local conversion and omitted UTC suffix are
  not reproduced. Non-ISO formats depend on current culture; applications should
  use explicit formats when ambiguity matters.
- Existing Neo case-insensitive property matching, public-field support,
  parameterized-constructor binding and scalar/exponent acceptance are retained.
  SWAN ignored the tested class field and lowercase-only property, and lost the
  tested constructor argument. Case-colliding properties retain Neo's last-value
  behavior; use unique property names.
- Untyped numbers retain the existing decimal representation/range. Values such
  as `1e-100` can round to zero; `1e100` remains out of range. Exact arbitrary
  precision is not claimed.
- Output whitespace, HTML/Unicode escaping and escaped slashes follow .NET.
  These byte differences preserve the tested values. Cycles continue to require
  the existing explicit serialization policy.

Legacy SWAN attributes and serializer-option APIs remain part of the separately
approved source/binary migration described in [Migration](../compatibility/migration.md).
They are not restored through reflection or a production SWAN dependency.

## Explicit application options

`Json.CreateOptions()` retains the compatibility defaults, including when copied
with `new JsonSerializerOptions(options)` or captured by `RequestDeserializer.Json`.
Application boolean/enum/date converters added to those options take precedence.
Standard JSON attributes, including `JsonIgnore`, remain effective.

A fresh `new JsonSerializerOptions()` replaces Neo's defaults and keeps .NET's
strict behavior. This is an existing explicit configuration choice, not a
requirement for ordinary consumers. Direct `System.Text.Json.JsonSerializer`
calls do not run Neo's lexical normalization even when supplied options from
`Json.CreateOptions()`. A custom metadata resolver replaces the default
private-setter resolver too. `[JsonData]` continues to use Neo defaults; a custom
request deserializer's options do not automatically change that attribute.

## Validation and reproduction

The [standalone probe](../../test/JsonMigrationProbe/Program.cs) pins SWAN 3.1.0,
uses committed package locks, and targets .NET 10 and .NET Framework 4.7.2. The
latter consumes Neo's .NET Standard 2.0 asset. All probe dependencies are private,
test-only and outside the ordinary solution and shipped packages.

From the repository root with the SDK selected by `global.json`:

```powershell
New-Item -ItemType Directory -Force TestResults/json-migration | Out-Null

dotnet restore test/JsonMigrationProbe/JsonMigrationProbe.csproj --locked-mode
dotnet run --project test/JsonMigrationProbe/JsonMigrationProbe.csproj --configuration Release --framework net10.0 --no-restore > TestResults/json-migration/modern.log

# Windows with .NET Framework installed:
dotnet build test/JsonMigrationProbe/JsonMigrationProbe.csproj --configuration Release --framework net472 --no-restore
& test/JsonMigrationProbe/bin/Release/net472/JsonMigrationProbe.exe > TestResults/json-migration/legacy.log
```

Each execution records 84 input cases across three configurations (SWAN, Neo
defaults and explicit strict .NET options), eight serialization cases across two
serializers, and four in-process HTTP observations: 272 observations total. It
also enforces 16 portable compatibility assertions, all four HTTP statuses and
successful CRLF-value preservation. Parser differences are recorded for review
rather than assumed to be defects automatically. Culture is explicitly invariant
in this probe; its local DateTime diagnostics depend on the host time zone.

Local Windows checks passed on .NET 10.0.11 and the installed .NET Framework
runtime, including actual netstandard2.0 execution. The ordinary regression
fixture covers raw controls, nesting, escaping, number boundaries, failed
conversions, option snapshots/overrides, dates, private setters and both request
binding paths. CI runs the ordinary tests and modern probe on Windows/Linux/macOS,
and the legacy probe on Windows. Required checks must pass before merge.

The HTTP probe uses TestWebServer's in-process pipeline. It is not an original
VBA-host, real-network or mobile-runtime reproduction. No complete SWAN parity,
custom-attribute compatibility, release availability or original-macro repair is
claimed.
