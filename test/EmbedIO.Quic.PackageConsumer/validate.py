"""Build and exercise package-only QUIC consumers on their actual native RID."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys
import tempfile
import venv
from xml.etree.ElementTree import Element, SubElement, ElementTree
from defusedxml import ElementTree as SafeET
import zipfile

ROOT = Path(__file__).resolve().parents[2]
RIDS = ("linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64")
CIPHERS = ("AES_128_GCM_SHA256", "AES_256_GCM_SHA384", "CHACHA20_POLY1305_SHA256")


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate(args):
    require((args.rid == "osx-x64") == bool(args.peer_wheel_directory),
            "Intel macOS requires its exact-source pinned test wheel; other RIDs use the published wheel lock")
    architecture = {"x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(platform.machine().lower())
    musl = any(Path("/lib").glob("ld-musl-*.so.1"))
    actual = ("osx-" if sys.platform == "darwin" else "linux-musl-" if musl else "linux-") + str(architecture)
    require(sys.platform in ("linux", "darwin") and actual == args.rid, "Consumer must execute on the actual target RID")
    sdk = subprocess.check_output([args.dotnet, "--version"], cwd=ROOT, text=True).strip()
    require(sdk == json.loads((ROOT / "global.json").read_text(encoding="utf-8"))["sdk"]["version"], "Consumer SDK differs from global.json")
    args.output.mkdir(parents=True, exist_ok=False)
    version = SafeET.parse(ROOT / "Directory.Build.props", forbid_dtd=True, forbid_entities=True, forbid_external=True).findtext(".//Version")
    native_package = args.feed / f"EmbedIO-Neo.Quic.Native.{version}.nupkg"
    core_package = args.feed / f"EmbedIO-Neo.{version}.nupkg"
    require(native_package.is_file() and core_package.is_file() and
            {p.name for p in args.feed.iterdir()} == {native_package.name, core_package.name, "packages.json"},
            "Missing exact-version preview packages or unexpected feed entries")
    package_hashes = {path.name: digest(path) for path in (core_package, native_package)}
    receipt_path = args.feed / "packages.json"
    receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    head = (args.preview_source_commit if args.preview and args.preview_source_commit else
            subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip())
    require(receipt["sourceCommit"] == head and receipt["packages"] == package_hashes and receipt["previewOnly"] == args.preview,
            "Package artifact differs from the workflow source or hashes")
    library = "libembedio_quic_tls.dylib" if args.rid.startswith("osx-") else "libembedio_quic_tls.so"
    with zipfile.ZipFile(native_package) as bundle:
        manifest = json.loads(bundle.read("runtime-assets.json"))
        require(manifest["rids"] == ([args.rid] if args.preview else sorted(RIDS)) and
                manifest["completeDistribution"] == (not args.preview),
                "Consumer runtime inventory differs; incomplete packages require explicit --preview")
        native_bytes = bundle.read(f"runtimes/{args.rid}/native/{library}")
        native_receipt = bundle.read(f"provenance/{args.rid}/build-receipt.json")
    peer_wheel = None
    if args.peer_wheel_directory:
        sys.path.insert(0, str(ROOT / "native/EmbedIO.Quic.Tls"))
        from build_peer_wheel import verify_artifact
        peer_wheel = verify_artifact(args.peer_wheel_directory, head, native_receipt)
        require(peer_wheel.get("host") == {"system": "Darwin", "machine": "x86_64"},
                "Test wheel was not built on Intel macOS")
    steps = []
    def run(command, name, environment, timeout=300):
        with (args.output / (name + ".log")).open("wb") as log:
            result = subprocess.run(command, cwd=ROOT, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
        steps.append({"name": name, "exit": result.returncode})
        require(result.returncode == 0, "Package consumer step failed: " + name)
    with tempfile.TemporaryDirectory(prefix="embedio-quic-package-") as temporary:
        scratch = Path(temporary)
        environment = {**os.environ, "NUGET_PACKAGES": str(scratch / "nuget"),
                       "DOTNET_NOLOGO": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
        configuration = Element("configuration")
        sources = SubElement(configuration, "packageSources")
        SubElement(sources, "clear")
        SubElement(sources, "add", key="preview", value=str(args.feed.resolve()))
        SubElement(sources, "add", key="nuget", value="https://api.nuget.org/v3/index.json")
        mappings = SubElement(configuration, "packageSourceMapping")
        preview = SubElement(mappings, "packageSource", key="preview")
        for name in ("EmbedIO-Neo", "EmbedIO-Neo.Quic.Native"):
            SubElement(preview, "package", pattern=name)
        SubElement(SubElement(mappings, "packageSource", key="nuget"), "package", pattern="*")
        config = args.output / "nuget.config"
        ElementTree(configuration).write(config, encoding="utf-8", xml_declaration=True)
        peer_root = scratch / "peer"
        venv.EnvBuilder(with_pip=True).create(peer_root)
        peer = str(peer_root / "bin/python")
        if peer_wheel:
            environment["CRYPTOGRAPHY_OPENSSL_NO_LEGACY"] = "1"
            wheel_lock = args.output / "source-peer-requirement.txt"
            wheel_lock.write_text("cryptography @ " + (args.peer_wheel_directory / peer_wheel["wheel"]).as_uri() +
                                  " --hash=sha256:" + peer_wheel["wheelSha256"] + "\n", encoding="utf-8")
            run([peer, "-m", "pip", "--isolated", "install", "--require-hashes", "--no-deps", "-r", str(wheel_lock)],
                "peer-source-wheel-install", environment)
        run([peer, "-m", "pip", "--isolated", "install", "--require-hashes", "--only-binary=:all:", "-r",
             str(ROOT / "test/EmbedIO.Conformance/drivers/requirements.txt")], "peer-restore", environment)
        if peer_wheel:
            run([peer, "-c", "import cryptography; from cryptography.hazmat.backends.openssl.backend import backend; "
                 "assert cryptography.__version__ == '50.0.2'; "
                 "assert backend.openssl_version_text().startswith('OpenSSL 3.5.9 '); "
                 "print(cryptography.__version__, backend.openssl_version_text())"], "peer-source-wheel-backend", environment)
        consumer = str(ROOT / "test/EmbedIO.Quic.PackageConsumer/EmbedIO.Quic.PackageConsumer.csproj")
        for profile in ("portable", "rid", "aot"):
            target = args.output / profile
            lock = str(args.output / (profile + ".lock.json"))
            properties = ["-p:NuGetLockFilePath=" + lock]
            if profile != "portable":
                properties += ["-r", args.rid]
            if profile == "aot":
                properties += ["-p:PublishAot=true"]
            run([args.dotnet, "restore", consumer, "--configfile", str(config), "--use-lock-file", *properties],
                profile + "-restore", environment)
            run([args.dotnet, "publish", consumer, "--no-restore", "-c", "Release", "-o", str(target), *properties],
                profile + "-publish", environment)
            native_path = target / library if profile != "portable" else target / "runtimes" / args.rid / "native" / library
            require(native_path.read_bytes() == native_bytes, "NuGet-selected native bytes differ: " + profile)
            entry = target / ("EmbedIO.Quic.Development" if profile == "aot" else "EmbedIO.Quic.Development.dll")
            for cipher in CIPHERS:
                name = profile + "-" + cipher
                command = [peer, str(ROOT / "test/EmbedIO.Quic.PackageConsumer/verify.py"), "--entry", str(entry),
                           "--cipher", cipher, "--output", str(args.output / name)]
                if profile == "aot":
                    command += ["--native"]
                run(command, name, environment, 60)
        host_project = str(ROOT / "test/EmbedIO.Quic/EmbedIO.Quic.csproj")
        host = args.output / "development-host"
        properties = ["-p:EmbedIOQuicPackageVersion=" + version,
                      "-p:NuGetLockFilePath=" + str(args.output / "development-host.lock.json"), "-r", args.rid]
        run([args.dotnet, "restore", host_project, "--configfile", str(config), "--use-lock-file", *properties],
            "development-host-restore", environment)
        run([args.dotnet, "publish", host_project, "--no-restore", "-c", "Release", "--self-contained", "false",
             "-o", str(host), *properties], "development-host-publish", environment)
        require((host / library).read_bytes() == native_bytes, "Development host did not resolve the package native asset")
        assembly = str(host / "EmbedIO.Quic.Development.dll")
        common = ["--dotnet", args.dotnet, "--assembly", assembly]
        run([peer, "-m", "unittest", "discover", "-s", str(ROOT / "test/EmbedIO.Quic"), "-p", "test_*peer.py"],
            "peer-regressions", environment)
        cases = (("verify.py", "wire", ["--openssl", "portable-rsa"]),
                 ("verify_cipher_suites.py", "ciphers", ["--openssl", "portable-rsa", "--vectors", "32"]),
                 ("verify_peer_connection_ids.py", "peer-connection-ids", []),
                 ("verify_stateless_reset.py", "stateless-reset", []),
                 ("verify_sni.py", "sni", []), ("verify_tls_listener.py", "publication", []),
                 ("verify_client_certificate.py", "certificate-policies", ["--profile", "full"]),
                 ("fuzz_client_certificate.py", "auth-fuzz", ["--cases", "256", "--seed", "1729"]))
        for script, name, extra in cases:
            run([peer, str(ROOT / "test/EmbedIO.Quic" / script), *common, *extra, "--output", str(args.output / name)],
                name, environment)
        if sys.platform == "linux":
            run([peer, str(ROOT / "test/EmbedIO.Quic/verify_server_chain.py"), *common,
                 "--output", str(args.output / "server-chains")], "server-chains",
                {**environment, "EMBEDIO_QUIC_DISPOSABLE_CERTIFICATE_STORE": "1"})
    result = {"passed": True, "previewOnly": args.preview, "rid": args.rid, "sourceCommit": head, "sdk": sdk, "packages": package_hashes,
              "nativeSha256": hashlib.sha256(native_bytes).hexdigest(), "steps": steps, "peerSourceWheel": peer_wheel,
              "scope": "Actual-RID package-only portable/RID/AOT consumers, generated controllers/auth/limits/abort, full wire/cipher/SNI/publication/policy/mutation/issuer suites. Not oldest-OS, browser, vendor-instrumented or endurance acceptance."}
    (args.output / "receipt.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"passed": True, "rid": args.rid, "steps": len(steps)}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", choices=RIDS, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--peer-wheel-directory", type=Path, help="Exact-source Intel macOS test peer wheel artifact")
    parser.add_argument("--preview-source-commit", help="Source commit of an archived local preview; requires --preview")
    parser.add_argument("--preview", action="store_true", help="Local single-RID development package; never distribution acceptance")
    args = parser.parse_args()
    if args.preview_source_commit and not args.preview:
        parser.error("--preview-source-commit requires --preview")
    args.feed, args.output = args.feed.resolve(), args.output.resolve()
    if args.peer_wheel_directory:
        args.peer_wheel_directory = args.peer_wheel_directory.resolve()
    validate(args)
