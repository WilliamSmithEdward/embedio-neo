"""Build the private, TLS-only EmbedIO QUIC native boundary (Python 3.12+)."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tarfile

VERSION = "3.5.9"
SHA256 = "603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a"
URL = f"https://github.com/openssl/openssl/releases/download/openssl-{VERSION}/openssl-{VERSION}.tar.gz"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def extract_source(archive, work):
    with tarfile.open(archive, "r:gz") as bundle:
        members = bundle.getmembers()
        if not members or len(members) > 50000 or sum(item.size for item in members) > 512 * 1024 * 1024:
            raise RuntimeError("Unexpected OpenSSL archive size")
        root = work.resolve()
        for item in members:
            parts = Path(item.name).parts
            target = (root / item.name).resolve()
            if (not parts or parts[0] != f"openssl-{VERSION}" or ".." in parts
                    or not target.is_relative_to(root) or not (item.isdir() or item.isfile())):
                raise RuntimeError("Unexpected OpenSSL archive entry")
            if item.isdir():
                target.mkdir(parents=True, exist_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                content = bundle.extractfile(item)
                if content is None:
                    raise RuntimeError("Missing OpenSSL archive file contents")
                with content, target.open("xb") as output:
                    shutil.copyfileobj(content, output)
                if target.stat().st_size != item.size:
                    raise RuntimeError("Incomplete OpenSSL archive file")
                target.chmod(item.mode & 0o755)


def build(args):
    if sys.version_info < (3, 12):
        raise RuntimeError("Python 3.12 or newer is required for the supported native build environment")
    system, machine = platform.system(), platform.machine().lower()
    targets = {("Linux", "x86_64"): "linux-x86_64", ("Linux", "aarch64"): "linux-aarch64",
               ("Darwin", "x86_64"): "darwin64-x86_64-cc", ("Darwin", "arm64"): "darwin64-arm64-cc"}
    target = targets.get((system, machine))
    if target is None:
        raise RuntimeError(f"Unsupported native build host: {system}/{machine}")
    source = args.source.resolve()
    args.work.mkdir(parents=True, exist_ok=False)
    args.output.mkdir(parents=True, exist_ok=False)
    work, output = args.work.resolve(), args.output.resolve()
    commands = []
    def run(command, label, cwd=work):
        commands.append(command)
        with (work / (label + ".log")).open("wb") as log:
            subprocess.run(command, cwd=cwd, stdout=log, stderr=subprocess.STDOUT, check=True)
    archive = args.archive
    if archive is None:
        archive = work / f"openssl-{VERSION}.tar.gz"
        run(["curl", "--fail", "--location", "--retry", "3", "--max-filesize", "104857600",
             "--connect-timeout", "20", "--max-time", "120",
             "--proto", "=https", "--proto-redir", "=https", "--tlsv1.2",
             "--output", str(archive), URL], "download")
    if digest(archive) != SHA256:
        raise RuntimeError("OpenSSL archive SHA-256 does not match the reviewed pin")
    extract_source(archive, work)
    upstream = work / f"openssl-{VERSION}"
    objects = work / "objects"
    objects.mkdir()
    # QUIC transport stays managed. No default configuration files or dynamically
    # loaded modules from a machine's different OpenSSL installation are used.
    options = [target, "no-shared", "no-quic", "no-tests", "no-apps", "no-docs",
               "no-autoload-config", "no-dso", "no-module", "-fPIC"]
    run(["perl", str(upstream / "Configure"), *options], "configure", objects)
    run(["make", f"-j{args.jobs}", "build_libs"], "make", objects)
    for name in ("libssl.a", "libcrypto.a"):
        listing = subprocess.check_output(["ar", "t", str(objects / name)], text=True)
        (work / (name + ".objects.txt")).write_text(listing, encoding="utf-8")
        if re.search(r"quic_(engine|channel|record_rx|record_tx)", listing):
            raise RuntimeError("TLS-only archive unexpectedly contains a QUIC transport engine")
    library = output / ("libembedio_quic_tls.dylib" if system == "Darwin" else "libembedio_quic_tls.so")
    command = [args.cc, "-std=c11", "-O2", "-fPIC", "-fvisibility=hidden", "-Wall", "-Wextra", "-Werror", "-Wpedantic",
               "-I" + str(objects / "include"), "-I" + str(upstream / "include"),
               "-dynamiclib" if system == "Darwin" else "-shared", str(source / "native.c"),
               str(objects / "libssl.a"), str(objects / "libcrypto.a")]
    if system == "Darwin":
        exports = work / "exports.txt"
        names = re.findall(r'visibility\("default"\)\)\) [^\n]*?\b(embedio_quic_tls_\w+)\(', (source / "native.c").read_text(encoding="utf-8"))
        if len(names) != 14 or len(set(names)) != len(names):
            raise RuntimeError("Unexpected private native ABI export inventory")
        exports.write_text("".join("_" + name + "\n" for name in names), encoding="utf-8")
        command += ["-Wl,-exported_symbols_list," + str(exports), "-Wl,-install_name,@rpath/" + library.name]
    else:
        command += ["-Wl,--exclude-libs,ALL", "-Wl,-Bsymbolic", "-Wl,-z,relro,-z,now,-z,noexecstack",
                    "-Wl,--version-script=" + str(source / "exports.map"), "-Wl,-soname," + library.name, "-ldl"]
    command += ["-pthread", "-o", str(library)]
    run(command, "link")
    if system == "Darwin":
        run(["otool", "-L", str(library)], "dependencies")
        run(["nm", "-gU", str(library)], "exports")
    else:
        run(["readelf", "-d", str(library)], "dependencies")
        run(["nm", "-D", "--defined-only", str(library)], "exports")
    shutil.copyfile(upstream / "LICENSE.txt", output / "OpenSSL-LICENSE.txt")
    receipt = {"opensslVersion": VERSION, "opensslArchiveSha256": SHA256, "upstreamUrl": URL,
               "host": {"system": system, "architecture": machine, "platform": platform.platform()},
               "configure": options, "commands": commands,
               "nativeSourceHashes": {name: digest(source / name) for name in ("native.c", "exports.map")},
               "outputs": {p.name: digest(p) for p in output.iterdir() if p.is_file()},
               "scope": "TLS-only native build. Runtime interoperability, sanitizers, packaging and platform support require separate validation."}
    (output / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"library": str(library), "sha256": digest(library)}), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path(__file__).resolve().parent)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cc", default="cc")
    parser.add_argument("--jobs", type=int, default=2)
    args = parser.parse_args()
    if not 1 <= args.jobs <= 64:
        parser.error("Jobs must be between 1 and 64")
    build(args)
