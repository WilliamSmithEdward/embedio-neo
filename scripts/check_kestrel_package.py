"""Verify bundled Kestrel bytes against the pinned official NuGet archives."""

import argparse
import hashlib
import json
import os
from pathlib import Path
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/EmbedIO.Kestrel"
RIDS = ("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")


def verify(package, nuget_root):
    pins = json.loads((PROJECT / "upstream-packs.json").read_text(encoding="utf-8"))
    components = json.loads((PROJECT / "components.json").read_text(encoding="utf-8"))
    version = pins["version"]
    with ZipFile(package) as bundled:
        names = bundled.namelist()
        if len(names) != len(set(names)):
            raise ValueError("Duplicate package paths")
        adapter = bundled.read("lib/net10.0/EmbedIO.Kestrel.dll")
        expected_dlls = {"lib/net10.0/EmbedIO.Kestrel.dll"}
        for name, expected in pins["sha256"].items():
            identity = name.lower()
            upstream_path = nuget_root / identity / version / f"{identity}.{version}.nupkg"
            if hashlib.sha256(upstream_path.read_bytes()).hexdigest().upper() != expected:
                raise ValueError(f"Upstream archive hash differs: {name}")
            with ZipFile(upstream_path) as upstream:
                if name == "Microsoft.AspNetCore.App.Ref":
                    for notice in ("LICENSE.txt", "THIRD-PARTY-NOTICES.TXT"):
                        if bundled.read("licenses/Microsoft.AspNetCore.App/" + notice) != upstream.read(notice):
                            raise ValueError(f"Upstream notice differs: {notice}")
                    continue
                rid = name.removeprefix("Microsoft.AspNetCore.App.Runtime.")
                if rid not in RIDS:
                    raise ValueError(f"Unexpected RID: {rid}")
                prefix = f"runtimes/{rid}/lib/net10.0/"
                adapter_path = prefix + "EmbedIO.Kestrel.dll"
                expected_dlls.add(adapter_path)
                if bundled.read(adapter_path) != adapter:
                    raise ValueError(f"Runtime adapter differs: {rid}")
                for component in components:
                    path = prefix + component + ".dll"
                    expected_dlls.add(path)
                    if bundled.read(path) != upstream.read(path):
                        raise ValueError(f"Bundled component differs: {path}")
        actual_dlls = {name for name in names if name.endswith(".dll")}
        if actual_dlls != expected_dlls:
            raise ValueError(f"Unexpected or missing assemblies: {actual_dlls ^ expected_dlls}")
        if bundled.read("LICENSE") != (ROOT / "LICENSE").read_bytes():
            raise ValueError("Root license differs")
    return {"package": package.name, "sha256": hashlib.sha256(package.read_bytes()).hexdigest(),
            "upstreamVersion": version, "rids": list(RIDS), "componentsPerRid": len(components),
            "verifiedDlls": len(expected_dlls), "compressedBytes": package.stat().st_size}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--nuget-root", type=Path, default=Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages")))
    args = parser.parse_args()
    print(json.dumps(verify(args.package, args.nuget_root), indent=2))


if __name__ == "__main__":
    main()
