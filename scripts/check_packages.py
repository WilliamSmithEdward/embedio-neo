"""Check local packages, including the licenses shipped to consumers."""

from pathlib import Path
import sys
from zipfile import ZipFile


def main(directory):
    packages = list(Path(directory).glob("*.nupkg"))
    if len(packages) != 5:
        raise ValueError(f"Expected five packages, found {len(packages)}")
    root = Path(__file__).resolve().parents[1]
    expected_readme = (root / "README.md").read_bytes()
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
            if package.name.startswith("EmbedIO-Neo.Cli."):
                identity = "EmbedIO-Neo.Cli"
                required = {"README.md", "licenses/embedio-cli-LICENSE",
                            "tools/net10.0/any/EmbedIO.dll",
                            "tools/net10.0/any/DotnetToolSettings.xml"}
            else:
                identity = next((name for name in ("EmbedIO-Neo.DependencyInjection", "EmbedIO-Neo.JsonServer", "EmbedIO-Neo.Testing", "EmbedIO-Neo")
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
    main(sys.argv[1])
