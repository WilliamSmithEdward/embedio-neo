"""Inspect private TLS runtime assets without loading their native code."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import subprocess

EXPORTS = {"embedio_quic_tls_" + suffix for suffix in (
    "abi", "add", "alert", "blocks", "certificate", "certificate_count", "connections",
    "credential_add_chain", "credential_free", "credential_new", "credentials", "free", "new",
    "next", "set_ticket_keys", "step", "use_credential", "version")}
RIDS = ("linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64", "osx-x64", "osx-arm64")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def exports_match(text, darwin=False):
    actual = set()
    version_definition = darwin
    for line in text.splitlines():
        fields = line.split()
        if not fields:
            continue
        require(len(fields) == 3, "Unexpected native export record")
        _, kind, symbol = fields
        if not darwin and kind == "A" and symbol == "EMBEDIO_QUIC_TLS_4":
            require(not version_definition, "Duplicate native ABI version definition")
            version_definition = True
            continue
        require(darwin or symbol.endswith("@@EMBEDIO_QUIC_TLS_4"), "Unexpected native ABI symbol version")
        symbol = symbol.removeprefix("_") if darwin else symbol.split("@", 1)[0]
        require(kind == "T" and symbol not in actual, "Unexpected native export kind or duplicate")
        actual.add(symbol)
    require(version_definition and actual == EXPORTS, "Private native ABI exports differ")


def inspect_linux(header, segments, dynamic, versions, exports, architecture, requested=None):
    machine = {"x64": "Advanced Micro Devices X86-64", "arm64": "AArch64"}[architecture]
    require(re.search(r"Class:\s+ELF64\b", header) is not None, "Expected ELF64")
    require(re.search(r"Data:.*little endian", header) is not None, "Expected little-endian ELF")
    require(re.search(r"Type:\s+DYN\b", header) is not None, "Expected a shared object")
    require(re.search(r"Machine:\s+" + re.escape(machine) + r"\s*$", header, re.MULTILINE) is not None,
            "Native architecture differs from build host")
    programs = [line.split() for line in segments.splitlines() if line.strip().startswith(("LOAD ", "GNU_STACK ", "GNU_RELRO "))]
    require(programs and all(len(fields) >= 8 for fields in programs), "Missing or invalid ELF program headers")
    loads = [fields for fields in programs if fields[0] == "LOAD"]
    stacks = [fields for fields in programs if fields[0] == "GNU_STACK"]
    require(loads and all(not {"W", "E"} <= set("".join(fields[6:-1])) for fields in loads),
            "Writable executable native segment")
    require(len(stacks) == 1 and "".join(stacks[0][6:-1]) == "RW", "Expected a non-executable native stack")
    require(any(fields[0] == "GNU_RELRO" and int(fields[5], 16) > 0 for fields in programs), "Missing native RELRO")
    require(re.search(r"\(BIND_NOW\)|\((?:FLAGS|FLAGS_1)\).*\b(?:NOW|BIND_NOW)\b", dynamic) is not None,
            "Missing immediate native binding")
    require(re.search(r"\((?:RPATH|RUNPATH|TEXTREL)\)", dynamic) is None, "Unexpected native search path or text relocation")
    require(re.search(r"\(SONAME\).*\[libembedio_quic_tls\.so\]", dynamic) is not None, "Unexpected native SONAME")
    needed = re.findall(r"\(NEEDED\).*?\[([^\]]+)\]", dynamic)
    musl = "libc.musl-" + ("x86_64" if architecture == "x64" else "aarch64") + ".so.1"
    if musl in needed:
        require(set(needed) <= {musl, "libgcc_s.so.1"}, "Unexpected musl native dependency")
        rid = "linux-musl-" + architecture
        minimum = None
        require("GLIBC_" not in versions, "Mixed libc ABI in musl asset")
    else:
        allowed = {"libc.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "libm.so.6", "libgcc_s.so.1"}
        require("libc.so.6" in needed and set(needed) <= allowed, "Unexpected glibc native dependency")
        names = set(re.findall(r"Name:\s+(GLIBC_\S+)", versions))
        require(names and all(re.fullmatch(r"GLIBC_\d+(?:\.\d+)+", name) for name in names),
                "Unrecognized glibc version requirement")
        required = max(tuple(int(part) for part in name[6:].split(".")) for name in names)
        minimum = ".".join(str(part) for part in required)
        rid = "linux-" + architecture
        if requested:
            require(required <= (2, 27, 0), "Native asset requires a newer glibc than .NET 10's portable baseline")
    require(requested is None or requested == rid, "Requested RID differs from the actual native asset")
    exports_match(exports)
    return {"format": "ELF64", "runtimeIdentifier": rid, "dependencies": sorted(needed),
            "minimumGlibc": minimum, "nonExecutableStack": True, "relro": True, "immediateBinding": True,
            "scope": "Binary identity, dependencies, hardening and versioned glibc symbols; runtime and musl baseline acceptance require separate execution."}


def inspect_darwin(architectures, loads, dependencies, exports, architecture, requested=None):
    require(architectures.strip() == ("x86_64" if architecture == "x64" else "arm64"), "Unexpected Mach-O architecture")
    require("cmd LC_RPATH" not in loads, "Unexpected native runtime search path")
    require(len(re.findall(r"cmd LC_BUILD_VERSION\b", loads)) == 1, "Expected one Mach-O deployment target")
    require(re.search(r"platform\s+(?:1|MACOS)\s*$", loads, re.MULTILINE) is not None, "Expected the macOS platform")
    minima = re.findall(r"minos\s+(\d+(?:\.\d+)+)", loads)
    require(len(minima) == 1, "Missing macOS minimum version")
    if requested:
        minimum = tuple(int(part) for part in minima[0].split("."))
        require(minimum <= (14, 0, 0), "Native asset requires a newer macOS than the deployment baseline")
    names = [line.strip().split(" (", 1)[0] for line in dependencies.splitlines()[1:] if line.strip()]
    require(names.count("@rpath/libembedio_quic_tls.dylib") == 1 and names.count("/usr/lib/libSystem.B.dylib") == 1
            and len(names) == 2, "Unexpected Mach-O identity or native dependency")
    rid = "osx-" + architecture
    require(requested is None or requested == rid, "Requested RID differs from the actual native asset")
    exports_match(exports, darwin=True)
    return {"format": "Mach-O", "runtimeIdentifier": rid, "minimumMacOS": minima[0],
            "dependencies": ["/usr/lib/libSystem.B.dylib"],
            "scope": "Binary identity, dependencies and deployment metadata; signing and runtime acceptance require separate execution."}


def inspect(library, requested=None):
    require(library.is_file() and 0 < library.stat().st_size <= 32 * 1024 * 1024, "Unexpected native library size")
    machine = platform.machine().lower()
    architecture = {"x86_64": "x64", "aarch64": "arm64", "arm64": "arm64"}.get(machine)
    require(architecture is not None, "Unsupported native audit architecture")
    environment = {**os.environ, "LC_ALL": "C"}
    def read(command):
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", check=True,
                              timeout=30, env=environment).stdout
    if platform.system() == "Linux":
        result = inspect_linux(read(["readelf", "-hW", str(library)]), read(["readelf", "-Wl", str(library)]),
                               read(["readelf", "-dW", str(library)]), read(["readelf", "-VW", str(library)]),
                               read(["nm", "-D", "--defined-only", str(library)]), architecture, requested)
    elif platform.system() == "Darwin":
        result = inspect_darwin(read(["lipo", "-archs", str(library)]), read(["otool", "-l", str(library)]),
                                read(["otool", "-L", str(library)]), read(["nm", "-gU", str(library)]), architecture, requested)
    else:
        raise ValueError("Native audit requires Linux or macOS")
    with library.open("rb") as source:
        result["librarySha256"] = hashlib.file_digest(source, "sha256").hexdigest()
    return {"schemaVersion": 1, **result}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--rid", choices=RIDS)
    args = parser.parse_args()
    result = inspect(args.library, args.rid)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2)
        output.write("\n")
    print(json.dumps(result), flush=True)
