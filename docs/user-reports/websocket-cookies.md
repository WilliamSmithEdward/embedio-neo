# Cookies in WebSocket handshakes

[Upstream #583](https://github.com/unosquare/embedio/issues/583), reported by
`anatoly-abramov`, describes multiple cookies appearing as one comma-joined
`Set-Cookie` value when opening a WebSocket. The report used WebView2 on Windows
10 and Android WebView on a Samsung SM-P613 running Android 13.

## The wire-format correction

The managed EmbedIO listener stored each outgoing cookie separately but used a
header-collection indexer when writing the HTTP 101 response. That indexer joins
multiple values with commas. For example, two incoming cookies produced:

```http
Set-Cookie: culture=en,token=demo
```

The corrected handshake writes each stored cookie value as its own field:

```http
Set-Cookie: culture=en
Set-Cookie: token=demo
```

This preserves the legacy cookie values and changes their HTTP serialization.
It does not split a combined string on commas: a quoted cookie value may itself
contain a comma, and an `Expires` attribute can contain one too.
[RFC 6265 section 3](https://www.rfc-editor.org/rfc/rfc6265.html#section-3)
explains why folding `Set-Cookie` fields is unsafe.
Other handshake headers retain their existing serialization.

The correction is included starting with EmbedIO-Neo 1.0.2.
Build the current repository to test it before the next owner-authorized release.
No namespace, dependency, target framework or configuration change is required.

## Why are request cookies sent back?

Echoing incoming cookies is legacy behavior of the managed EmbedIO listener.
It is not required by WebSocket: the handshake may contain cookies, but the
protocol does not require reflecting incoming cookies into `Set-Cookie`.
See [RFC 6455 section 4.2.2](https://www.rfc-editor.org/rfc/rfc6455.html#section-4.2.2).
The native Microsoft listener does not automatically echo request cookies;
switching listener modes solely to change cookies can introduce other platform
and hosting differences and is not required for this fix.

The managed handshake emits cookies configured with `context.Response.SetCookie`
or `context.Response.Cookies.Add`, using the same attribute formatter as ordinary
HTTP responses. Explicit response cookies take precedence over request-cookie
echoes with the same name (case-insensitive); multiple explicitly configured
cookies with that name and different scopes remain separate. Other incoming
cookies retain the legacy echo behavior.

`LocalSessionManager` adds its cookie to both collections before the upgrade.
The response cookie is emitted once with its configured scope, expiry and
HttpOnly policy. Removing the fallback request-cookie echo is a separate
compatibility change requiring William's explicit approval.

A request's `Cookie` header contains name/value pairs, not the original cookie's
`HttpOnly`, `Secure`, `SameSite`, expiry or scope attributes. Echoing those pairs
is not equivalent to recreating the original browser cookie with all its
attributes. Configure those attributes on response cookies rather than relying
on request reflection. Response-cookie omission in managed upgrades is corrected
by [issue #64](https://github.com/WilliamSmithEdward/embedio-neo/issues/64); native
HttpOnly/Secure serialization is corrected by
[issue #59](https://github.com/WilliamSmithEdward/embedio-neo/issues/59).
`System.Net.Cookie` does not expose a SameSite property. The backend echo difference
remains intentional compatibility behavior; request reflection is not an
attribute-preservation mechanism.

Incoming cookies remain available to WebSocket handlers through
`IWebSocketContext.Cookies`. WebView cookie-manager calls in the original report
remain client-specific; this change requires no new client API.

## Validation and remaining limits

Response-cookie regressions cover both listener modes, SetCookie and Cookies.Add,
replacement, same-name request collisions, explicit scopes, expiry and security
attributes, quoted commas, rejected upgrades, normal socket closure and a fresh
HTTP health request.

Real TCP upgrade tests inspect individual header lines for zero, one and multiple
cookies, quoted commas, empty values, equals signs, session creation and
subprotocol negotiation. Each accepted connection completes a normal WebSocket
close, followed by a fresh HTTP health request. Real .NET WebSocket clients on
both listener modes verify incoming cookie values, the documented backend echo
difference, message exchange and normal closure.

The original comma-folding defect was reproduced before the fix. The Windows
10/WebView2 and Samsung/Android 13 device combinations were not reproduced;
the protocol-level regression does not depend on those particular cookie
manager APIs. If those clients still fail against the corrected build, provide
a minimal server/client, runtime and listener mode, with redacted raw request
and response headers. Never post real authentication cookie values.
