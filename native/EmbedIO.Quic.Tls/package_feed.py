"""Seal the exact local package feed consumed by the native-RID workflow."""
import argparse
import json
from pathlib import Path
import subprocess
from defusedxml import ElementTree as ET
import zipfile
from stage import ROOT, RIDS, digest, require, verify_package


def seal(staged, feed, preview_rid=None):
    repository = ROOT.parents[1]
    version = ET.parse(repository / "Directory.Build.props", forbid_dtd=True, forbid_entities=True, forbid_external=True).findtext(".//Version")
    head = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository, text=True).strip()
    native = feed / f"EmbedIO-Neo.Quic.Native.{version}.nupkg"
    core = feed / f"EmbedIO-Neo.{version}.nupkg"
    require({path.name for path in feed.iterdir()} == {native.name, core.name}, "Unexpected local feed inventory")
    verify_package(native, staged, [preview_rid] if preview_rid else list(RIDS), version)
    for path, identity in ((core, "EmbedIO-Neo"), (native, "EmbedIO-Neo.Quic.Native")):
        require(path.is_file() and not path.is_symlink() and path.stat().st_size <= 128 * 1024 * 1024,
                "Unexpected feed package")
        with zipfile.ZipFile(path) as bundle:
            name = identity + ".nuspec"
            info = bundle.getinfo(name)
            require(info.file_size <= 65536 and bundle.namelist().count(name) == 1, "Unexpected package metadata")
            data = bundle.read(name)
            require(b"<!" not in data, "Unexpected package metadata declaration")
            document = ET.fromstring(data, forbid_dtd=True, forbid_entities=True, forbid_external=True)
            namespace = document.tag.removesuffix("package")
            require(namespace in ("{http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd}",
                                  "{http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd}"), "Unexpected NuGet schema")
            metadata = document.find(namespace + "metadata")
            require(metadata is not None and metadata.findtext(namespace + "id") == identity and
                    metadata.findtext(namespace + "version") == version, "Package identity differs")
            source = metadata.find(namespace + "repository")
            require(source is not None and source.attrib.get("commit") == head, "Package source commit differs")
            if identity == "EmbedIO-Neo":
                dependencies = metadata.find(namespace + "dependencies")
                require(dependencies is not None and len(dependencies) == 2, "Core dependency groups missing or duplicated")
                groups = {group.attrib.get("targetFramework"): {item.attrib.get("id") for item in group}
                          for group in dependencies}
                require(groups == {".NETStandard2.0": {"System.ComponentModel.Annotations", "System.Text.Json", "System.Threading.RateLimiting"},
                                   "net10.0": {"System.Threading.RateLimiting", "EmbedIO-Neo.Quic.Native"}}, "Core runtime dependency groups differ")
                for group in dependencies:
                    require(len(group) == len(groups[group.attrib["targetFramework"]]), "Duplicate core dependency")
                    for item in group:
                        if item.attrib.get("id") == "EmbedIO-Neo.Quic.Native":
                            require(item.attrib.get("version") == version, "Native dependency version differs")
    result = {"sourceCommit": head, "version": version, "previewOnly": preview_rid is not None,
              "packages": {path.name: digest(path.read_bytes()) for path in (core, native)}}
    with (feed / "packages.json").open("x", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)
    print(json.dumps(result), flush=True)
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--staged", type=Path, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--preview-rid", choices=RIDS, help="Local development only; does not accept a distribution package")
    args = parser.parse_args()
    seal(args.staged, args.feed, args.preview_rid)
