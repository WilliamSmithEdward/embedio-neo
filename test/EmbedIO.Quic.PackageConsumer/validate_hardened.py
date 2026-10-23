"""Run the package consumers as signed macOS apps with the hardened runtime.

validate.py first builds and checks the consumers on the actual RID. This script then
signs two shapes an application ships: the Native AOT consumer as an .app bundle, and a
self-contained JIT consumer as a flat directory, whose managed .dll files cannot sit in
an .app's Contents/MacOS. Each is signed inside out, the native runtime first, and the
entry point with the hardened runtime, then runs every cipher profile and the
missing-runtime control.

The hardened runtime enables library validation: a process may load only libraries signed
by Apple or with its own Team ID. An ad-hoc signature (--identity -, the default) has no
Team ID, so ad-hoc apps need com.apple.security.cs.disable-library-validation, and a
control without it must be refused with "different Team IDs", which shows the hardened
runtime was enforced. Library validation needs System Integrity Protection: where
csrutil reports it disabled, as on some hosted runners, the controls are recorded, not
required. With a Developer ID identity, every file is signed by one team and
the AOT app runs with no entitlements; JIT needs com.apple.security.cs.allow-jit either way.
"""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import zipfile
from defusedxml import ElementTree as SafeET

sys.path.insert(0, str(Path(__file__).resolve().parent))
from validate import CIPHERS, ROOT, require, verify_transitive_runtime  # noqa: E402

LIBRARY = "libembedio_quic_tls.dylib"
ENTRY = "EmbedIO.Quic.Development"
JIT = "com.apple.security.cs.allow-jit"
LIBRARY_VALIDATION = "com.apple.security.cs.disable-library-validation"


def write_entitlements(path, keys):
    path.write_bytes(plistlib.dumps({key: True for key in keys}))


def sign(path, identity, log, entitlements=None):
    command = ["codesign", "--force", "--sign", identity, "--timestamp=none"]
    if entitlements is not None:
        command += ["--options", "runtime", "--entitlements", str(entitlements)]
    with log.open("ab") as stream:
        subprocess.run(command + [str(path)], stdout=stream, stderr=subprocess.STDOUT, check=True, timeout=120)


def signature(path):
    """The code directory flags and entitlements codesign reports for one signed path."""
    details = subprocess.run(["codesign", "--display", "--verbose=2", str(path)], capture_output=True, text=True, check=True)
    entitlements = subprocess.run(["codesign", "--display", "--entitlements", "-", "--xml", str(path)],
                                  capture_output=True, check=True).stdout
    flags = next((line.split("flags=", 1)[1].split(" ", 1)[0] for line in details.stderr.splitlines() if "flags=" in line), "")
    team = next((line.split("=", 1)[1] for line in details.stderr.splitlines() if line.startswith("TeamIdentifier=")), "")
    granted = sorted(plistlib.loads(entitlements)) if entitlements.strip() else []
    return {"flags": flags, "teamIdentifier": team, "entitlements": granted}


def bundle(source, app):
    """An .app bundle holding the AOT consumer and its native runtime in Contents/MacOS."""
    executables = app / "Contents" / "MacOS"
    executables.mkdir(parents=True)
    for name in (ENTRY, LIBRARY):
        if (source / name).exists():
            shutil.copy2(source / name, executables / name)
    (app / "Contents" / "Info.plist").write_bytes(plistlib.dumps({
        "CFBundleExecutable": ENTRY, "CFBundleIdentifier": "org.embedio.quic.hardened-consumer",
        "CFBundleName": "EmbedIO QUIC consumer", "CFBundlePackageType": "APPL", "CFBundleShortVersionString": "1.0"}))
    return executables / ENTRY


def mach_o(directory):
    """Every Mach-O file in a flat publish directory, other than the entry point."""
    found = []
    for path in sorted(directory.iterdir()):
        if path.is_file() and path.name != ENTRY:
            with path.open("rb") as stream:
                magic = stream.read(4)
            if magic in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe"):
                found.append(path)
    return found


