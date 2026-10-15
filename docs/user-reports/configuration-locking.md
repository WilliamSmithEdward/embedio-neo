# Configuration locking compared with init-only properties

[Neo #49](https://github.com/WilliamSmithEdward/embedio-neo/issues/49) evaluates
the roadmap item from [upstream #546](https://github.com/unosquare/embedio/issues/546):
replace the locked configuration model with C# init-only properties. This guide
records when each configuration surface locks today, what object initialization
already allows, where validation runs, which nested objects stay mutable, one
defect the measurement found, and the decision.

## When each surface locks

| Surface | Mutable until | Enforced by |
| --- | --- | --- |
| `WebServerOptions` and its URL prefixes | The `WebServer` constructor returns | The constructor builds the listener from the options, then locks them |
| `WebServer` handlers, `SessionManager`, module collection | `RunAsync` starts | The state change from `Created` locks the server and its modules collection |
| Each module (`OnUnhandledException`, `FileModule` settings, routes) | The module starts inside `RunAsync` | `WebModuleBase.Start` locks after `OnStart` |
| `LocalSessionManager` settings | Its `Start` runs inside `RunAsync` | The manager's own lock |

A rejected assignment throws `InvalidOperationException` and leaves the existing
value in place; a running server keeps serving with the configuration it started
with. Locking is idempotent, so a locked `WebServerOptions` instance can configure
a replacement `WebServer` after the first one stops.

## Object initialization already works

Every configurable property has a public setter, so object initializers and the
fluent helpers produce the same options:

```csharp
var initialized = new WebServerOptions { Mode = HttpListenerMode.EmbedIO, AutoLoadCertificate = false };
initialized.AddUrlPrefix("http://localhost:9696/");

var fluent = new WebServerOptions()
    .WithMode(HttpListenerMode.EmbedIO)
    .WithoutAutoLoadCertificate()
    .WithUrlPrefix("http://localhost:9696/");

var files = new FileModule("/", provider) { DefaultDocument = "index.html", ContentCaching = true };
```

URL prefixes are the one surface that needs a method call rather than an
initializer, because `AddUrlPrefix` validates each prefix and rejects duplicates
at the call. `WithUrlPrefixes(...)` adds several in one expression.

## Where validation runs

Assignments validate only what a single value can show. Combinations are
validated at startup, when every setting is known regardless of assignment order:
a `LocalSessionManager` with `SameSite=None` and `CookieSecure = false` accepts
both assignments, and `RunAsync` then fails with `InvalidOperationException`
naming `CookieSecure`. The failed startup reports `Stopped` and releases its port.
Init-only properties would not move this check earlier: an object initializer
assigns members one at a time in source order, so cross-property rules still need
a validation point after all of them are set.

## Nested objects

Locking freezes a surface's own fields, not the objects it points to. The measured
cases:

- `FileModule.Cache` cannot be replaced after start, but the borrowed `FileCache`
  keeps its runtime limits (`MaxSizeKb`, `MaxFileSizeKb`) adjustable. That is
  deliberate: a cache can be shared by several modules and tuned while serving.
- A `WebServerOptions.Certificate` is an `X509Certificate2` the application owns;
  the options hold the reference, not a copy.
- Collection views were the gap. `WebServerOptions.UrlPrefixes`,
  `Modules.WithSafeNames` and `Modules.Named` returned the backing `List` and
  `Dictionary` objects typed as read-only interfaces. Casting them back allowed
  adding a URL prefix after the options were locked, or inserting a module into a
  running server's dispatch list, without any lock or duplicate check. They now
  return `ReadOnlyCollection` and `ReadOnlyDictionary` wrappers created once per
  instance; the declared types are unchanged and a view taken before locking still
  reflects later additions. Internal dispatch enumerates the backing list directly,
  which also removes a boxed enumerator from every request.

Init-only properties would not have caught the collection gap: `init` restricts
reassigning the property, not mutating the object it returns.

## Measured scenarios

`test/EmbedIO.Tests/Issues/Issue49_ConfigurationLocking.cs` covers the acceptance
criteria of #49:

| Scenario | Result |
| --- | --- |
| Object initializer and fluent setup | Equivalent options |
| Options after server construction | Mode, prefixes and request settings reject changes; prefixes unchanged |
| URL prefix view cast back to a list | Not the backing list; `Add` throws `NotSupportedException` (failed before the fix) |
| URL prefix view taken before locking | Reflects later additions; duplicates rejected |
| Locked options reused for a replacement server | Accepted |
| Server and modules before and after start | Mutable before, locked after; running server unaffected by rejected changes |
| Module views cast back during a run | Not the backing collections; insertion throws; dispatch unchanged (failed before the fix) |
| `FileModule` after start | Settings and cache replacement locked; borrowed cache limits still tunable |
| Invalid session combination | Accepted at assignment, rejected at startup, port released |
| Session settings | Locked only once the manager starts |
| Explicit `Lock()` twice | Idempotent |

## Decision

No init-only or alternative configuration model is added. The existing public
setters already support object initializers, the lock points match when each
value is consumed, and startup validation catches cross-property mistakes that
init-only properties cannot. The concrete weakness the comparison exposed, mutable
collections behind read-only interfaces, is fixed in place without changing any
public signature. The correction is new in Neo v2; published 1.0.3 returns the
backing collections.
