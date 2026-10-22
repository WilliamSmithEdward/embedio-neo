"""Runtime inventory, byte consistency and package input ownership regressions."""
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import warnings
import sys
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("native_stage", Path(__file__).resolve().parents[1] / "stage.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class StageTests(unittest.TestCase):
    def asset(self, parent):
        directory = parent / "input"
        directory.mkdir()
        binary = bytearray(64)
        binary[:6] = bytes.fromhex("7f454c460201")
        struct.pack_into("<HH", binary, 16, 3, 62)
        audit = dict(schemaVersion=1, runtimeIdentifier="linux-x64", format="ELF64", minimumGlibc="2.17",
                     dependencies=["libc.so.6"], librarySha256=module.digest(binary),
                     nonExecutableStack=True, relro=True, immediateBinding=True)
        encoded = json.dumps(audit).encode()
        license = (module.ROOT / "OpenSSL-LICENSE.txt").read_bytes()
        outputs = {"libembedio_quic_tls.so": module.digest(binary), "binary-audit.json": module.digest(encoded),
                   "OpenSSL-LICENSE.txt": module.digest(license)}
        receipt = dict(opensslVersion=module.pins.VERSION, opensslArchiveSha256=module.pins.SHA256,
                       upstreamUrl=module.pins.URL, configure=["no-shared", "no-quic", "no-autoload-config", "no-dso", "no-module"],
                       nativeSourceHashes={name: module.digest((module.ROOT / name).read_bytes()) for name in module.SOURCES},
                       binaryAudit=audit, outputs=outputs)
        for name, value in {"libembedio_quic_tls.so": binary, "binary-audit.json": encoded,
                            "OpenSSL-LICENSE.txt": license, "build-receipt.json": json.dumps(receipt).encode()}.items():
            (directory / name).write_bytes(value)
        return directory

    def test_exact_staging_and_preview_identity(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = self.asset(root)
            result = module.stage(root / "output", [source], ["linux-x64"])
            self.assertFalse(result["completeDistribution"])
            self.assertEqual(result["rids"], ["linux-x64"])
            self.assertEqual((root / "output/runtimes/linux-x64/native/libembedio_quic_tls.so").read_bytes(),
                             (source / "libembedio_quic_tls.so").read_bytes())
            module.verify(root / "output", ["linux-x64"])

    def test_individual_test_asset_must_match_requested_rid(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = self.asset(root)
            self.assertEqual(module.verify_asset(source, "linux-x64")["rids"], ["linux-x64"])
            with self.assertRaises(ValueError):
                module.verify_asset(source, "osx-arm64")

    def test_refuses_missing_duplicate_and_foreign_runtimes_before_output(self):
        for expected, duplicate in ((list(module.RIDS), False), (["linux-x64"], True), (["osx-arm64"], False)):
            with self.subTest(expected=expected), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = self.asset(root)
                with self.assertRaises(ValueError):
                    module.stage(root / "output", [source, source] if duplicate else [source], expected)
                self.assertFalse((root / "output").exists())

    def test_detects_changed_binary_license_and_source(self):
        for changed in ("binary", "license", "source", "archive", "profile", "foreign", "header"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = self.asset(root)
                receipt = json.loads((source / "build-receipt.json").read_text())
                if changed == "binary":
                    (source / "libembedio_quic_tls.so").write_bytes(b"other")
                elif changed == "license":
                    (source / "OpenSSL-LICENSE.txt").write_bytes(b"other")
                elif changed == "source":
                    receipt["nativeSourceHashes"]["native.c"] = "0" * 64
                elif changed == "archive":
                    receipt["opensslArchiveSha256"] = "0" * 64
                elif changed == "profile":
                    receipt["configure"].remove("no-quic")
                elif changed == "foreign":
                    (source / "credential.pfx").write_bytes(b"must not be packaged")
                else:
                    binary = bytearray((source / "libembedio_quic_tls.so").read_bytes())
                    struct.pack_into("<H", binary, 18, 183)
                    (source / "libembedio_quic_tls.so").write_bytes(binary)
                (source / "build-receipt.json").write_text(json.dumps(receipt))
                with self.assertRaises(ValueError):
                    module.load_asset(source)

    def test_detects_mutation_and_extra_files_after_staging(self):
        for changed in ("binary", "foreign", "runtime", "completeness", "manifest-path"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = self.asset(root)
                output = root / "output"
                module.stage(output, [source], ["linux-x64"])
                manifest = json.loads((output / "runtime-assets.json").read_text())
                if changed == "binary":
                    (output / "runtimes/linux-x64/native/libembedio_quic_tls.so").write_bytes(b"changed")
                elif changed == "foreign":
                    (output / "extra.pfx").write_bytes(b"must not be packaged")
                elif changed == "runtime":
                    manifest["rids"] = ["linux-arm64"]
                elif changed == "completeness":
                    manifest["completeDistribution"] = True
                else:
                    manifest["files"]["../outside"] = "0" * 64
                (output / "runtime-assets.json").write_text(json.dumps(manifest))
                with self.assertRaises(ValueError):
                    module.verify(output, ["linux-x64"])

    def test_json_duplicates_and_wrong_shapes_fail(self):
        for value in (b'{"abi":2,"abi":3}', b'[]', b'null'):
            with self.subTest(value=value), self.assertRaises(ValueError):
                module.decode(value)

    def package(self, output, destination):
        repository = module.ROOT.parents[1]
        contents = {path.relative_to(output).as_posix(): path.read_bytes() for path in output.rglob("*") if path.is_file()}
        contents.update({"LICENSE": (repository / "LICENSE").read_bytes(),
                         "README.md": (repository / "README.md").read_bytes(),
                         "embedio_neo_icons.png": (repository / "images/embedio_neo_icons.png").read_bytes(),
                         "[Content_Types].xml": b"metadata", "_rels/.rels": b"relationships",
                         "package/services/metadata/core-properties/nuget.psmdcp": b"properties",
                         "EmbedIO-Neo.Quic.Native.nuspec": b'<package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd"><metadata><id>EmbedIO-Neo.Quic.Native</id><version>2.16.0</version><license type="expression">MIT AND Apache-2.0</license></metadata></package>'})
        with zipfile.ZipFile(destination, "w") as bundle:
            for name, data in contents.items():
                bundle.writestr(name, data)
        return contents

    def test_packed_bytes_match_staging_and_notices(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            output, package = root / "output", root / "native.nupkg"
            module.stage(output, [self.asset(root)], ["linux-x64"])
            self.package(output, package)
            result = module.verify_package(package, output, ["linux-x64"], "2.16.0")
            self.assertEqual(result["rids"], ["linux-x64"])
            self.assertEqual(result["sha256"], module.digest(package.read_bytes()))
            self.assertFalse(result["completeDistribution"])

    def test_package_refuses_changed_payload_identity_and_foreign_entries(self):
        for changed in ("native", "notice", "version", "license", "dependency", "foreign", "traversal", "duplicate", "absolute", "utf16-dtd"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                output, package = root / "output", root / "native.nupkg"
                module.stage(output, [self.asset(root)], ["linux-x64"])
                contents = self.package(output, package)
                nuspec = "EmbedIO-Neo.Quic.Native.nuspec"
                if changed == "native":
                    contents["runtimes/linux-x64/native/libembedio_quic_tls.so"] = b"changed"
                elif changed == "notice":
                    contents["LICENSE"] = b"changed"
                elif changed == "version":
                    contents[nuspec] = contents[nuspec].replace(b"2.16.0", b"2.15.0")
                elif changed == "license":
                    contents[nuspec] = contents[nuspec].replace(b"MIT AND Apache-2.0", b"MIT")
                elif changed == "dependency":
                    contents[nuspec] = contents[nuspec].replace(b"</metadata>", b'<dependencies><dependency id="foreign" version="1.0.0" /></dependencies></metadata>')
                elif changed == "utf16-dtd":
                    contents[nuspec] = ('<!DOCTYPE package [<!ENTITY x "test">]>' + contents[nuspec].decode()).encode("utf-16")
                elif changed == "foreign":
                    contents["private-key.pem"] = b"foreign"
                elif changed == "traversal":
                    contents["../outside"] = b"foreign"
                elif changed == "absolute":
                    contents["/outside"] = b"foreign"
                with zipfile.ZipFile(package, "w") as bundle:
                    for name, data in contents.items():
                        bundle.writestr(name, data)
                    if changed == "duplicate":
                        with warnings.catch_warnings():
                            warnings.simplefilter("ignore", UserWarning)
                            bundle.writestr("LICENSE", contents["LICENSE"])
                with self.assertRaises(ValueError):
                    module.verify_package(package, output, ["linux-x64"], "2.16.0")


    def test_preview_feed_checks_source_and_legacy_dependency_groups(self):
        feed_spec = importlib.util.spec_from_file_location("native_feed", module.ROOT / "package_feed.py")
        feed_module = importlib.util.module_from_spec(feed_spec)
        with patch.dict(sys.modules, {"stage": module}):
            feed_spec.loader.exec_module(feed_module)
        head = "0" * 40
        for changed in (None, "source", "standard", "modern", "version", "foreign", "partial-default",
                        "native-missing", "native-version", "native-legacy", "duplicate"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                output, feed = root / "output", root / "feed"
                feed.mkdir()
                module.stage(output, [self.asset(root)], ["linux-x64"])
                native = feed / "EmbedIO-Neo.Quic.Native.2.16.0.nupkg"
                contents = self.package(output, native)
                name = "EmbedIO-Neo.Quic.Native.nuspec"
                contents[name] = contents[name].replace(b"</metadata>", ('<repository commit="' + head + '" /></metadata>').encode())
                with zipfile.ZipFile(native, "w") as bundle:
                    for path, data in contents.items():
                        bundle.writestr(path, data)
                source_head = "1" * 40 if changed == "source" else head
                version = "2.15.0" if changed == "version" else "2.16.0"
                standard = "Foreign" if changed == "standard" else "System.Text.Json"
                native_version = '2.15.0' if changed == 'native-version' else version
                native_dependency = '<dependency id="EmbedIO-Neo.Quic.Native" version="' + native_version + '" />'
                modern = ('<dependency id="Foreign" />' if changed == 'modern' else
                          '' if changed == 'native-missing' else native_dependency)
                if changed == 'duplicate':
                    modern += native_dependency
                core = ('<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>EmbedIO-Neo</id><version>' + version + '</version><repository commit="' + source_head + '" /><dependencies><group targetFramework=".NETStandard2.0"><dependency id="System.ComponentModel.Annotations" /><dependency id="' + standard + '" /><dependency id="System.Threading.RateLimiting" /></group><group targetFramework="net10.0"><dependency id="System.Threading.RateLimiting" />' + modern + '</group></dependencies></metadata></package>')
                if changed == 'native-legacy':
                    core = core.replace('</group><group targetFramework="net10.0">',
                                        native_dependency + '</group><group targetFramework="net10.0">')
                with zipfile.ZipFile(feed / "EmbedIO-Neo.2.16.0.nupkg", "w") as bundle:
                    bundle.writestr("EmbedIO-Neo.nuspec", core.encode())
                if changed == "foreign":
                    (feed / "unexpected.pem").write_bytes(b"foreign")
                with patch.object(feed_module.subprocess, "check_output", return_value=head + "\n"):
                    if changed is None:
                        result = feed_module.seal(output, feed, "linux-x64")
                        self.assertTrue(result["previewOnly"])
                        self.assertEqual(result["sourceCommit"], head)
                        self.assertTrue((feed / "packages.json").is_file())
                    else:
                        with self.assertRaises(ValueError):
                            feed_module.seal(output, feed, None if changed == "partial-default" else "linux-x64")
                        self.assertFalse((feed / "packages.json").exists())


    def test_symlink_input_is_not_followed(self):
        if not hasattr(Path, "symlink_to"):
            self.skipTest("Symbolic links are unavailable")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = self.asset(root)
            alias = root / "alias"
            try:
                alias.symlink_to(source, target_is_directory=True)
            except OSError:
                self.skipTest("This host does not grant symbolic-link creation")
            with self.assertRaises(ValueError):
                module.load_asset(alias)


if __name__ == "__main__":
    unittest.main()
