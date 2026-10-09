#!/usr/bin/env python3
"""Exercise package trust boundaries using mutations of the real native artifact."""
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
import zipfile

candidate = Path(sys.argv[1]).resolve()
package = Path(sys.argv[2]).resolve()
rid = sys.argv[3]
output = Path(sys.argv[4]).resolve()
output.mkdir(parents=True, exist_ok=True)
verifier = Path(__file__).with_name("verify_package.py")
subprocess.run([sys.executable, str(verifier), str(candidate), str(package), rid], check=True, capture_output=True)
with zipfile.ZipFile(package) as archive:
    original = {name: archive.read(name) for name in archive.namelist()}
receipt = json.loads(original["build-receipt.json"])
other_rid = "osx-x64" if rid == "osx-arm64" else "osx-arm64"
other_cpu = 0x01000007 if rid == "osx-arm64" else 0x0100000C
outcomes = []
with tempfile.TemporaryDirectory(prefix="native-verifier-", dir=output) as temporary:
    scratch = Path(temporary).resolve()
    if not scratch.is_relative_to(output):
        raise ValueError("Verifier scratch escaped the named evidence directory.")
    for mode in ("relabel", "wrong_cpu", "bad_magic", "bad_type", "truncated", "extra_rid", "missing_asset", "bad_notice"):
        files = dict(original)
        observed = dict(receipt)
        expected_rid = rid
        if mode == "relabel":
            expected_rid = other_rid
            observed["rid"] = other_rid
            files = {name.replace("runtimes/" + rid + "/", "runtimes/" + other_rid + "/"): data for name, data in files.items()}
        if mode in ("wrong_cpu", "bad_magic", "bad_type", "truncated"):
            data = files["runtimes/" + rid + "/native/libmsquic.dylib"]
            if mode == "wrong_cpu":
                data = data[:4] + struct.pack("<I", other_cpu) + data[8:]
            elif mode == "bad_magic":
                data = b"BAD!" + data[4:]
            elif mode == "bad_type":
                data = data[:12] + struct.pack("<I", 2) + data[16:]
            else:
                data = data[:16]
            observed["sha256"] = hashlib.sha256(data).hexdigest()
            for name in files:
                if name.startswith("runtimes/"):
                    files[name] = data
        if mode == "extra_rid":
            files["runtimes/" + other_rid + "/native/libmsquic.dylib"] = files["runtimes/" + rid + "/native/libmsquic.dylib"]
        if mode == "missing_asset":
            del files["runtimes/" + rid + "/native/libmsquic.2.dylib"]
        if mode == "bad_notice":
            files["licenses/openssl-LICENSE.txt"] = b"altered notice"
        files["build-receipt.json"] = json.dumps(observed).encode("utf-8")
        (scratch / "build-receipt.json").write_text(json.dumps(observed), encoding="utf-8")
        for license_name in ("MsQuic-LICENSE", "MsQuic-THIRD-PARTY-NOTICES", "openssl-LICENSE.txt"):
            target = scratch / "licenses" / license_name
            target.parent.mkdir(exist_ok=True)
            target.write_bytes(original["licenses/" + license_name])
        altered = scratch / "candidate.nupkg"
        with zipfile.ZipFile(altered, "w") as archive:
            for name, data in files.items():
                archive.writestr(name, data)
        result = subprocess.run([sys.executable, str(verifier), str(scratch), str(altered), expected_rid], capture_output=True, text=True)
        message = result.stderr.strip().splitlines()[-1] if result.stderr.strip() else ""
        expected = "Mach-O" if mode in ("relabel", "wrong_cpu", "bad_magic", "bad_type", "truncated") else "native assets" if mode in ("extra_rid", "missing_asset") else "License/notice"
        if result.returncode == 0 or expected not in message:
            raise ValueError("Invalid package was accepted or failed for the wrong reason: " + mode)
        outcomes.append({"mutation": mode, "rejected": True, "reason": message})
(output / "mutation-results.json").write_text(json.dumps(outcomes, indent=2), encoding="utf-8")
print(json.dumps({"rid": rid, "rejectedMutations": len(outcomes), "realPackageVerified": True}))
