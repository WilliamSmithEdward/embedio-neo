# Utility package extraction decision

[Upstream #550](https://github.com/unosquare/embedio/issues/550), proposed by
original maintainer `rdeago`, suggested moving general-purpose helpers into a
separate EmbedIO.Utilities assembly and NuGet package for reuse outside the web
server. It also anticipated a rewritten validation framework. No original
comments, reactions or concrete independent consumer examples were present.

## Decision for Neo

The published-package examples below target 1.0.2. In the unreleased warning cleanup,
`Validate.UrlPath` is named `Validate.RoutePath`; see the
[migration notes](../compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved).

Keep the current utilities in `EmbedIO.dll` and the existing `EmbedIO-Neo`
package. A standalone utility package is **not planned for this request**. The
existing helpers can already be used without constructing or starting a server.
For new applications needing only a small general-purpose operation, prefer the
appropriate built-in .NET API where it satisfies the application's contract.

This preserves public type identities, both supported targets and the current
package graph. A unified repository can host multiple useful packages; it does
not by itself justify or prohibit another package. Extraction needs a concrete
consumer and a stable boundary that provides enough value to support separately.
No namespace/assembly/package move, type duplication, new dependency or behavior
change is implemented by this decision.

The original proposal's goal of useful reuse is sound. The current namespace is
not a self-contained library boundary: some types are small conveniences, some
implement HTTP-specific policies, and others directly depend on core contracts.
Moving nearly the entire namespace would require a broader architecture and
compatibility design than relocating files.

## Inventory and boundaries

Reflection of the current .NET 10 and .NET Standard 2.0 builds found the same
**15 public top-level utility types**, all defined in `EmbedIO`. The 19 source
files include five parts of one `Validate` type; source-file count is not type
count. This snapshot was evaluated against the baseline at commit `1755720`.

| Types | Boundary and .NET alternatives |
| --- | --- |
| `StringExtensions`, `UniqueIdGenerator` | Small wrappers around string operations and GUID-based diagnostic IDs. Direct `string.Split`, comparisons and `Guid` can cover simple new consumer needs; preserve existing wrapper semantics for current callers. A diagnostic ID is not an authentication token. |
| `HttpDate` | BCL-backed HTTP date parsing/formatting. Invariant UTC `r` formatting covers the formatting example below; generic parsing is not established as equivalent to its accepted historical input formats. |
| `NameValueCollectionExtensions` | BCL collection conveniences with defined case/value matching behavior. Plain collection access is available; replacement needs the same null, repeated-value and comparison policies. |
| `IPParser` | IP literals, DNS and range/CIDR behavior. `IPAddress` and `Dns` cover parts of this domain; they do not replace its range API by themselves. Its failure diagnostics use core logging. |
| `QValueList`, `QValueListExtensions` | HTTP quality values and compression negotiation. The extension's public output includes core `CompressionMethod`; moving it depends on that contract and negotiation policy. |
| `UrlEncodedDataParser`, `UrlPath` | HTTP query/form and path policies, including repeated values, bracketed indexes, flags, read-only results and path normalization. BCL URL decoding or `Uri` alone does not establish equivalent behavior. The parser also uses the internal `LockableNameValueCollection`. |
| `Validate` | General guards plus MIME, HTTP-token, URL-path and route checks. Implementation depends on core MIME and routing code. Built-in guards help modern consumers; retain existing contracts. See [argument validation](argument-validation.md) for the separately evaluated fluent-validator proposal. |
| `IComponentCollection<T>`, `ComponentCollection<T>`, `DisposableComponentCollection<T>`, `ComponentCollectionExtensions` | Named/configuration-locked collections and component disposal. `ComponentCollection<T>` publicly derives from `EmbedIO.Configuration.ConfiguredObject`; a `List` or `Dictionary` is not a complete behavioral replacement. |
| `MimeTypeProviderStack` | Core infrastructure, explicitly documented as not intended for direct application use. Its public interface is `EmbedIO.IMimeTypeProvider`; its implementation also uses core MIME associations. |

The public-signature audit identified the configuration base, MIME interface and
compression enum outside the utility namespace. Source inspection additionally
identified routing/MIME implementation calls, diagnostics and the internal query
collection. Looking only at a namespace or public signatures misses these
implementation dependencies.

Repository source references show use in the server and test helpers. A textual
scan found no direct utility-type references in the CLI, JsonServer or dependency
injection source; this is not proof that no external application uses them, nor a
semantic call graph. The upstream report supplied no independently deployable
consumer that requires a separate package. Reuse potential is distinct from a
verified extraction requirement.

## Assembly identity and compatibility

Moving files while preserving the namespace does not preserve the defining
assembly. Copying public types into another assembly creates distinct identities
and can break assignments or public signatures; it does not provide binary
compatibility. Keep one authoritative definition of each type.

[CLR type forwarding](https://learn.microsoft.com/en-us/dotnet/standard/assembly/type-forwarding)
can let an unchanged compiled consumer resolve a moved type through its original
assembly. A candidate migration would retain the old assembly with forwarders,
keep full type names/contracts, and deploy the destination assembly plus its
runtime/package dependency metadata. This is a possible mechanism, **not a
validated Neo migration or blanket compatibility guarantee**.

Reflection and assembly-qualified identity observations, discovery of exported
versus forwarded types, deployment/load contexts, target-framework asset
selection and any applicable signing/version constraints still need validation.
The old assembly must remain available to old consumers; forwarding does not
eliminate the need to deploy the destination or preserve notices.

For the whole current namespace, a naive utilities-to-core reference plus the
core's new utilities reference creates a dependency cycle. Removing it would
mean choosing a smaller subset, moving additional shared contracts or redesigning
implementations. Each affects scope and may affect consumers. No such move is
approved here. Any breaking change still requires William's explicit approval,
release notes and migration documentation.

## Package and support cost

The existing core package defines the utility API on .NET 10 and .NET Standard
2.0. Its .NET 10 runtime package graph has no external package dependencies;
.NET Standard 2.0 retains Microsoft's JSON/support dependencies, even for a
consumer that only uses a utility. A proposed smaller package's actual dependency
closure would have to be measured after selecting its types; this audit does not
promise a dependency-free extracted package.

An additional first-party package is still another package/assembly and dependency
edge, even within one repository. It needs an approved identity, coordinated
versioning and release/trusted-publishing coverage, target/lockfile validation,
security scanning, README/icon/notices, compatibility tests and ongoing support.
Any applicable original and third-party licenses must remain with the distributed
source/binaries. The current evaluation adds none of these shipping artifacts.

## Complete utilities-only example

This example uses the published package without opening a listener, making DNS
requests or writing files:

```sh
dotnet new console --framework net10.0 --name UtilitiesOnly
cd UtilitiesOnly
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace `Program.cs` with:

```csharp
using System;
using System.Globalization;
using EmbedIO.Utilities;

// Using utilities does not start an HTTP listener.
Console.WriteLine("Utility assembly: " + typeof(UrlPath).Assembly.GetName().Name);
Console.WriteLine("Base path: " + Validate.UrlPath("basePath", "/api//items", true));
var query = UrlEncodedDataParser.Parse("?tag[0]=one&tag[1]=two&debug", groupFlags: false);
Console.WriteLine("Tags: " + string.Join(",", query.GetValues("tag")!));
Console.WriteLine("Flag is empty: " + (query["debug"] == string.Empty));
var date = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
Console.WriteLine("HTTP date: " + HttpDate.Format(date));
Console.WriteLine("Diagnostic ID length: " + UniqueIdGenerator.GetNext().Length);
// An independent application can use built-in APIs for these simpler tasks.
Console.WriteLine("BCL date: " + date.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture));
Console.WriteLine("BCL GUID parses: " + Guid.TryParse(Guid.NewGuid().ToString("N"), out _));
```

Run `dotnet run`. Expected output:

```text
Utility assembly: EmbedIO
Base path: /api/items/
Tags: one,two
Flag is empty: True
HTTP date: Tue, 06 Oct 2026 12:00:00 GMT
Diagnostic ID length: 22
BCL date: Tue, 06 Oct 2026 12:00:00 GMT
BCL GUID parses: True
```

The example compares only the demonstrated simple date-formatting/identifier
operations; it does not claim all utilities can be replaced with BCL equivalents.
The GUID's value varies. Existing URL/query policies remain useful independently
of server startup.

## Validation and reconsideration

Both core targets build, their reflected public utility inventories/public core
dependency edges match, and the complete example runs against published 1.0.2.
The existing regression suite protects utility and server behavior. There are no
new production APIs or permanent tests mirroring this documentation-only decision.
No .NET Standard execution on an old runtime or full type-forwarding migration
is claimed.

Reconsider extraction for a concrete independent consumer with an identified
minimal type set, meaningful dependency/deployment benefit, an acyclic boundary,
and an owner-approved package identity and compatibility plan. Validate unchanged
precompiled consumers and source consumers across both supported assets, along
with reflection/load-context behavior, dependency metadata and notice distribution.
Without that specific use case, close this evaluated proposal as not planned
rather than retaining an indefinite implementation promise.