def validate(args):
    require(sys.platform == "darwin", "Hardened-runtime apps need macOS")
    args.feed, args.consumers, args.output = args.feed.resolve(), args.consumers.resolve(), args.output.resolve()
    receipt = json.loads((args.consumers / "receipt.json").read_text(encoding="utf-8"))
    require(receipt.get("passed") is True and receipt.get("rid") == args.rid,
            "The consumers did not pass validate.py on this RID")
    version = SafeET.parse(ROOT / "Directory.Build.props", forbid_dtd=True, forbid_entities=True,
                           forbid_external=True).findtext(".//Version")
    with zipfile.ZipFile(args.feed / f"EmbedIO-Neo.Quic.Native.{version}.nupkg") as package:
        native_bytes = package.read(f"runtimes/{args.rid}/native/{LIBRARY}")
    require(hashlib.sha256(native_bytes).hexdigest() == receipt["nativeSha256"],
            "The feed's native runtime differs from the one the consumers were checked with")
    args.output.mkdir(parents=True, exist_ok=False)
    adhoc = args.identity == "-"
    # Library validation is enforced only with System Integrity Protection on. Hosted macOS
    # runners may have it off, and there the controls below load the library; they are then
    # recorded rather than required.
    sip = subprocess.run(["csrutil", "status"], capture_output=True, text=True).stdout.strip()
    enforced = "enabled" in sip.lower() and "disabled" not in sip.lower()
    controls = {}
    steps = []
    environment = {**os.environ, "NUGET_PACKAGES": str(args.output / "nuget"),
                   "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}

    def run(command, name, timeout=300, expect_failure=None):
        log = args.output / (name + ".log")
        with log.open("wb") as stream:
            result = subprocess.run(command, cwd=ROOT, env=environment, stdout=stream, stderr=subprocess.STDOUT, timeout=timeout)
        steps.append({"name": name, "exit": result.returncode})
        if expect_failure is None:
            require(result.returncode == 0, "Hardened step failed: " + name)
        else:
            stderr_log = args.output / name / "host.stderr.log"
            stderr = stderr_log.read_text(encoding="utf-8", errors="replace") if stderr_log.exists() else ""
            refused = result.returncode != 0 and expect_failure in stderr
            controls[name] = "refused" if refused else "loaded" if result.returncode == 0 else "failed"
            if enforced:
                require(result.returncode != 0, "Hardened control unexpectedly passed: " + name)
                require(expect_failure in stderr, "Hardened control failed for another reason: " + name)
            else:
                require(result.returncode == 0 or refused, "Hardened control failed for another reason: " + name)

    def verify(entry, name, missing=False, expect_failure=None):
        command = [str(args.peer), str(ROOT / "test/EmbedIO.Quic.PackageConsumer/verify.py"), "--entry", str(entry),
                   "--native", "--output", str(args.output / name)]
        if missing:
            command += ["--missing-tls", "--cipher", CIPHERS[0]]
            run(command, name, 30)
            return
        if expect_failure is not None:
            run(command + ["--cipher", CIPHERS[0]], name, 60, expect_failure)
            return
        for cipher in CIPHERS:
            run(command[:-2] + ["--cipher", cipher, "--output", str(args.output / (name + "-" + cipher))], name + "-" + cipher, 60)

    def signed(source_entry, files, name, keys, layout):
        """Copies files into a fresh layout, signs it inside out and returns its entry point."""
        log = args.output / (name + "-sign.log")
        entitlements = args.output / (name + ".entitlements")
        write_entitlements(entitlements, keys)
        if layout == "app":
            app = args.output / "layouts" / name / "Consumer.app"
            entry = bundle(source_entry.parent, app)
            if LIBRARY not in files and (entry.parent / LIBRARY).exists():
                (entry.parent / LIBRARY).unlink()
            for path in mach_o(entry.parent):
                sign(path, args.identity, log)
            sign(app, args.identity, log, entitlements)
            subprocess.run(["codesign", "--verify", "--strict", "--verbose=2", str(app)], check=True, capture_output=True)
        else:
            directory = args.output / "layouts" / name / "app"
            shutil.copytree(source_entry.parent, directory, ignore=shutil.ignore_patterns(*(["*.dSYM"] + ([] if LIBRARY in files else [LIBRARY]))))
            entry = directory / ENTRY
            for path in mach_o(directory):
                sign(path, args.identity, log)
            sign(entry, args.identity, log, entitlements)
            subprocess.run(["codesign", "--verify", "--strict", "--verbose=2", str(entry)], check=True, capture_output=True)
        record = signature(entry)
        require("runtime" in record["flags"], "The entry point lacks the hardened runtime: " + name)
        require(record["entitlements"] == sorted(keys), "Unexpected entitlements: " + name)
        if LIBRARY in files:
            require((entry.parent / LIBRARY).is_file(), "The signed layout lost the native runtime: " + name)
        return entry, record

    signatures = {}
    # Native AOT, as an .app bundle.
    aot_keys = [LIBRARY_VALIDATION] if adhoc else []
    aot = args.consumers / "aot" / ENTRY
    require((aot.parent / LIBRARY).read_bytes() == native_bytes, "The AOT consumer does not carry the package native asset")
    entry, signatures["aot-app"] = signed(aot, {ENTRY, LIBRARY}, "aot-app", aot_keys, "app")
    verify(entry, "aot-app")
    entry, signatures["aot-app-missing-runtime"] = signed(aot, {ENTRY}, "aot-app-missing-runtime", aot_keys, "app")
    verify(entry, "aot-app-missing-tls", missing=True)
    if adhoc:
        entry, signatures["aot-app-library-validation"] = signed(aot, {ENTRY, LIBRARY}, "aot-app-library-validation", [], "app")
        verify(entry, "aot-app-library-validation", expect_failure="different Team IDs")

    # Self-contained JIT, as a flat directory.
    consumer = str(ROOT / "test/EmbedIO.Quic.PackageConsumer/EmbedIO.Quic.PackageConsumer.csproj")
    lock = args.output / "self-contained.lock.json"
    properties = ["-r", args.rid, "--self-contained", "true", "-p:NuGetLockFilePath=" + str(lock)]
    run([args.dotnet, "restore", consumer, "--configfile", str(args.consumers / "nuget.config"), "--use-lock-file",
         "-r", args.rid, "-p:SelfContained=true", "-p:NuGetLockFilePath=" + str(lock)], "self-contained-restore")
    verify_transitive_runtime(lock, version)
    published = args.output / "self-contained"
    run([args.dotnet, "publish", consumer, "--no-restore", "-c", "Release", "-o", str(published), *properties],
        "self-contained-publish")
    require((published / LIBRARY).read_bytes() == native_bytes, "The self-contained publish did not select the package native asset")
    jit_keys = [JIT] + ([LIBRARY_VALIDATION] if adhoc else [])
    entry, signatures["jit"] = signed(published / ENTRY, {ENTRY, LIBRARY}, "jit", jit_keys, "directory")
    verify(entry, "jit")
    entry, signatures["jit-missing-runtime"] = signed(published / ENTRY, {ENTRY}, "jit-missing-runtime", jit_keys, "directory")
    verify(entry, "jit-missing-tls", missing=True)
    if adhoc:
        entry, signatures["jit-library-validation"] = signed(published / ENTRY, {ENTRY, LIBRARY}, "jit-library-validation", [JIT], "directory")
        verify(entry, "jit-library-validation", expect_failure="different Team IDs")
    shutil.rmtree(args.output / "nuget", ignore_errors=True)

    sw_vers = subprocess.run(["sw_vers"], capture_output=True, text=True, check=True).stdout
    result = {"passed": True, "rid": args.rid, "identity": "ad-hoc" if adhoc else "developer-identity",
              "macos": {key.strip(): value.strip() for key, value in (line.split(":", 1) for line in sw_vers.splitlines() if ":" in line)},
              "nativeSha256": receipt["nativeSha256"], "sourceCommit": receipt["sourceCommit"],
              "systemIntegrityProtection": sip, "libraryValidationEnforced": enforced, "controls": controls,
              "signatures": signatures, "steps": steps,
              "scope": ("Signed with the hardened runtime and checked by codesign --verify --strict: the Native AOT consumer "
                        "as an .app bundle and a self-contained JIT consumer, all cipher profiles and the missing-runtime "
                        "control. Not notarized, not Gatekeeper-assessed, not App Sandbox.")}
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "rid": args.rid, "identity": result["identity"], "libraryValidationEnforced": enforced,
                      "controls": controls, "steps": len(steps)}), flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rid", choices=("osx-x64", "osx-arm64"), required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--consumers", type=Path, required=True, help="validate.py output for this RID")
    parser.add_argument("--peer", type=Path, required=True, help="Python of the independent peer environment")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--identity", default="-", help="codesign identity; - (ad-hoc) by default")
    validate(parser.parse_args())


if __name__ == "__main__":
    main()
