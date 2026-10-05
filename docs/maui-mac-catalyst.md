# MAUI Mac Catalyst: local web server startup

[Upstream issue #601](https://github.com/unosquare/embedio/issues/601) reports a
MAUI WebView app on macOS Sonoma 14.5 / Apple M1 Pro. Its screenshot shows
`PlatformNotSupportedException` from `System.Console.WindowHeight`, called by
`Swan.Terminal` and `Swan.Logging.Logger` while constructing `WebServer`.

EmbedIO-Neo 1.0.0 already removed SWAN and uses `System.Diagnostics.TraceSource`
for diagnostics. The core does not probe console dimensions. Rebuild the app
against `EmbedIO-Neo` and follow [MIGRATION.md](migration.md) for the approved
SWAN migration. The namespace remains `EmbedIO`; this is not a binary replacement
for an app compiled against upstream. Avoid leaving the original EmbedIO package
or its SWAN-based logging integration in the application. Custom trace listeners
remain application code: do not configure one that requires unsupported console
APIs in a GUI app.

## Separate networking permissions

[Yu-Core's upstream advice](https://github.com/unosquare/embedio/issues/601#issuecomment-3806818672)
identifies Mac Catalyst's network entitlements. These address sandbox permissions,
not the console API exception in the screenshot. Keep the sandbox enabled and
merge the needed keys into the app's existing `Platforms/MacCatalyst/Entitlements.plist`:

```xml
<key>com.apple.security.network.server</key>
<true/>
<key>com.apple.security.network.client</key>
<true/>
```

The server entitlement permits inbound connections; the client entitlement permits
outbound connections, such as a WebView accessing the local server. The signed app
must actually consume the file. In the MAUI app project, configure the applicable
Mac Catalyst builds, including Release:

```xml
<PropertyGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'maccatalyst'">
  <CodesignEntitlements>Platforms/MacCatalyst/Entitlements.plist</CodesignEntitlements>
</PropertyGroup>
```

See [Microsoft's entitlement setup and signing guidance](https://learn.microsoft.com/en-us/dotnet/maui/mac-catalyst/entitlements?view=net-maui-10.0)
and Apple's [server](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.network.server)
and [client](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.network.client)
entitlement references. Use an accessible local content directory and a loopback
prefix such as `http://127.0.0.1:9696/` when serving only the local app.

## Observe startup errors and server lifetime

The report discards exceptions and the task returned by `RunAsync`. Keep and observe
that task, report failures through the app's diagnostics, and retain/dispose the
server with the app's lifetime. Do not await the long-running server task directly
on a WebView initialization path: wait for the server's `Listening` state before
navigating the WebView, and observe completion or failure separately. Do not use
an empty catch block. If startup still fails, provide the full exception and inner
exception, .NET/MAUI and package versions, target framework, URL prefix, Debug or
Release configuration, and effective signed entitlements, with secrets removed.

## Validation boundary

The regression tests check the core assembly for SWAN/Console references and
construct the reported server configuration, start its real EmbedIO listener, and
fetch a temporary `index.html` over HTTP. Desktop tests do not reproduce Mac
Catalyst's runtime, code signing, WebView, or sandbox. Neither Windows nor a Linux
Docker container can establish that the full MAUI scenario works. Confirmation
still requires a signed MAUI Mac Catalyst app on Apple hardware. The migrated
tracking issue remains open pending that confirmation.

A dedicated CI smoke fixture in `test/EmbedIO.MacCatalystSmoke` builds a MAUI
app with a pinned Apple workload on `macos-26`, verifies its ad-hoc signature
and sandbox entitlements, and requires successful HTTP and WebView checks.
Its result is captured as `mac-catalyst-smoke`; the CI gate requires this job.
This is an automated test configuration, not App Store signing or the original
Sonoma/M1 environment. A passing run must be verified before citing it as evidence.

Verified evidence: [CI run 37256447885](https://github.com/WilliamSmithEdward/embedio-neo/actions/runs/37256447885)
passed the Mac Catalyst smoke job. Its uploaded result reports `passed: true`,
OS `Unix 26.6.0`, and .NET runtime `10.0.12`; its signed entitlements include
app sandbox and network client/server permissions. This confirms the automated
MAUI HTTP/WebView fixture, not the reporter's original application.
