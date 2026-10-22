"""Build the pinned test peer's missing Intel macOS cryptography wheel."""
from __future__ import annotations
import argparse
import base64
import csv
from email.parser import BytesParser
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import platform
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import venv
import zipfile
from stage import ROOT, pins, require, load_asset, digest, canonical, decode, read

VERSION = "50.0.2"
ARCHIVE_SHA256 = "7b46165bb56eb4704e2eaaf86f3c940d19154535d9b0ca7d6d590b04060e00d5"
URL = "https://files.pythonhosted.org/packages/9d/af/182eb91b0df3fe75c4d9f26fe70684569566745f6ba7e5c9c73a862c5252/cryptography-50.0.2.tar.gz"
RUST = "1.99.1"


def safe_path(name):
    path = PurePosixPath(name)
    require(name and not path.is_absolute() and ".." not in path.parts and
            "\\" not in name and ":" not in name and "\0" not in name and
            path.as_posix() == name.rstrip("/"), "Unexpected archive path")
    return path


def extract(archive, directory):
    with tarfile.open(archive, "r:gz") as bundle:
        entries = bundle.getmembers()
        require(0 < len(entries) <= 10000 and sum(e.size for e in entries) <= 64 * 1024 * 1024,
                "Unexpected peer archive size")
        names = set()
        for entry in entries:
            path = safe_path(entry.name)
            require(path.parts[0] == "cryptography-" + VERSION and path not in names and
                    (entry.isdir() or entry.isfile()), "Unexpected peer archive member")
            names.add(path)
        for entry in entries:
            target = directory / entry.name
            require(target.resolve().is_relative_to(directory.resolve()), "Peer archive escapes work directory")
            if entry.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                content = bundle.extractfile(entry)
                require(content is not None, "Missing peer archive file")
                with content, target.open("xb") as output:
                    shutil.copyfileobj(content, output)
                require(target.stat().st_size == entry.size, "Incomplete peer archive file")
                target.chmod(entry.mode & 0o755)


def wheel_contents(source):
    with zipfile.ZipFile(source) as bundle:
        entries = bundle.infolist()
        require(0 < len(entries) <= 10000 and len(entries) == len({e.filename for e in entries}) and
                sum(e.file_size for e in entries) <= 64 * 1024 * 1024, "Unexpected built peer wheel")
        for entry in entries:
            safe_path(entry.filename)
            require(not stat.S_ISLNK(entry.external_attr >> 16) and not entry.is_dir() and
                    not entry.flag_bits & 1, "Unexpected wheel member type")
        contents = {e.filename: bundle.read(e) for e in entries}
    prefix = f"cryptography-{VERSION}.dist-info/"
    require(all(name.startswith(("cryptography/", prefix)) for name in contents), "Unexpected wheel package")
    metadata = BytesParser().parsebytes(contents.get(prefix + "METADATA", b""))
    require(metadata.get_all("Name") == ["cryptography"] and metadata.get_all("Version") == [VERSION],
            "Wheel metadata differs from peer pin")
    for license in ("LICENSE", "LICENSE.APACHE", "LICENSE.BSD"):
        require(prefix + "licenses/" + license in contents, "Missing peer license")
    record = prefix + "RECORD"
    rows = list(csv.reader(io.StringIO(contents.get(record, b"").decode())))
    require(len(rows) == len(contents) and {r[0] for r in rows if len(r) == 3} == set(contents),
            "Wheel RECORD inventory differs")
    for name, hash, size in rows:
        data = contents[name]
        expected = "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode()
        require((hash, size) == (("", "") if name == record else (expected, str(len(data)))),
                "Wheel RECORD hash differs")
    return entries, contents


def verify_artifact(directory, source_commit, native_receipt):
    receipt = decode(read(directory / "peer-wheel.json"))
    name = receipt.get("wheel", "")
    require(re.fullmatch(r"cryptography-50\.0\.2-[A-Za-z0-9_.-]+\.whl", name) is not None,
            "Unexpected test peer wheel name")
    require({p.name for p in directory.iterdir()} == {name, "peer-wheel.json"}, "Unexpected test wheel inventory")
    wheel = read(directory / name, 64 * 1024 * 1024)
    require(receipt.get("schemaVersion") == 1 and receipt.get("sourceCommit") == source_commit and
            receipt.get("cryptographyVersion") == VERSION and receipt.get("sourceUrl") == URL and
            receipt.get("sourceArchiveSha256") == ARCHIVE_SHA256 and receipt.get("rustVersion") == RUST and
            receipt.get("opensslVersion") == pins.VERSION and receipt.get("opensslArchiveSha256") == pins.SHA256 and
            receipt.get("nativeBuildReceiptSha256") == digest(native_receipt) and receipt.get("wheelSha256") == digest(wheel),
            "Test wheel source, package or dependency identity differs")
    expected = {name: digest(canonical((ROOT / name).read_bytes())) for name in
                ("build_peer_wheel.py", "peer-wheel-requirements.txt")}
    require(receipt.get("buildSourceHashes") == expected, "Test wheel build source differs")
    _, contents = wheel_contents(directory / name)
    require(contents.get(f"cryptography-{VERSION}.dist-info/licenses/OpenSSL-LICENSE.txt") ==
            (ROOT / "OpenSSL-LICENSE.txt").read_bytes(), "Missing linked OpenSSL license")
    return receipt


