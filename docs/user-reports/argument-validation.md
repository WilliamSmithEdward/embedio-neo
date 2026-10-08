# Argument validation: modern guards with existing contracts

[Upstream #551](https://github.com/unosquare/embedio/issues/551), proposed by
original maintainer `rdeago`, explored a fluent `Arg` API for a future v4 utility
library. It used ref-struct wrappers, implicit conversions, caller-expression
names, nullable checks, predicates, comparisons and derived path outputs.
`michael-hawker` pointed to Community Toolkit Guard as another approach.

## Decision for Neo

The published-package examples below target 1.0.2. In the unreleased warning cleanup,
`Validate.UrlPath` is named `Validate.RoutePath`; see the
[migration notes](../compatibility/migration.md#warning-free-api-cleanup-unreleased-owner-approved).

Preserve `EmbedIO.Utilities.Validate` and both library target frameworks. For new
.NET 10 application code, use built-in throw helpers and simple explicit checks
where they express the intended contract. Keep EmbedIO's domain validators for
URL paths, routes, tokens and MIME types. The proposed fluent `Arg` framework is
not being adopted for this request, and no guard package is added.

The proposal identifies useful goals: clear parameter names, nullable-aware
validation and separating derived values from inputs. Existing .NET APIs and
ordinary return values address those goals without asking callers to understand
wrapper inference, implicit-conversion restrictions or a second validation API.
In particular, the proposal itself notes that `var` can infer a wrapper and that
interface/delegate consumers may need `.Value`. A new public convenience API
would need a demonstrated gap and separately agreed scope.

This is an evaluated design decision, not a runtime bug fix or a claim to have
implemented the historical v4 proposal. `EmbedIO.Utilities.ArgumentValidation.Arg`
is not a shipped Neo API. The upstream discussion's illustrative code should not
be pasted as a complete current example. No utility package extraction is needed
for this decision.

## Choose checks by their contract

| Task | New .NET 10 application code | Existing/legacy-compatible behavior |
| --- | --- | --- |
| Required reference | `ArgumentNullException.ThrowIfNull(value)` | `Validate.NotNull(nameof(value), value)` returns the original value. Assign/use that return value for a non-null local. |
| Required nonempty string | `ArgumentException.ThrowIfNullOrEmpty(value)` | `Validate.NotNullOrEmpty` deliberately allows whitespace. |
| Required non-whitespace string | `ArgumentException.ThrowIfNullOrWhiteSpace(value)` | Explicitly combine a null check and whitespace check when that is your application's policy. |
| Optional reference or nullable value | Check only when present | Null stays valid only when your API deliberately allows it. |
| Inclusive numeric range | `ThrowIfLessThan` / `ThrowIfGreaterThan` | Explicit `value < minimum || value > maximum`; retain both endpoints and the intended exception type. |
| URL path, route, token or MIME type | Keep the domain validator | Generic string guards do not replace protocol/routing rules. |
| Local path plus full path | Store the returned full path separately | `Validate.LocalPath(..., getFullPath: true)` returns the derived path; the caller's original string is unchanged. |

The newer throw helpers are .NET 10 consumer APIs, not .NET Standard 2.0 APIs.
Do not introduce an unconditional call into the shared library target. Compiling
a .NET Standard example with a modern compiler also does not prove every legacy
runtime's execution support.

Parameter names are observable behavior. Caller-expression inference is handy
for direct parameters; an expression such as `count.Value` may produce a name
that differs from the public API's `count`. Pass `nameof(count)` explicitly when
preserving that name matters. Preserve exception types, null/empty/whitespace
policy, normalization and range bounds when modernizing existing code. Framework
exception messages may vary by runtime/culture; this guide does not promise
message equivalence between built-in guards and the inherited validators.

The original discussion mentions
[Community Toolkit Guard](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/diagnostics/guard),
which offers a larger set of checks and caller-expression support. It remains
useful context, but Neo does not need that additional dependency for this
proposal. No relative performance claim is made without measurements.

## Complete runnable example

Create a .NET 10 console program using the published package:

```sh
dotnet new console --framework net10.0 --name ArgumentChecks
cd ArgumentChecks
dotnet add package EmbedIO-Neo --version 1.0.2
```

Replace `Program.cs` with the complete program below. The helpers are
application code, not new EmbedIO APIs. The conditional branches also demonstrate
how the same checks can be written for a .NET Standard 2.0 consumer surface.
The executable targets .NET 10; a .NET Standard library is not itself runnable.
This example performs no network or filesystem writes.

```csharp
using System;
using System.IO;
using EmbedIO.Utilities;

Console.WriteLine("Required name: " + RequiredName("neo"));
Console.WriteLine("Optional label: " + (OptionalLabel(null) ?? "<none>"));
Console.WriteLine("Inclusive bounds: " + OptionalCount(1) + ", " + OptionalCount(5));
Console.WriteLine("Original whitespace retained: " + Validate.NotNullOrEmpty("legacyName", " ").Length);
var relative = Path.Combine("assets", "..", "content");
var fullPath = Validate.LocalPath(nameof(relative), relative, getFullPath: true);
Console.WriteLine("Original path retained: " + (relative == Path.Combine("assets", "..", "content")));
Console.WriteLine("Derived path is absolute: " + Path.IsPathRooted(fullPath));
Console.WriteLine("API base: " + Validate.UrlPath("basePath", "/api//items", isBasePath: true));

// Preserve null vs empty policy and exception parameter names for your own API.
ExpectFailure(() => RequiredName(null), typeof(ArgumentNullException), "name");
ExpectFailure(() => RequiredName(" "), typeof(ArgumentException), "name");
ExpectFailure(() => OptionalLabel(""), typeof(ArgumentException), "label");
ExpectFailure(() => OptionalCount(0), typeof(ArgumentOutOfRangeException), "count");
ExpectFailure(() => OptionalCount(6), typeof(ArgumentOutOfRangeException), "count");
if (OptionalCount(null) != null) throw new Exception("Optional null policy changed.");
Console.WriteLine("Validation checks passed.");

static string RequiredName(string? name)
{
#if NET10_0
    // Existing .NET APIs infer the caller expression; no new guard package.
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    return name;
#else
    // These explicit checks also compile for .NET Standard 2.0.
    var required = Validate.NotNull(nameof(name), name);
    if (string.IsNullOrWhiteSpace(required))
        throw new ArgumentException("Name must contain non-whitespace text.", nameof(name));
    return required;
#endif
}

static string? OptionalLabel(string? label)
{
    // Null is intentionally valid; do not validate it as a required argument.
    if (label != null && string.IsNullOrWhiteSpace(label))
        throw new ArgumentException("Label must be null or non-whitespace text.", nameof(label));
    return label;
}

static int? OptionalCount(int? count)
{
    if (!count.HasValue) return null;
#if NET10_0
    // Both ends are included. Preserve the public parameter name, not "value".
    ArgumentOutOfRangeException.ThrowIfLessThan(count.Value, 1, nameof(count));
    ArgumentOutOfRangeException.ThrowIfGreaterThan(count.Value, 5, nameof(count));
#else
    if (count.Value < 1 || count.Value > 5)
        throw new ArgumentOutOfRangeException(nameof(count), count, "Count must be between 1 and 5 inclusive.");
#endif
    return count;
}

static void ExpectFailure(Action action, Type expected, string parameter)
{
    try { action(); }
    catch (ArgumentException error)
    {
        if (error.GetType() == expected && error.ParamName == parameter) return;
        throw;
    }
    throw new Exception("Expected " + expected.Name + " for " + parameter);
}
```

Run `dotnet run`. Expect:

```text
Required name: neo
Optional label: <none>
Inclusive bounds: 1, 5
Original whitespace retained: 1
Original path retained: True
Derived path is absolute: True
API base: /api/items/
Validation checks passed.
```

The checks assert rejection of a missing required name, whitespace-only required
name, empty optional label and values outside the inclusive range. They accept
both numeric endpoints and optional null. The full path is derived from the
process working directory; validation does not establish file existence or
containment inside an allowed directory. URL-path normalization collapses slashes
and adjusts a base-path trailing slash; it does not decode percent escapes.
Treat separate filesystem access/containment policy as an application concern.

## Interfaces, delegates and nullable flow

This **partial method-body example** uses the actual current return types:

```csharp
IDisposable? service = GetService(); // Existing application provisioning.
EventHandler? handler = GetHandler();
IDisposable requiredService = Validate.NotNull(nameof(service), service);
EventHandler requiredHandler = Validate.NotNull(nameof(handler), handler);
requiredService.Dispose();
requiredHandler(this, EventArgs.Empty);
```

`GetService` and `GetHandler` stand for your application's functions. There is no
wrapper, implicit conversion or `.Value` step here. Returning a validated value
is distinct from passing `ref` or rewriting the original variable; assign it
when you need a non-null local. For custom rules, use a straightforward branch
that throws the intended exception with the public parameter's name. Avoid
clock-dependent predicates in deterministic examples and specify whether a
nullable value is allowed before applying a predicate to it.

## Verification

Twenty-one compatibility cases protect original interface/delegate identities,
required-null exception types/parameter names, whitespace acceptance, local-path
rejections and derived values, URL-path normalization, token rules and media-range
policy. No production implementation or contract is changed. The exact example
is verified against published 1.0.2; its legacy branch is compiled for .NET Standard
2.0 and executed separately on a modern host, with nullable warnings treated as
errors. Cross-platform regression suites exercise operating-system path behavior.

Reference APIs:
[ThrowIfNull](https://learn.microsoft.com/en-us/dotnet/api/system.argumentnullexception.throwifnull),
[ThrowIfNullOrWhiteSpace](https://learn.microsoft.com/en-us/dotnet/api/system.argumentexception.throwifnullorwhitespace),
and [ThrowIfLessThan](https://learn.microsoft.com/en-us/dotnet/api/system.argumentoutofrangeexception.throwiflessthan).
