#!/usr/bin/env python3
"""Verify private package assets against a validated native candidate receipt."""
import hashlib
import json
from pathlib import Path
import sys
import struct
import zipfile

candidate = Path(sys.argv[1])
package = Path(sys.argv[2])
expected_rid = sys.argv[3] if len(sys.argv) > 3 else "osx-arm64"
if expected_rid not in ("osx-arm64", "osx-x64"):
    raise ValueError("Unsupported candidate RID.")
receipt = json.loads((candidate / "build-receipt.json").read_text())
if receipt.get("msquicSource") != "819ab74f851ee168504cbc392ec32e7bed1d82e9":
    raise ValueError("Unexpected MsQuic source.")
if receipt.get("rid") != expected_rid or receipt.get("productionInstalled") is not False:
    raise ValueError("Unexpected artifact scope.")
if receipt.get("tlsBackend") != "openssl":
    raise ValueError("The deployment fixture requires the OpenSSL candidate.")
if receipt.get("cryptoSource") != "45e844fa2a14ec92d146bd8f5778ac130b6625fb":
    raise ValueError("Unexpected crypto source.")
with zipfile.ZipFile(package) as archive:
    names = archive.namelist()
    if len(names) != len(set(names)):
        raise ValueError("Duplicate package entries.")
    expected = {"runtimes/" + expected_rid + "/native/" + name for name in
                ("libmsquic.2.6.2.dylib", "libmsquic.2.dylib", "libmsquic.dylib")}
    if {name for name in names if name.startswith("runtimes/")} != expected:
        raise ValueError("Unexpected native assets or RIDs.")
    # Thin 64-bit Mach-O dylibs only. Check the actual CPU type so renaming an
    # arm64 asset and receipt to osx-x64 cannot masquerade as an Intel package.
    # Apple xnu: EXTERNAL_HEADERS/mach-o/loader.h and osfmk/mach/machine.h.
    expected_cpu = {"osx-arm64": 0x0100000C, "osx-x64": 0x01000007}[expected_rid]
    for name in expected:
        data = archive.read(name)
        if hashlib.sha256(data).hexdigest() != receipt["sha256"]:
            raise ValueError("Native asset differs from candidate receipt: " + name)
        if len(data) < 32:
            raise ValueError("Truncated Mach-O header: " + name)
        magic, cpu, _, file_type, _, _, _, _ = struct.unpack("<8I", data[:32])
        if magic != 0xFEEDFACF or cpu != expected_cpu or file_type != 6:
            raise ValueError("Native Mach-O architecture/type differs from requested RID: " + name)
    for name in ("MsQuic-LICENSE", "MsQuic-THIRD-PARTY-NOTICES", "openssl-LICENSE.txt"):
        if archive.read("licenses/" + name) != (candidate / "licenses" / name).read_bytes():
            raise ValueError("License/notice differs: " + name)
    if archive.read("lib/net10.0/_._") != b"":
        raise ValueError("Unexpected framework placeholder.")
    if json.loads(archive.read("build-receipt.json")) != receipt:
        raise ValueError("Package receipt differs.")
print(json.dumps({"packageSha256": hashlib.sha256(package.read_bytes()).hexdigest(),
                  "nativeSha256": receipt["sha256"], "rid": expected_rid,
                  "verifiedNativeAssets": len(expected), "verifiedNativeArchitecture": True, "noticesMatch": True}))
