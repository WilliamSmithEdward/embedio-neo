# Reach an Android emulator server from your computer

For a server running **inside** the Android emulator, forward a host port to the
app's listening port, then browse the **host loopback address**. The emulator's
`10.0.2.16` address is not the address to enter in the host browser for this
forwarding setup.

This answers [upstream #536](https://github.com/unosquare/embedio/issues/536),
reported by sachinkanadia using Xamarin Forms on a Pixel 3a/API 29 emulator.
ferhrosa correctly pointed out the host's `localhost` URL in the
[follow-up](https://github.com/unosquare/embedio/issues/536#issuecomment-1160746056).
The existing Xamarin application and exact device configuration were not supplied;
the validation below uses our test-only MAUI app on Android 10/API 29.

## Host browser to emulator app

Start the app's server inside the emulator. For local development through ADB,
bind to **device loopback**; ADB's TCP forward connects there. A listener bound
only to the guest's Wi-Fi address may not accept that connection. This is an
application configuration choice, not a changed library default.

The following is a **replacement listener configuration fragment**, not a
complete app or a second listener to run alongside the existing one:

```csharp
new WebServer(options => options
    .WithUrlPrefix("http://127.0.0.1:8080/")
    .WithMode(HttpListenerMode.EmbedIO))
```

Keep your existing modules, app-owned server lifetime and cancellation/disposal.
If you need a full hosting example, see [Android hosting](../platforms/maui-android.md).
Do not broaden the bind to all interfaces just to make host debugging work.

Run these commands on your **computer**, using Android SDK Platform-Tools. First
identify the running emulator:

```sh
adb devices
```

Replace `emulator-5554` below with its actual serial. The emulator must appear as
`device`; start it or resolve its offline status first. Keep the app running.

```sh
adb -s emulator-5554 forward --no-rebind tcp:18080 tcp:8080
adb -s emulator-5554 forward --list
curl --noproxy "*" --max-time 10 http://127.0.0.1:18080/api/ChangeBackGround
```

On Windows PowerShell, use `curl.exe` for the last command. Or enter
`http://127.0.0.1:18080/api/ChangeBackGround` in the host browser. The expected
response is **your configured route's response**; this guide does not introduce
the original application's controller or assume its payload. Use the route's
actual HTTP verb and exact spelling (`ChangeBackGround` in the original report).

The forwarding table should include:

```text
emulator-5554 tcp:18080 tcp:8080
```

`18080` is the **host** port and can differ from `8080`, the **device** port.
Neither `5554` (emulator console) nor `5555` (commonly emulator ADB transport)
is your application's server port. `--no-rebind` rejects an existing mapping
instead of replacing someone else's. Choose a free host port if it is occupied.

When finished, remove only the mapping you created:

```sh
adb -s emulator-5554 forward --remove tcp:18080
```

Do not use `--remove-all` on a shared development setup. Forwarding is temporary;
check/recreate it after reconnecting or restarting the emulator/ADB server.

## Which direction does each address or command mean?

| Task | What to use |
| --- | --- |
| Host browser calls server in emulator | `adb forward tcp:HOST_PORT tcp:DEVICE_PORT`, then host `127.0.0.1:HOST_PORT` |
| Emulator app calls server on computer | Emulator's special `10.0.2.2` host-loopback alias, or an intentionally configured `adb reverse` mapping |
| App calls its own loopback listener | Device `127.0.0.1:DEVICE_PORT` |
| Emulator console redirects host to guest | `redir add tcp:HOST_PORT:DEVICE_PORT`, then host `127.0.0.1:HOST_PORT`; guest destination/bind must match |

For the report's console command, `redir add tcp:8080:8080` means host 8080 to
guest 8080, so the **host** URL is `http://127.0.0.1:8080/...`.
`redir add tcp:8080:5555` targets guest port 5555, not the reported listener on
8080. Console redirection and ADB forwarding are alternatives, with different
guest destinations; do not assume a loopback-only app works through console
redirection. Use the ADB recipe above for this local-only configuration.

Google documents [ADB forwarding](https://developer.android.com/tools/adb#forwardports),
[console redirection](https://developer.android.com/studio/run/emulator-networking-interconnect)
and [emulator address space](https://developer.android.com/studio/run/emulator-networking-address).
Guest Ethernet/Wi-Fi addresses and sharing vary with emulator versions, so the
old distinction between `10.0.2.15` and `10.0.2.16` alone is not a diagnosis.
rdeago's [original response](https://github.com/unosquare/embedio/issues/536#issuecomment-1062710145)
helpfully linked Google's networking guidance; use the current documentation
and the forwarding direction when investigating this case.

## If it still fails

1. Check app startup logs: the managed listener must actually be listening on
   device loopback at the configured port. The app must have Android's Internet
   permission; the [hosting guide](../platforms/maui-android.md) explains app setup.
2. Verify the selected serial and forwarding table. A successful forwarding
   command establishes a mapping; it does not prove a server is listening on
   the destination port.
3. Use the explicit host IPv4 loopback URL and the diagnostic curl command above
   to avoid a proxy or IPv6 `localhost` mismatch. A timeout/refusal/reset is a
   transport symptom. A real HTTP 404/405 means an HTTP server responded: inspect
   route/module registration and verb rather than changing forwarding ports.
4. Keep the server owned by the application, not a disposed Activity or temporary
   method scope. Check logs when the app resumes or the server is replaced.
5. Do not disable the Windows firewall. If endpoint protection blocks ADB, use
   its diagnostics and a narrowly scoped local development exception through
   your administrator; forwarding is not a reason to expose the server broadly.

The host browser does not use the Android app's HTTP client policy. If an
**Android client/WebView** separately calls a cleartext HTTP endpoint, its
cleartext policy is a distinct concern. See
[MAUI HTTPS validation](../platforms/maui-https-validation.md) for trusted HTTPS
testing; do not disable certificate verification as a forwarding workaround.

## Validation and limits

The Android smoke fixture binds the managed listener to device `127.0.0.1`.
Its host harness selects one online emulator explicitly and uses `--no-rebind`.
It verifies a real JSON response through a different, dynamically allocated
host port, checks the forwarding table and app process, verifies a conflicting
`--no-rebind` attempt is rejected without changing the destination, removes that mapping
and requires the host request to fail, confirms the other mapping stays healthy,
then recreates it and reaches the same app process. Only owned mappings are
removed during cleanup, and evidence is retained with the Android CI artifact.

The fixture retains its real static/API traffic and lifecycle/shutdown checks.
It runs on Android 10/API 29; desktop tests alone cannot verify emulator routing.
The exact original Xamarin app, Pixel 3a profile and Wi-Fi-only bind are not
claimed reproduced or repaired. If the recipe does not resolve your case,
please provide a minimal runnable app, listener prefix, emulator/Platform-Tools
versions, selected serial, forwarding table and sanitized startup/error logs.
