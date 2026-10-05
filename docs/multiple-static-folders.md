# Serving multiple static folders

[Upstream #599](https://github.com/unosquare/embedio/issues/599) registers two
static folders at `/` and asks why the second is not reached. Multiple static
folders are already supported; choose their URL mounts and missing-file behavior
explicitly. No new dependency or change to routing defaults is required.

## A subfolder may need only one mount

If `analytics` is inside `HtmlRootPath`, a single root mount already serves
`HtmlRootPath/analytics/index.html` at `/analytics/` (with the default document
configuration). It does not require a second root mount.

The Boolean argument to `WithStaticFolder` means **files are immutable during the
server's lifetime**, not "continue to the next module". Content caching is a
separate setting. Use `false` for mutable files where that provider configuration
is supported; changing the Boolean does not change routing finality.

## Mount folders at distinct URLs

This configuration fragment uses the reporter's application-supplied URL, paths,
cache setting, and controller types:

```csharp
using System.IO;
using EmbedIO;
using EmbedIO.Files;
using EmbedIO.WebApi;

var analyticsRoot = Path.Combine(HtmlRootPath, "analytics");
var server = new WebServer(o => o.WithUrlPrefix(url)
        .WithMode(HttpListenerMode.EmbedIO))
    .WithLocalSessionManager()
    .WithWebApi("/api", m => m
        .WithController(() => new ApiController())
        .WithController(() => new AnalyticsController()))
    .WithStaticFolder("/analytics", analyticsRoot, true,
        m => m.WithContentCaching(UseFileCache))
    .WithStaticFolder("/", HtmlRootPath, true,
        m => m.WithContentCaching(UseFileCache));
```

Register the API and the more specific `/analytics` mount **before** the root
mount. Modules run in registration order; the server does not automatically sort
them by route specificity. `/analytics/shared.txt` is relative to `analyticsRoot`,
while `/shared.txt` is relative to `HtmlRootPath`. Use `Path.Combine` rather than a
hard-coded backslash for filesystem paths; URL routes use `/` on every platform.
Start and observe the server task as described in the README.

Each file module sends 404 for a missing resource by default. A root mount
matches every path, so putting it first can prevent later modules from running.
Distinct mounts are not a filesystem access boundary: a root-mounted directory
also exposes its ordinary child files, subject to the provider's checks. Choose
the served directory carefully.

## Search two folders at the same URL root

If the intention is an ordered overlay, configure missing-file pass-through:

```csharp
server.WithStaticFolder("/", firstRoot, true, m => m
        .WithContentCaching(UseFileCache)
        .HandleMappingFailed(FileRequestHandler.PassThrough))
    .WithStaticFolder("/", secondRoot, true, m => m
        .WithContentCaching(UseFileCache));
```

This is an **alternative** to the distinct mounts above; configure it before
starting the server. A file in the first folder wins if both contain the same
relative path. A missing file in the first folder is looked up in the second.
If neither contains it, the second module retains its default 404 behavior.

The reporter's final `ActionModule` does not run after a file module's default
404. To use a custom final response, also configure the last file module with
`HandleMappingFailed(FileRequestHandler.PassThrough)` and register the fallback
after both folders. Set the response status explicitly if it should be 404:

```csharp
server.OnAny(context =>
{
    context.Response.StatusCode = 404;
    return context.SendDataAsync(new { Message = "Error" });
});
```

Pass-through here applies to **mapping failures**. It does not turn permission
failures, unsupported methods, or every exception into fallback routing. A found
directory without an index can have different behavior from an absent path;
configure its directory handler separately if needed. With overlapping mounts,
keep API handlers before file modules and check that fallback content matches
your intended public URL contract.

Eight regression cases exercise real temporary directories through the
in-process HTTP pipeline with content caching on and off: nested child serving,
default same-root shadowing, distinct mounts with API precedence, and ordered
overlays with collision priority and a custom final 404. The original report did
not include the requested URL or actual error, so these tests verify the described
configuration rules rather than an exact reproduction of its application.
