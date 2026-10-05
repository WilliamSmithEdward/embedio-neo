"""Launch the signed MAUI app and require its HTTP/WebView success report."""
import json
import pathlib
import plistlib
import subprocess
import sys
import time

output = pathlib.Path("TestResults/mac-catalyst")
with (output / "entitlements.plist").open("rb") as stream:
    entitlements = plistlib.load(stream)
for key in ("com.apple.security.app-sandbox", "com.apple.security.network.server", "com.apple.security.network.client"):
    if entitlements.get(key) is not True:
        raise SystemExit(f"Required signed entitlement missing: {key}")
container = pathlib.Path.home() / "Library/Containers/io.embedioneo.smoke601/Data"
if container.exists() and list(container.rglob("smoke-result.json")):
    raise SystemExit("Unexpected stale smoke report; use a fresh runner.")
subprocess.run(["open", "-n", str(pathlib.Path(sys.argv[1]).resolve())], check=True)
deadline = time.monotonic() + 120
while time.monotonic() < deadline:
    results = list(container.rglob("smoke-result.json")) if container.exists() else []
    if results:
        data = json.loads(results[0].read_text())
        (output / "smoke-result.json").write_text(json.dumps(data, indent=2) + "\n")
        print(json.dumps(data))
        if data.get("passed") is not True:
            raise SystemExit("Mac Catalyst smoke failed.")
        break
    time.sleep(1)
else:
    raise SystemExit("Mac Catalyst app did not produce a result within 120 seconds.")