def macos_dependencies(listing, identification):
    lines = listing.splitlines()
    identity = identification.splitlines()
    require(lines and identity and lines[0].endswith(":") and lines[0] == identity[0] and len(identity) <= 2,
            "Unexpected macOS dependency metadata")
    require(all(" (" in line for line in lines[1:]), "Malformed macOS dependency entry")
    dependencies = [line.strip().split(" (", 1)[0] for line in lines[1:]]
    # otool -L prints LC_ID_DYLIB first, although that is not a library load.
    # -D identifies that exact row; any repeated actual load is still validated.
    if len(identity) == 2:
        require(dependencies and dependencies[0] == identity[1].strip(), "macOS install name differs")
        dependencies = dependencies[1:]
    require(dependencies and all(path.startswith(("/usr/lib/", "/System/Library/Frameworks/")) and
                                safe_path(path.removeprefix("/")).as_posix() == path.removeprefix("/")
                                for path in dependencies), "Test peer has a non-system shared library dependency")
    return dependencies


def audit_wheel(wheel, work):
    _, contents = wheel_contents(wheel)
    modules = [(name, data) for name, data in contents.items() if name.endswith(".so")]
    require(len(modules) == 1 and modules[0][0].startswith("cryptography/hazmat/bindings/_rust."),
            "Unexpected native peer module")
    module = work / "peer-native-module.so"
    module.write_bytes(modules[0][1])
    if platform.system() == "Darwin":
        require(module.read_bytes()[:8] == bytes.fromhex("cffaedfe07000001"), "Peer module architecture differs")
        output = subprocess.check_output(["otool", "-L", str(module)], text=True)
        (work / "peer-native-dependencies.log").write_text(output, encoding="utf-8")
        identity = subprocess.check_output(["otool", "-D", str(module)], text=True)
        (work / "peer-native-install-name.log").write_text(identity, encoding="utf-8")
        dependencies = macos_dependencies(output, identity)
    else:
        data = module.read_bytes()
        require(data[:6] == bytes.fromhex("7f454c460201") and struct.unpack_from("<H", data, 18)[0] == 62,
                "Peer module architecture differs")
        output = subprocess.check_output(["readelf", "-d", str(module)], text=True)
        (work / "peer-native-dependencies.log").write_text(output, encoding="utf-8")
        dependencies = re.findall(r"\(NEEDED\).*?\[(.*?)\]", output)
        require(set(dependencies) <= {"libgcc_s.so.1", "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0",
                                      "librt.so.1", "ld-linux-x86-64.so.2"}, "Unexpected test peer dynamic dependency")
    require(not any(re.search(r"lib(?:ssl|crypto)[.-]", path, re.IGNORECASE) for path in dependencies),
            "Test peer dynamically loads OpenSSL")
    (work / "peer-native-dependencies.log").write_text(output, encoding="utf-8")
    return dependencies


