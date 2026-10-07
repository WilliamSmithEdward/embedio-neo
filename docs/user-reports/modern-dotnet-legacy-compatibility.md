# Modern .NET with legacy library compatibility

[Upstream #549](https://github.com/unosquare/embedio/issues/549) proposed moving
EmbedIO to .NET 6. Original maintainer `rdeago` wanted better lifetime handling,
argument diagnostics, configuration and routing. `michael-hawker` explained why
the .NET Standard contract still mattered to existing applications. The
maintainer then proposed multi-targeting. Their discussion helped distinguish
modern development tools from the runtime requirements imposed on consumers.

## Decision for Neo

Keep `netstandard2.0;net10.0` for the libraries, and `net10.0` for the CLI.
Neo already uses a .NET 10 SDK and C# 14. Modernization can continue without
removing the legacy asset. This answers the target-policy question; it does not
adopt every API or architecture idea in the original proposal.

| Package | Shipped targets | Relevant runtime package dependencies |
| --- | --- | --- |
| `EmbedIO-Neo` | `netstandard2.0`, `net10.0` | Standard: Microsoft System.Text.Json and its supporting packages. .NET 10: none. |
| `EmbedIO-Neo.JsonServer` | `netstandard2.0`, `net10.0` | Core package. |
| `EmbedIO-Neo.Testing` | `netstandard2.0`, `net10.0` | Core package. These are optional consumer test helpers, not the repository's NUnit tools. |
| `EmbedIO-Neo.DependencyInjection` | `netstandard2.0`, `net10.0` | Core and Microsoft.Extensions.DependencyInjection / Hosting.Abstractions, including their target-specific supporting dependencies. |
| `EmbedIO-Neo.Cli` | Tool under `tools/net10.0/any` | Bundles the core server assembly; requires a .NET 10 host. |

Assembly names and `EmbedIO` namespaces stay unchanged. No targets, dependency
versions, production APIs or defaults change for this evaluation. Retaining
.NET Standard does not undo the earlier approved SWAN/JSON migration; applications
moving from archived EmbedIO must still follow the [migration guide](../compatibility/migration.md).

## Which asset does my application use?

NuGet selects the compatible asset during restore. A .NET 8 application cannot
load Neo's .NET 10 asset simply because a newer SDK built it. With the current
package layout, .NET 8 uses the Standard asset; .NET 10 uses the modern asset.
Installing a newer SDK does not retarget an existing application.

| Consumer target | Core asset selected in the published 1.0.2 probe |
| --- | --- |
| `net10.0` | `lib/net10.0/EmbedIO.dll` |
| `net8.0` | `lib/netstandard2.0/EmbedIO.dll` |
| `net472` | `lib/netstandard2.0/EmbedIO.dll` |

The Standard asset runs on the application's runtime and can benefit from that
runtime's implementation improvements. It does not acquire newer compile-time
APIs or become equivalent to an assembly built against .NET 10. Its JSON
dependency graph also differs from the modern core asset.

Microsoft documents [NuGet selection and multi-targeting](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/cross-platform-targeting).
.NET Standard is an API contract, not a runtime. Standard 2.1 does not support
.NET Framework; replacing 2.0 with 2.1 would drop consumers. See the
[Standard overview](https://learn.microsoft.com/en-us/dotnet/standard/net-standard).
An additional 2.1 or older modern target needs a concrete API/dependency benefit
and validation plan; this report does not justify adding one.

## SDK, language features and behavior

Contributors use the SDK selected by `global.json` (10.0.400 floor, stable later
feature bands allowed), C# 14 and the existing analyzers. Library compilation
still checks each target's available references. Ordinary consumers install a
NuGet package; they do not need Neo's contributor SDK to compile their own older
application target.

The original feature suggestions need individual design decisions:

- `IAsyncDisposable` is a runtime interface. A compiler upgrade alone does not
  add it to Standard 2.0. Adding async lifetime APIs or an interface package
  would require an ownership/cancellation/shutdown design and compatibility
  tests. Existing disposal contracts remain intact here.
- Caller argument expressions are compiler-supported metadata. A conditional
  attribute can support older targets, but changing parameter-name behavior
  still needs contract review. Neo keeps the existing validators; see
  [argument validation](argument-validation.md).
- Init-only setters can use a compiler marker on older targets. They do not
  replace runtime configuration validation and locking. Changing current
  setters to init-only would change how consumers configure objects.
- Source generators can produce code for older targets. Their output must use
  compatible APIs, and replacing routing needs separate evidence and scope.

No polyfill, new interface, init-only conversion, generator or configuration
rewrite is introduced by this policy decision. A target label alone does not
promise Native AOT, trimming, mobile sandbox or every listener capability.
For the underlying contracts, see Microsoft's [IAsyncDisposable reference](https://learn.microsoft.com/en-us/dotnet/api/system.iasyncdisposable),
[caller-expression attribute](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.callerargumentexpressionattribute)
and [init-only proposal](https://github.com/dotnet/csharplang/blob/main/proposals/csharp-9.0/init.md).

## Check the core asset in your application

For a new .NET 10 console project, install the verified published version:

```sh
dotnet new console --framework net10.0 --name NeoAssetCheck
cd NeoAssetCheck
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace the generated `Program.cs` with this complete program, then run
`dotnet run`. It exercises JSON and a utility without starting a listener:

```csharp
using System;
using System.Reflection;
using System.Runtime.Versioning;
using EmbedIO;
using EmbedIO.Serialization;
using EmbedIO.Utilities;

internal static class Program
{
    private static void Main()
    {
        var assembly = typeof(WebServer).Assembly;
        var target = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        var json = Json.Serialize(new Payload { Message = "compatible", Count = 3 });
        var restored = Json.Deserialize<Payload>(json);
        if (restored.Message != "compatible" || restored.Count != 3 || UrlPath.Normalize("/api", true) != "/api/")
            throw new InvalidOperationException("Consumer compatibility smoke failed.");
        Console.WriteLine("Asset: " + target);
        Console.WriteLine("Assembly: " + assembly.GetName().Name);
        Console.WriteLine("JSON: " + json);
        Console.WriteLine("Utilities: passed");
    }
    public sealed class Payload
    {
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
```

Expected .NET 10 output:

```text
Asset: .NETCoreApp,Version=v10.0
Assembly: EmbedIO
JSON: {"Message":"compatible","Count":3}
Utilities: passed
```

The same source compiled and ran in `net8.0` and `net472` consumer projects.
Their first line was `Asset: .NETStandard,Version=v2.0`; all other lines matched.
The Framework-targeted executable ran on installed Windows .NET Framework 4.8.1,
not an original 4.7.2 installation. The .NET 8/10 host versions were 8.0.28 and
10.0.11. Restore assets and loaded assembly metadata both confirmed selection.
These are core asset/JSON/utility checks, not a complete HTTP or platform suite.

## Compatibility and support limits

Keep application runtimes serviced. As checked on October 6, 2026, .NET 10 LTS
support ends November 14, 2028; .NET 8/9 support ends November 10, 2026. .NET 6
is already out of support. These dates come from Microsoft's
[support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
and can change. Package asset compatibility does not extend runtime support.

Standard compatibility is not proof that a particular Unity, UWP, Xamarin or
MAUI application works. Networking, sandbox entitlements, reflection, TLS,
serialization and native listener behavior require validation in the actual
app model. Consult the relevant platform/report guides and provide a minimal
reproduction for a concrete failure. Do not infer full platform certification
from these small desktop probes.

The current dual-target policy addresses modernization while retaining existing
consumers. Any future target removal or other breaking change requires William's
approval, migration documentation and release notes.
