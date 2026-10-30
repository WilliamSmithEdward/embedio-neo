"""Check local packages, including the licenses shipped to consumers."""

from pathlib import Path
import re
import sys
import argparse
from zipfile import ZipFile


def relative_links(markdown):
    """Link targets nuget.org cannot follow: it renders a package README without a base URL."""
    targets = re.findall(r"\]\(\s*<?([^)\s>]+)", markdown)
    targets += re.findall(r"^\s*\[[^\]]+\]:\s*<?([^\s>]+)", markdown, re.MULTILINE)
    targets += re.findall(r"""\b(?:href|src)\s*=\s*["']([^"']+)""", markdown, re.IGNORECASE)
    return [target for target in targets if not re.match(r"(?:https?:|mailto:|#)", target, re.IGNORECASE)]


def main(directory, native_staging=None):
    packages = list(Path(directory).glob("*.nupkg"))
    expected_count = 8 if native_staging is not None else 7
    if len(packages) != expected_count:
        raise ValueError(f"Expected {expected_count} packages, found {len(packages)}")
    root = Path(__file__).resolve().parents[1]
    expected_readme = (root / "README.md").read_bytes()
    relative = relative_links(expected_readme.decode("utf-8-sig"))
    if relative:
        raise ValueError(f"Every package carries README.md; use absolute links instead of: {relative}")
    expected_license = (root / "LICENSE").read_bytes()
    identities = set()
    for package in packages:
        with ZipFile(package) as archive:
            names = set(archive.namelist())
            if not {"README.md", "embedio_neo_icons.png"} <= names:
                raise ValueError(f"Missing package README or icon: {package.name}")
            if archive.read("README.md") != expected_readme:
                raise ValueError(f"README differs from root source: {package.name}")
            if archive.read("LICENSE") != expected_license:
                raise ValueError(f"License differs from source: {package.name}")
            for name in (b"Samuel Neff", b"Novell", b"Xamarin", b"sta.blockhead"):
                if name not in expected_license:
                    raise ValueError(f"Missing embedded source attribution: {name!r}")
            if package.name.startswith("EmbedIO-Neo.Quic.Native."):
                if native_staging is None or "EmbedIO-Neo.Quic.Native" in identities:
                    raise ValueError("Native runtime requires complete staging verification")
                sys.path.insert(0, str(root / "native/EmbedIO.Quic.Tls"))
                from stage import RIDS, verify_package
                from defusedxml import ElementTree as ET
                version = ET.parse(root / "Directory.Build.props", forbid_dtd=True,
                                   forbid_entities=True, forbid_external=True).findtext(".//Version")
                verify_package(package, Path(native_staging), list(RIDS), version)
                identities.add("EmbedIO-Neo.Quic.Native")
                print(f"Verified {package.name}")
                continue
            if package.name.startswith("EmbedIO-Neo.Kestrel."):
                from check_kestrel_package import verify
                import os
                cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
                verify(package, cache)
                identity = "EmbedIO-Neo.Kestrel"
                required = {"lib/net10.0/EmbedIO.Kestrel.dll"}
            elif package.name.startswith("EmbedIO-Neo.Cli."):
                identity = "EmbedIO-Neo.Cli"
                required = {"README.md", "licenses/embedio-cli-LICENSE",
                            "tools/net10.0/any/EmbedIO.dll",
                            "tools/net10.0/any/DotnetToolSettings.xml"}
            else:
                identity = next((name for name in ("EmbedIO-Neo.DependencyInjection", "EmbedIO-Neo.JsonServer", "EmbedIO-Neo.Testing", "EmbedIO-Neo.Jwt", "EmbedIO-Neo")
                                 if package.name.startswith(name + ".")), None)
                if identity is None:
                    raise ValueError(f"Unexpected package: {package.name}")
                assembly = identity.replace("EmbedIO-Neo", "EmbedIO", 1)
                required = {f"lib/{target}/{assembly}.dll"
                            for target in ("netstandard2.0", "net10.0")}
                if identity == "EmbedIO-Neo.JsonServer":
                    required.add("licenses/embedio-extras-LICENSE")
            if identity in identities or not required <= names:
                raise ValueError(f"Duplicate identity or missing contents: {package.name}: {required - names}")
            identities.add(identity)
            print(f"Verified {package.name}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory")
    parser.add_argument("--native-staging", type=Path)
    args = parser.parse_args()
    main(args.directory, args.native_staging)
