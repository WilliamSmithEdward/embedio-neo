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
