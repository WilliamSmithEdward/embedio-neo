"""Stage and verify source-matched private TLS runtime package inputs."""
from __future__ import annotations
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import re
from defusedxml import ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("tls_build_pins", ROOT / "build.py")
pins = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pins)
RIDS = ("linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64")
SOURCES = ("native.c", "exports.map", "build.py", "audit.py")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def read(path, limit=1024 * 1024):
    require(not path.is_symlink() and path.is_file() and path.stat().st_size <= limit, "Unexpected native input: " + str(path))
    data = path.read_bytes()
    require(len(data) <= limit, "Native input changed size")
    return data


def unique(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON field")
        result[key] = value
    return result


def decode(data):
    result = json.loads(data.decode("utf-8"), object_pairs_hook=unique)
    require(isinstance(result, dict), "Expected a native JSON object")
    return result


def canonical(data):
    return data.replace(b"\r\n", b"\n")


def library_name(rid):
    return "libembedio_quic_tls.dylib" if rid.startswith("osx-") else "libembedio_quic_tls.so"


def validate_header(binary, rid):
    require(len(binary) >= 64, "Truncated native library")
    arm = rid.endswith("arm64")
    if rid.startswith("osx-"):
        require(binary[:4] == bytes.fromhex("cffaedfe") and
                struct.unpack_from("<I", binary, 4)[0] == (0x100000c if arm else 0x1000007) and
                struct.unpack_from("<I", binary, 12)[0] == 6, "Mach-O header differs from RID")
    else:
        require(binary[:6] == bytes.fromhex("7f454c460201") and
                struct.unpack_from("<H", binary, 16)[0] == 3 and
                struct.unpack_from("<H", binary, 18)[0] == (183 if arm else 62), "ELF header differs from RID")


def load_asset(directory):
    require(not directory.is_symlink() and directory.is_dir(), "Expected a native output directory")
    audit_bytes = read(directory / "binary-audit.json")
    receipt_bytes = read(directory / "build-receipt.json")
    audit, receipt = decode(audit_bytes), decode(receipt_bytes)
    rid = audit.get("runtimeIdentifier")
    require(rid in RIDS and audit.get("schemaVersion") == 1, "Unknown native runtime identity")
    name = library_name(rid)
    require({p.name for p in directory.iterdir()} == {name, "binary-audit.json", "build-receipt.json", "OpenSSL-LICENSE.txt"},
            "Unexpected native output inventory")
    binary = read(directory / name, 32 * 1024 * 1024)
    license = read(directory / "OpenSSL-LICENSE.txt")
    validate_header(binary, rid)
    require(audit.get("librarySha256") == digest(binary) and receipt.get("binaryAudit") == audit,
            "Native binary/audit/receipt hashes differ")
    require(receipt.get("opensslVersion") == pins.VERSION and receipt.get("opensslArchiveSha256") == pins.SHA256
            and receipt.get("upstreamUrl") == pins.URL, "Native source archive differs from pin")
    required = {"no-shared", "no-quic", "no-autoload-config", "no-dso", "no-module"}
    require(required <= set(receipt.get("configure", [])), "Native build is outside the TLS-only profile")
    source_hashes = receipt.get("nativeSourceHashes", {})
    require(set(source_hashes) == set(SOURCES), "Unexpected native source inventory")
    for source in SOURCES:
        trusted = (ROOT / source).read_bytes()
        require(source_hashes[source] in {digest(trusted), digest(canonical(trusted))}, "Native source differs: " + source)
    outputs = {name: digest(binary), "binary-audit.json": digest(audit_bytes), "OpenSSL-LICENSE.txt": digest(license)}
    require(receipt.get("outputs") == outputs, "Native output hashes differ")
    require(license == (ROOT / "OpenSSL-LICENSE.txt").read_bytes(), "Native license differs from reviewed source")
    if rid.startswith("osx-"):
        minimum = audit.get("minimumMacOS", "")
        require(audit.get("format") == "Mach-O" and minimum and
                tuple(int(p) for p in minimum.split(".")) <= (15, 0, 0), "Native macOS baseline differs")
        require(audit.get("dependencies") == ["/usr/lib/libSystem.B.dylib"], "Unexpected macOS dependency")
    else:
        require(audit.get("format") == "ELF64" and all(audit.get(field) is True for field in
                ("nonExecutableStack", "relro", "immediateBinding")), "Native ELF properties differ")
        if "-musl-" not in rid:
            minimum = audit.get("minimumGlibc", "")
            require(minimum and tuple(int(p) for p in minimum.split(".")) <= (2, 27, 0), "Native glibc baseline differs")
    return {"rid": rid, "binary": binary, "audit": audit_bytes, "receipt": receipt_bytes}


def verify_asset(directory, rid):
    asset = load_asset(directory)
    require(asset["rid"] == rid, "Native runtime differs from the executing test RID")
    return {"rids": [rid], "sha256": digest(asset["binary"])}


def stage(output, directories, rids):
    require(rids and len(rids) == len(set(rids)) and set(rids) <= set(RIDS), "Invalid expected runtime inventory")
    assets = [load_asset(path) for path in directories]
    require(len(assets) == len(rids) and {a["rid"] for a in assets} == set(rids), "Missing or duplicate native runtime")
    output.mkdir(parents=True, exist_ok=False)
    entries = {}
    for asset in sorted(assets, key=lambda a: a["rid"]):
        rid = asset["rid"]
        contents = {f"runtimes/{rid}/native/{library_name(rid)}": asset["binary"],
                    f"provenance/{rid}/binary-audit.json": asset["audit"],
                    f"provenance/{rid}/build-receipt.json": asset["receipt"]}
        for name, data in contents.items():
            path = output / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            entries[name] = digest(data)
    license_name = "licenses/OpenSSL-LICENSE.txt"
    path = output / license_name
    path.parent.mkdir(parents=True)
    path.write_bytes((ROOT / "OpenSSL-LICENSE.txt").read_bytes())
    entries[license_name] = digest(path.read_bytes())
    manifest = {"schemaVersion": 1, "abi": 2, "opensslVersion": pins.VERSION,
                "opensslArchiveSha256": pins.SHA256, "rids": sorted(rids),
                "completeDistribution": set(rids) == set(RIDS),
                "sourceHashFormat": "SHA-256 of UTF-8 source with CRLF normalized to LF",
                "sourceHashes": {name: digest(canonical((ROOT / name).read_bytes())) for name in SOURCES},
                "files": entries,
                "scope": "Source/archive identity, header, license and receipt consistency. Receipts are not build attestations or substitutes for runtime validation."}
    (output / "runtime-assets.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    verify(output, rids)
    return manifest


def verify(output, rids):
    manifest = decode(read(output / "runtime-assets.json"))
    require(manifest.get("schemaVersion") == 1 and manifest.get("abi") == 2 and
            manifest.get("opensslVersion") == pins.VERSION and manifest.get("opensslArchiveSha256") == pins.SHA256,
            "Unknown native runtime manifest")
    require(bool(rids) and manifest.get("rids") == sorted(rids) and len(rids) == len(set(rids)) and set(rids) <= set(RIDS),
            "Native manifest runtime inventory differs")
    require(manifest.get("completeDistribution") == (set(rids) == set(RIDS)), "Native distribution completeness differs")
    require(manifest.get("sourceHashes") == {name: digest(canonical((ROOT / name).read_bytes())) for name in SOURCES},
            "Native manifest sources differ")
    expected = {"licenses/OpenSSL-LICENSE.txt"}
    for rid in rids:
        expected.update({f"runtimes/{rid}/native/{library_name(rid)}", f"provenance/{rid}/binary-audit.json",
                         f"provenance/{rid}/build-receipt.json"})
    hashes = manifest.get("files")
    require(isinstance(hashes, dict) and set(hashes) == expected, "Native manifest files differ")
    files = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file() or path.is_symlink()}
    require(files == expected | {"runtime-assets.json"}, "Native staging directory contains foreign files")
    for name in sorted(expected):
        data = read(output / name, 32 * 1024 * 1024)
        require(digest(data) == hashes[name], "Staged native asset changed: " + name)
    return manifest


def verify_package(package, output, rids, version):
    """Compare the packed bytes with their already-validated staging inventory."""
    manifest = verify(output, rids)
    require(package.is_file() and not package.is_symlink() and package.stat().st_size <= 128 * 1024 * 1024,
            "Unexpected native package size")
    repository = ROOT.parents[1]
    package_id = "EmbedIO-Neo.Quic.Native"
    roots = {"LICENSE": repository / "LICENSE", "README.md": repository / "README.md",
             "embedio_neo_icons.png": repository / "images/embedio_neo_icons.png"}
    assets = set(manifest["files"]) | {"runtime-assets.json"}
    ns = {"n": "http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd"}
    with zipfile.ZipFile(package) as bundle:
        entries = bundle.infolist()
        require(0 < len(entries) <= 128 and len(entries) == len({e.filename for e in entries}),
                "Duplicate or excessive package entries")
        require(all(not e.is_dir() and 0 <= e.file_size <= 32 * 1024 * 1024 and not e.flag_bits & 1
                    for e in entries) and sum(e.file_size for e in entries) <= 256 * 1024 * 1024,
                "Unexpected package entry size or encryption")
        names = {entry.filename for entry in entries}
        require(all(not name.startswith("/") and "\\" not in name and
                    all(part not in ("", ".", "..") for part in name.split("/")) for name in names),
                "Unexpected package path")
        metadata = {name for name in names if re.fullmatch(r"package/services/metadata/core-properties/(?:[0-9a-f]{32}|nuget)\.psmdcp", name)}
        require(len(metadata) == 1 and names == assets | set(roots) | metadata |
                {package_id + ".nuspec", "[Content_Types].xml", "_rels/.rels"}, "Native package inventory differs")
        for name in assets:
            require(bundle.read(name) == read(output / name, 32 * 1024 * 1024), "Packed runtime asset differs: " + name)
        for name, source in roots.items():
            require(bundle.read(name) == source.read_bytes(), "Packed repository notice differs: " + name)
        nuspec = bundle.read(package_id + ".nuspec")
        require(len(nuspec) <= 65536 and b"<!" not in nuspec, "Unexpected package metadata declaration")
        metadata_node = ET.fromstring(nuspec, forbid_dtd=True, forbid_entities=True, forbid_external=True).find("n:metadata", ns)
        require(metadata_node is not None and metadata_node.findtext("n:id", namespaces=ns) == package_id and
                metadata_node.findtext("n:version", namespaces=ns) == version, "Native package identity differs")
        license_node = metadata_node.find("n:license", ns)
        require(license_node is not None and license_node.attrib.get("type") == "expression" and
                license_node.text == "MIT AND Apache-2.0", "Native package license differs")
        require(not metadata_node.findall("n:dependencies/*", ns), "Native-only package acquired dependencies")
    return {"package": package.name, "sha256": digest(package.read_bytes()), "version": version,
            "rids": manifest["rids"], "completeDistribution": manifest["completeDistribution"]}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("stage", "verify", "package", "asset"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--package", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--inputs", type=Path, nargs="*")
    parser.add_argument("--rids", choices=RIDS, nargs="+", default=list(RIDS))
    args = parser.parse_args()
    if args.mode == "asset":
        if len(args.rids) != 1:
            parser.error("Asset verification requires exactly one --rids value")
        result = verify_asset(args.output, args.rids[0])
    elif args.mode == "package":
        if args.package is None or args.version is None:
            parser.error("Package validation requires --package and --version")
        result = verify_package(args.package, args.output, args.rids, args.version)
    else:
        result = stage(args.output, args.inputs or [], args.rids) if args.mode == "stage" else verify(args.output, args.rids)
    print(json.dumps({key: value for key, value in result.items() if key in
                      ("rids", "completeDistribution", "package", "sha256", "version")}), flush=True)
