# Common hosting tasks without an extra abstraction layer

[Upstream discussion #561](https://github.com/unosquare/embedio/discussions/561#discussioncomment-8394073)
raised a practical concern: after using v2 for several years, bufferUnderrun
found v3's fluent configuration and split classes too verbose. This audit
addresses the concepts needed to write an application, rather than removing
public types that other applications extend.

## Start with the task

The maintained [Getting started sequence](../guides/getting-started/README.md)
provides complete programs with package installation, imports, exact requests,
expected responses and graceful cancellation. It uses existing entry points:

| Task | Application-facing entry points | Decisions needed now |
| --- | --- | --- |
| Return JSON | `WebServer`, `OnGet`, `SendDataAsync` | Listening URL, callback base path, response object |
| Serve a folder | `WebServer`, `WithStaticFolder` | URL mount, absolute directory, file-change policy |
| Combine JSON and files | The two paths above | Register API callbacks before the root static folder |
| Group exact API routes | `WithWebApi`, `WebApiController`, `Route` | Shared mount, relative routes, returned data |
| Accept input | Controller route/query arguments and `[JsonData]` | Verb, input shape, validation, status code |

`CancellationTokenSource`, `using` and `RunAsync` remain visible because the
application owns server shutdown and disposal. A smaller example that hides
that ownership would omit behavior developers need in their real application.

The first callback needs neither a module subclass nor an explicit options
object, listener mode, session store, dependency injection container or serializer.
`OnGet` matches a base path, including descendants; a controller is the next
step when exact routes or multiple related operations matter. Static-folder
examples introduce working-directory resolution and API-before-static order
where those choices first affect the response.

## Keep extension points available

Fluent calls are optional composition helpers over the existing modules. For
example, `WithStaticFolder` creates a `FileModule`; applications needing its
additional configuration can use the helper's configuration callback or create
the module explicitly. Controllers use the same server and pipeline as callbacks.
Custom `WebModuleBase` implementations and existing extension methods remain
available without requiring every newcomer to learn them first.

There is no demonstrated composition defect or specific pair of public classes
to consolidate in the source report. This decision therefore keeps the existing
API and uses the task-focused guides as the simple path. It does not add a second
facade, remove interfaces, change inheritance contracts or replace the transport.
The separate modern-listener proposal needs its own feasibility and compatibility
assessment; this onboarding audit makes no backend or performance claim.

## Validation and limits

The six Getting started program variants are tested against their stated,
pinned package version, EmbedIO-Neo 1.0.0: JSON, folder, generated HTML, combined
JSON/files, controller, and verbs/input. Checks exercise actual HTTP responses,
route/query binding, POST/PUT/DELETE, JSON bodies, invalid input returning 400,
and the documented cancellation path. These examples use established APIs;
they do not depend on unreleased features.

This is a verified documentation and design answer, not a measurement proving
that every developer prefers the API or that the original v2 application has
been migrated. A concrete task that still requires unnecessary setup is welcome:
provide the smallest runnable program and the intended response so the gap can
be evaluated without removing established extension contracts.

See [the first JSON endpoint](../guides/getting-started/README.md),
[files and APIs](../guides/getting-started/files.md),
[controllers](../guides/getting-started/controllers.md), and
[verbs and input](../guides/getting-started/requests.md).