def add_license(source, output, license):
    # The wheel is a new local build; preserve every vendor file and update RECORD
    # when including the statically linked OpenSSL license.
    record = f"cryptography-{VERSION}.dist-info/RECORD"
    vendor_license = f"cryptography-{VERSION}.dist-info/licenses/OpenSSL-LICENSE.txt"
    entries, contents = wheel_contents(source)
    require(vendor_license not in contents, "Unexpected existing linked OpenSSL license")
    del contents[record]
    contents[vendor_license] = license
    rows = [(name, "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode(), str(len(data)))
            for name, data in sorted(contents.items())]
    stream = io.StringIO(newline="")
    csv.writer(stream, lineterminator="\n").writerows([*rows, (record, "", "")])
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as result:
        for entry in entries:
            if entry.filename != record:
                result.writestr(entry, contents.pop(entry.filename))
        result.writestr(vendor_license, contents.pop(vendor_license))
        result.writestr(record, stream.getvalue().encode())
    wheel_contents(output)



def build(args):
    require(platform.system() in ("Darwin", "Linux") and platform.machine() == "x86_64", "Source peer proof requires x64 Linux or macOS")
    require(re.fullmatch("[0-9a-f]{40}", args.source_commit) is not None, "Unexpected peer source commit")
    asset = load_asset(args.native_output)
    native_receipt = decode(asset["receipt"])
    require(native_receipt["opensslVersion"] == pins.VERSION, "Peer OpenSSL build differs")
    repository = ROOT.parents[1]
    lock = repository / "test/EmbedIO.Conformance/drivers/requirements.txt"
    require(re.findall(r"^cryptography==([^\s]+)", lock.read_text(), re.MULTILINE) == [VERSION], "Peer version differs from dependency lock")
    args.work.mkdir(parents=True, exist_ok=False)
    args.output.mkdir(parents=True, exist_ok=False)
    commands = []
    environment = {**os.environ, "CARGO_HOME": str(args.work / "cargo"), "RUSTUP_HOME": str(args.work / "rustup"),
                   "RUSTUP_TOOLCHAIN": RUST, "CRYPTOGRAPHY_OPENSSL_NO_LEGACY": "1", "CARGO_BUILD_JOBS": "3"}
    def run(command, name, cwd=repository, timeout=600, env=environment):
        commands.append(command)
        with (args.work / (name + ".log")).open("wb") as log:
            subprocess.run(command, cwd=cwd, env=env, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=timeout)
    archive = args.archive or args.work / ("cryptography-" + VERSION + ".tar.gz")
    if args.archive is None:
        run(["curl", "--fail", "--location", "--proto", "=https", "--proto-redir", "=https", "--tlsv1.2",
             "--connect-timeout", "20", "--max-time", "120", "--max-filesize", "16777216", "--output", str(archive), URL], "download")
    require(digest(archive.read_bytes()) == ARCHIVE_SHA256, "Peer source archive differs from pin")
    extract(archive, args.work)
    project = args.work / ("cryptography-" + VERSION)
    cargo_lock = digest((project / "Cargo.lock").read_bytes())
    sdk = args.work / "openssl-sdk"
    shutil.copytree(args.native_work / ("openssl-" + pins.VERSION) / "include", sdk / "include")
    shutil.copytree(args.native_work / "objects/include", sdk / "include", dirs_exist_ok=True)
    (sdk / "lib").mkdir()
    for name in ("libssl.a", "libcrypto.a"):
        shutil.copyfile(args.native_work / "objects" / name, sdk / "lib" / name)
    environment.update(OPENSSL_DIR=str(sdk), OPENSSL_STATIC="1", OPENSSL_NO_VENDOR="1")
    if platform.system() == "Darwin":
        environment["MACOSX_DEPLOYMENT_TARGET"] = "15.0"
    run(["rustup", "toolchain", "install", RUST, "--profile", "minimal", "--no-self-update"], "rust-toolchain")
    python_root = args.work / "python"
    venv.EnvBuilder(with_pip=True).create(python_root)
    python = str(python_root / "bin/python")
    run([python, "-m", "pip", "--isolated", "install", "--require-hashes", "--only-binary=:all:", "-r",
         str(ROOT / "peer-wheel-requirements.txt")], "build-tools")
    wheels = args.work / "wheels"
    run([python, "-m", "maturin", "build", "--release", "--locked", "--manifest-path", "src/rust/Cargo.toml", "--out", str(wheels), "--interpreter", python], "wheel", project)
    require(digest((project / "Cargo.lock").read_bytes()) == cargo_lock, "Peer Rust dependency lock changed")
    artifacts = list(wheels.glob("cryptography-" + VERSION + "-*.whl"))
    require(len(artifacts) == 1, "Unexpected peer wheel inventory")
    wheel = args.output / artifacts[0].name
    add_license(artifacts[0], wheel, (ROOT / "OpenSSL-LICENSE.txt").read_bytes())
    dependencies = audit_wheel(wheel, args.work)
    run([python, "-m", "pip", "--isolated", "install", "--no-deps", str(wheel)], "install-built-wheel")
    probe = ("import json, cryptography; from cryptography.hazmat.backends.openssl.backend import backend; "
             "print(json.dumps({'version': cryptography.__version__, 'openssl': backend.openssl_version_text()}))")
    run([python, "-c", probe], "backend")
    backend = json.loads((args.work / "backend.log").read_text())
    require(backend["version"] == VERSION and backend["openssl"].startswith("OpenSSL " + pins.VERSION + " "),
            "Built test peer OpenSSL backend differs")
    result = {"schemaVersion": 1, "sourceCommit": args.source_commit, "cryptographyVersion": VERSION,
              "sourceUrl": URL, "sourceArchiveSha256": ARCHIVE_SHA256, "cargoLockSha256": cargo_lock,
              "rustVersion": RUST, "nativeBuildReceiptSha256": digest(asset["receipt"]), "opensslVersion": pins.VERSION, "opensslArchiveSha256": pins.SHA256,
              "host": {"system": platform.system(), "machine": platform.machine()},
              "wheel": wheel.name, "wheelSha256": digest(wheel.read_bytes()), "dependencies": dependencies, "backend": backend,
              "buildSourceHashes": {name: digest(canonical((ROOT / name).read_bytes())) for name in ("build_peer_wheel.py", "peer-wheel-requirements.txt")},
              "commands": commands, "scope": "Unpublished test-peer source wheel. Not a production dependency or an upstream-supported Intel macOS wheel."}
    (args.output / "peer-wheel.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    verify_artifact(args.output, args.source_commit, asset["receipt"])
    print(json.dumps({"passed": True, "wheel": wheel.name, "wheelSha256": result["wheelSha256"]}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-work", type=Path, required=True)
    parser.add_argument("--native-output", type=Path, required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--source-commit", required=True)
    args = parser.parse_args()
    for name in ("native_work", "native_output", "work", "output", "archive"):
        value = getattr(args, name)
        if value is not None:
            setattr(args, name, value.resolve())
    build(args)
