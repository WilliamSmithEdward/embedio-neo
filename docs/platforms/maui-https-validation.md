# MAUI HTTPS validation

Feature [#26](https://github.com/WilliamSmithEdward/embedio-neo/issues/26) tracks
TLS hosting on desktop and in MAUI apps. The test-only
`test/EmbedIO.MauiHttpsSmoke` app runs on Windows, iOS, Mac Catalyst and Android;
it is outside the solution and all production packages. CI requires its four
jobs through `CI passed`. A configured job is supported evidence only after its
actual run and artifact have been verified.

## What runs

Each job generates a fresh, short-lived test CA and a server PFX with a private
key. The CA private key is never exported. The app imports the PFX, starts the
real EmbedIO listener and checks:

1. Encrypted responses, sequential connections and rejection of an unrelated
   self-signed certificate using the shared transport fixture.
2. A normal platform `HttpClient`, without a validation callback, retrieves the
   CA-signed HTTPS page.
3. The MAUI native WebView navigates to the same HTTPS page and returns its
   rendered DOM marker through JavaScript.
4. A separate runner process connects to the listener with strict chain and
   hostname verification, checks the served page, and verifies that a client
   without the CA rejects it.

No WebView delegate bypasses certificate errors. Windows uses a temporary entry
in the disposable runner's machine root store, Mac Catalyst uses a temporary root
and trust setting in the runner's system keychain, iOS uses a fresh simulator
keychain, and Android uses a test-APK network-security configuration that trusts
the generated root only for `127.0.0.1`. Installed CA certificates are removed in
`finally` and the created iOS simulator is deleted. Mac Catalyst removes the CA
certificate from the keychain; residual administrative trust metadata expires
with the disposable runner because macOS can require an interactive dialog to
remove it. The provisioning script refuses to run
outside GitHub Actions. Never copy the test credentials or trust policy into a
production app.

The fixture uses port 59626. Results retain the exact OS/runtime, native platform,
individual check statuses and host URL. Failures retain app/logcat reports where
available. Generated private keys and app binaries are excluded from uploaded
test artifacts. The existing Android HTTP lifecycle and Mac Catalyst HTTP/WebView
smokes continue to run independently.

## Reproduce CI

The SDK and workload set are pinned to 10.0.401 and MAUI Controls to 10.0.110;
every target has its own committed dependency lock. Runners are Windows 2025,
Ubuntu 24.04 and macOS 26. Apple uses Xcode 26.6 and the iOS 26.5 simulator;
Android uses Android 10/API 29.

```sh
gh workflow run ci.yml --ref <branch>
```

Download `maui-https-windows`, `maui-https-ios`, `maui-https-maccatalyst` and
`maui-https-android` from the run. Each `result.json` must report `passed: true`
and every named check must pass. A build pass alone is insufficient. The
`update-https-locks` dispatch option deliberately regenerates test-only locks;
download, review and commit those files, then rerun ordinary locked CI before
merging. Ordinary PR runs never update dependency locks.

## Physical-device test procedure

Hosted runners establish Windows, simulator/emulator and sandboxed Mac Catalyst
behavior. They cannot establish physical-device Wi-Fi/firewall behavior or
Scobie's Android 7.1.2/Xamarin.Forms runtime. The strict client below is reusable
on a separate machine without altering its OS trust store:

```sh
dotnet run --project test/EmbedIO.HttpsCertificates -- TestResults/maui-https/certificates 192.168.1.50 device.example
python scripts/maui_https_probe.py --url https://192.168.1.50:59626/ --ca TestResults/maui-https/certificates/https-test-root.pem --output TestResults/device/result.json
```

Replace those example identities with the device's actual address/name before
building the app so they appear in the certificate SAN. Build the test app with
the appropriate `SmokePlatform`; override the runtime identifier for a physical
device (for example `android-arm64` or `ios-arm64`). iOS device deployment needs
the owner's signing identity and provisioning profile; simulator signing does
not substitute for it. Provision only the generated public CA on the test device
through its normal trust settings. Android's fixture configuration is scoped to
the loopback URL used by its in-app tests; the separate client trusts the CA file
for the device address. Never disable certificate or hostname validation.

Launch the app, ensure the test device and client can reach port 59626, then run
the probe. It waits for the in-app client/WebView checks, tests the external
connection and sends `/finish`, which stops the test app. Record the JSON along
with the device model, OS/API level, network setup and signing configuration.
Remove the test CA afterward. No physical-device pass is claimed without that
artifact. Legacy Xamarin needs a separate compatible fixture; rebuilding this
MAUI app on a newer runtime does not reproduce it.

For production certificate ownership, provisioning and client trust, see the
[HTTPS guide](../guides/https.md).
The [macOS reset guide](../user-reports/mac-accept-reset.md) describes the
runtime accept-crash mitigation and its additional thread per IPv6 endpoint.
Immediate-reset HTTP/TLS regressions and repeated macOS stress runs remain
required alongside this native HTTPS fixture.

## CI setup and timing

The HTTPS Windows job installs its pinned SDK into a private runner-temporary
directory. This keeps workload installation separate from the runner's Visual
Studio installation, whose other workloads otherwise get updated as well. The
SDK and workload versions remain 10.0.401. Each platform still performs its own
locked restore, build, native trust checks, WebView checks and external probes.

CI caches only downloaded NuGet packages, keyed by committed dependency locks;
it does not reuse compiled applications, generated certificates, simulator state
or test results. A cache miss performs an ordinary restore. The Android HTTPS
emulator starts before the app build, allowing boot to overlap compilation; the
existing readiness checks still run before application installation and testing.

Compare complete job times (including SDK setup and cache transfer), not just
workload-install times. Cold and warm runs can differ, and hosted runner queue
time is separate from execution time. These setup changes do not repair the
intermittent iOS navigation/finish failures tracked in issue #185.

Desktop regression/coverage jobs run alongside separate compatibility and
allocation-budget jobs on all three desktop operating systems. Performance
probes remain sequential within each dedicated job to avoid competing with the
regression suite on the same host. Both job groups are required by `CI passed`;
regression TRX/coverage and compatibility/probe reports have separate artifacts.

## Investigating slow or failed smoke runs

Each platform artifact includes `timings.json` with elapsed time and outcome for
every native command, including simulator boot, app launch, report collection
and cleanup. Numbered `command-*.log` files retain bounded command output; a
timeout keeps its partial output and is still a failure. `probe-events.jsonl`
and `last-probe-state.json` identify the last host request and reachable app state.
The app report contains a bounded monotonic timeline for listener startup, native
trust, WebView navigation/rendering, finish-response handling and shutdown.

On an iOS validation failure, the harness also attempts a bounded app-process
simulator log query before cleanup. Diagnostic capture cannot turn a failed trust
or rendering assertion into a pass. The existing trust checks, navigation/DOM
budgets and host request timeouts remain unchanged. This instrumentation gathers
evidence for issue #185; it does not by itself repair an intermittent failure.

Simulator report lookup, shutdown and deletion are independent cleanup attempts.
A missing app container (for example, because boot failed before installation)
cannot skip device cleanup; deletion is attempted even when shutdown fails.
`cleanup-errors.json` retains any cleanup failures. Cleanup errors fail an
otherwise successful job while preserving the original exception if validation
already failed.

The iOS job creates and starts its owned simulator immediately after checkout,
allowing startup to overlap pinned SDK/workload setup and compilation. The
240-second readiness budget is measured from the original boot start across
processes; starting it early does not grant extra boot time. The app execution
still waits for readiness and imports the generated CA before installation. An
always-run cleanup stage removes only the recorded owned simulator if setup or
compilation fails; it is a no-op after successful deletion by the execution stage.
No simulator state is cached or reused between jobs.

A background worker enforces the boot deadline independently of compilation and
atomically records readiness in `boot-result.json`; `boot-timings.json`,
`boot-command-*.log` and `boot-worker.log` retain its evidence. A slow build can
consume an already successful result; it neither resets nor extends the boot
deadline. Failure or a missing worker result stops app execution.

## Reusing the pinned iOS SDK

The iOS job uses a private SDK directory and caches SDK 10.0.401 with runtime
10.0.12, keyed by OS, architecture, global SDK configuration and the validation
script. The cache is saved immediately after verified installation, before any
workload, certificate or application build. A cache hit is validated by executing
that SDK and checking both pins; mismatched or unusable cached tools fail the job.
A miss uses the existing pinned setup-dotnet installer and verifies its result.

NuGet package caching remains separate and lockfile-based. Workload installation,
locked restore, compilation, certificate trust and every native/host HTTPS check
still run. Neither simulator state, generated keys, compiled apps nor test results
are cached. Measure complete cold and warm jobs before claiming an improvement.
