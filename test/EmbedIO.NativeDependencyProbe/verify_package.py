#!/usr/bin/env python3
"""Verify private package assets against a validated native candidate receipt."""
import hashlib
import json
from pathlib import Path
import sys
import struct
import re
from defusedxml import ElementTree as ET
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
    source_names = {"msquic-kqueue-close.patch", "msquic-kqueue-config.patch", "openssl-tag-signature.log", "crypto-version.txt"}
    manifest_name = "EmbedIO-Neo.Native.MsQuic.Candidate.nuspec"
    payload = expected | {"source/" + name for name in source_names} | {
        "licenses/MsQuic-LICENSE", "licenses/MsQuic-THIRD-PARTY-NOTICES", "licenses/openssl-LICENSE.txt",
        "README.md", "build-receipt.json", "lib/net10.0/_._", manifest_name,
        "_rels/.rels", "[Content_Types].xml"}
    metadata = {name for name in names if re.fullmatch(r"package/services/metadata/core-properties/[A-Za-z0-9_.-]+\.psmdcp", name)}
    if len(metadata) != 1 or set(names) != payload | metadata:
        raise ValueError("Unexpected package payload or missing review evidence.")
    document = ET.fromstring(archive.read(manifest_name), forbid_dtd=True, forbid_entities=True, forbid_external=True)
    namespace = "{http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd}"
    if document.tag != namespace + "package" or len(document) != 1 or document[0].tag != namespace + "metadata":
        raise ValueError("Unexpected package metadata structure.")
    metadata_node = document[0]
    if len({node.tag for node in metadata_node}) != len(metadata_node):
        raise ValueError("Duplicate package metadata fields.")
    if metadata_node is None or metadata_node.findtext(namespace + "id") != "EmbedIO-Neo.Native.MsQuic.Candidate" or metadata_node.findtext(namespace + "version") != "0.0.0-local":
        raise ValueError("Unexpected package identity/version.")
    dependencies = metadata_node.find(namespace + "dependencies")
    groups = [] if dependencies is None else list(dependencies)
    if len(groups) != 1 or groups[0].tag != namespace + "group" or groups[0].attrib != {"targetFramework": "net10.0"} or len(groups[0]) != 0:
        raise ValueError("Unexpected package dependency group.")
    license_node = metadata_node.find(namespace + "license")
    if license_node is None or license_node.attrib != {"type": "file"} or license_node.text != "licenses/MsQuic-LICENSE" or metadata_node.findtext(namespace + "readme") != "README.md":
        raise ValueError("Unexpected package notice/readme metadata.")
    for name in source_names:
        if archive.read("source/" + name) != (candidate / "source" / name).read_bytes():
            raise ValueError("Source evidence differs from candidate: " + name)
    reviewed = Path(__file__).resolve().parent.parent / "EmbedIO.RuntimeCloseProbe"
    for name in ("msquic-kqueue-close.patch", "msquic-kqueue-config.patch"):
        if archive.read("source/" + name).replace(b"\r\n", b"\n") != (reviewed / name).read_bytes().replace(b"\r\n", b"\n"):
            raise ValueError("Reviewed patch differs: " + name)
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
                  "verifiedNativeAssets": len(expected), "verifiedNativeArchitecture": True, "reviewedSourceMatches": True, "exactPayload": True, "noticesMatch": True}))
