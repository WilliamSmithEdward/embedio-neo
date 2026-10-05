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
values can still be read into string properties. Invalid request JSON returns 400.

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

Explicitly supplied options replace the defaults. Replace SWAN JSON attributes
with System.Text.Json attributes such as `JsonPropertyName` and `JsonIgnore`.
Use converters for application-specific enum, date, and number representations.
Escaping, whitespace, date formatting, constructor selection, unsupported types,
and error details follow System.Text.Json rather than SWAN. Scalar JSON roots
are now accepted. Untyped numbers outside decimal range throw `JsonException`.
Direct JSON utility failures use `JsonException` rather than `FormatException`.
Do not assume byte-for-byte JSON compatibility: validate stored documents and
client contracts, especially custom SWAN attributes or serializer options.

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
[CONTRIBUTING.md](CONTRIBUTING.md#development). StyleCop has been replaced by SDK
analyzers and EditorConfig settings, with explicit differences documented there.
These tooling changes do not change either library target or public APIs.

## Archived CLI consolidation

The CLI now lives in this repository and uses the current core. William approved
requiring legacy CLI plugins to be rebuilt against the current APIs. See
[CLI.md](CLI.md#provenance-and-migration) for the command and plugin migration.
