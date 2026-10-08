# Malformed typed route parameters

While investigating [upstream #505](https://github.com/unosquare/embedio/issues/505),
reported by `simontuffley` with discussion by `bdurrer` and former maintainer
`rdeago`, Neo validation found a separate binding defect: a route such as
`/rooms/{roomId}` with a `ushort roomId` argument returned HTTP 500 for
`not-number` or `65536`, before the handler method could run. The original
binary-response support answer is documented in
[binary controller responses](binary-controller-responses.md) and its tracker
[#150](https://github.com/WilliamSmithEdward/embedio-neo/issues/150) is closed.
The separate binding change is tracked in
[#163](https://github.com/WilliamSmithEdward/embedio-neo/issues/163).

William explicitly approved changing malformed supported route values from
HTTP 500 to **400 Bad Request**. This is an approved compatibility change,
**unreleased**; published EmbedIO-Neo 1.0.3 retains the old automatic-binding
status. No new release version or date is promised.

## Resulting behavior

The conversion expression used for controller route arguments reports a 400
when a supported string converter rejects a nonempty value with an argument,
format or overflow error. Legacy Framework's verified BCL numeric exception
wrapper is also recognized at this boundary, without treating arbitrary
application wrappers as client errors. The handler method is not invoked. The response uses
the configured HTTP exception handler; the default message is generic and does
not echo the supplied value. Controller construction/preprocessing can still
occur before binding, as before.

For a `ushort` route argument, `0` and `65535` still convert successfully;
`not-number`, `-1` and `65536` produce 400. Numeric, Boolean, character, GUID,
date/time, enum, nullable and supported custom-converter inputs retain their
existing successful conversion rules, including invariant-culture numeric
parsing and case-insensitive enum conversion. Undefined numeric enum values
remain accepted by the existing converter; this change does not add enum
membership validation.

The change is confined to failed route conversion:

- Unmatched routes still return their existing 404. Method defaults with no
  matching route argument are unchanged. Optional nullable route arguments
  still bind to null when omitted.
- An optional route mapped to a nonnullable argument that cannot accept the
  missing value still produces its existing 500. Prefer a nullable argument
  for an optional typed segment; changing this configuration behavior is
  outside the approved malformed-value fix.
- Types that cannot convert from strings, unimplemented converters,
  wrong-type converter results and unexpected converter failures remain
  server errors. Converters that promise string support and reject malformed
  input with argument/format/overflow errors produce 400.
- Exceptions from controller methods remain application/server errors. A
  controller's own `FormatException` or `OverflowException` is not intercepted
  by the route converter.
- Query/form/body binding, explicit request-data attribute precedence,
  serializers, route matching, public APIs, framework targets and package
  dependencies are unchanged.

## Migration and application-owned validation

Clients that previously interpreted malformed route input as a retryable 500
should recognize 400 as a client validation error and correct the value rather
than retrying it unchanged. Server-side error callbacks may now see an
`HttpException` for that specific binding failure. The configured exception
handler controls the response body; do not depend on the old unexpected-error
text.

For released 1.0.3, or when you need a custom validation message, accept a
string route argument and validate it inside the handler. The complete runnable
[image-controller example](binary-controller-responses.md) already demonstrates
that approach, invalid-ID 400s and missing-file 404s. It remains valid after
this change. See the [migration guide](../compatibility/migration.md) for the
status-change summary.

## Early rejection and transport policy

Managed error responses retain their inherited forced-close behavior for both
400 and 500. This work does not introduce keep-alive for those responses or
drain arbitrary unread uploads. A client continuing a bulk body upload while
the server closes can observe a TCP reset rather than a complete error body.
Do not treat that as a guarantee of a reusable error connection. Native
HttpListener retains its own connection policy. Validate route values before
large uploads where practical and handle transport errors separately from a
received HTTP response.

A controlled partial-POST test stops sending after the early rejection, checks
the wire-level 400 (and managed close header), and verifies a healthy fresh
request. An initial bulk-upload test exposed the inherited reset behavior;
a native probe also demonstrated that waiting for EOF incorrectly assumed a
forced-close policy. Both failures and the original fixtures are retained in
local validation evidence. Neither connection policy was changed to make the
status fix pass.

## Validation

Four original invalid-ushort cases reproduced the old 500 under both listener
modes and binary-response buffering settings. The wider pre-change suite reported
42 failures among 64 cases. The final 72 real HTTP cases pass locally
against the modern core and the actual .NET Standard 2.0 asset, both hosted on
the pinned .NET 10.0.12 runtime. Coverage includes invalid syntax/overflow,
valid boundaries and nonfinite doubles, invariant culture, nullable/default
and missing-route behavior, custom conversion/configuration errors, controller
exceptions, query/body precedence, classic disposal and awaited request-aware
release, binary-response recovery and partial POST rejection.

Required Windows/Linux/macOS CI and repository security/malware gates still
apply. These checks do not claim an exact reproduction of the original
unspecified application or a repair to its already answered image-header
concern.

A permanent Windows CI probe targets .NET Framework 4.7.2 and runs on the
installed Framework runtime, exercising the actual .NET Standard 2.0 core. Its
34 checks cover numeric errors and valid hexadecimal input, nullable values,
enums, custom/application failures and unchanged query/configuration behavior.
Local execution used CLR 4.0.30319.42000 / Framework Release 533509; it does not
claim an exact 4.7.2-runtime test. Framework native URL handling leaves a
whitespace-only segment unmatched while the managed listener reaches
conversion; existing host/listener URL matching is preserved.

The legacy numeric wrapper is confirmed by Microsoft's
[Framework reference source](https://github.com/microsoft/referencesource/blob/main/System/compmod/system/componentmodel/basenumberconverter.cs)
and the real CLR probe. The guard verifies the plain exception's BCL throwing
type and a recognized parsing inner exception. No runtime-private field shim
or new conversion dependency is used.
