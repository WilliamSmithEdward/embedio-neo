#!/usr/bin/env python3
"""Verify private package assets against a validated native candidate receipt."""
import hashlib
import json
from pathlib import Path
import sys
import zipfile

candidate = Path(sys.argv[1])
package = Path(sys.argv[2])
receipt = json.loads((candidate / "build-receipt.json").read_text())
if receipt.get("tlsBackend") != "openssl":
    raise ValueError("The deployment fixture requires the OpenSSL candidate.")
if receipt.get("cryptoSource") != "45e844fa2a14ec92d146bd8f5778ac130b6625fb":
    raise ValueError("Unexpected crypto source.")
with zipfile.ZipFile(package) as archive:
    names = archive.namelist()
    if len(names) != len(set(names)):
        raise ValueError("Duplicate package entries.")
    expected = {"runtimes/osx-arm64/native/" + name for name in
                ("libmsquic.2.6.2.dylib", "libmsquic.2.dylib", "libmsquic.dylib")}
    if {name for name in names if name.startswith("runtimes/")} != expected:
        raise ValueError("Unexpected native assets or RIDs.")
    for name in expected:
        if hashlib.sha256(archive.read(name)).hexdigest() != receipt["sha256"]:
            raise ValueError("Native asset differs from candidate receipt: " + name)
    for name in ("MsQuic-LICENSE", "MsQuic-THIRD-PARTY-NOTICES", "openssl-LICENSE.txt"):
        if archive.read("licenses/" + name) != (candidate / "licenses" / name).read_bytes():
            raise ValueError("License/notice differs: " + name)
    if archive.read("lib/net10.0/_._") != b"":
        raise ValueError("Unexpected framework placeholder.")
    if json.loads(archive.read("build-receipt.json")) != receipt:
        raise ValueError("Package receipt differs.")
print(json.dumps({"packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                  "nativeSha256": receipt["sha256"], "rid": "osx-arm64",
                  "verifiedNativeAssets": len(expected), "noticesMatch": True}))