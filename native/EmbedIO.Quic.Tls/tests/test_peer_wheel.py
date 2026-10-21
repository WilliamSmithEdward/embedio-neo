"""Pinned peer source archives, wheel records and workflow source identity."""
import base64
import csv
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location("peer_wheel", ROOT / "build_peer_wheel.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PeerWheelTests(unittest.TestCase):
    def wheel(self, path, changes=None):
        prefix = "cryptography-50.0.2.dist-info/"
        files = {prefix + "METADATA": b"Name: cryptography\nVersion: 50.0.2\n",
                 prefix + "WHEEL": b"Wheel-Version: 1.0\nRoot-Is-Purelib: false\nTag: cp39-abi3-macosx_15_0_x86_64\n",
                 "cryptography/__init__.py": b'__version__ = "50.0.2"\n',
                 "cryptography/hazmat/bindings/_rust.abi3.so": b"native fixture"}
        files.update({prefix + "licenses/" + name: name.encode() for name in ("LICENSE", "LICENSE.APACHE", "LICENSE.BSD")})
        files.update(changes or {})
        record = prefix + "RECORD"
        rows = [(name, "sha256=" + base64.urlsafe_b64encode(hashlib.sha256(data).digest()).rstrip(b"=").decode(), str(len(data)))
                for name, data in files.items()]
        output = io.StringIO(newline="")
        csv.writer(output, lineterminator="\n").writerows([*rows, (record, "", "")])
        files[record] = output.getvalue().encode()
        with zipfile.ZipFile(path, "w") as wheel:
            for name, data in files.items():
                wheel.writestr(name, data)
        return files

    def test_license_preserves_vendor_bytes_and_rebuilds_record(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = self.wheel(root / "original.whl")
            module.add_license(root / "original.whl", root / "licensed.whl", b"linked license")
            _, licensed = module.wheel_contents(root / "licensed.whl")
            for name, data in original.items():
                if not name.endswith("/RECORD"):
                    self.assertEqual(licensed[name], data)
            self.assertEqual(licensed["cryptography-50.0.2.dist-info/licenses/OpenSSL-LICENSE.txt"], b"linked license")

    def test_wheel_rejects_version_path_and_missing_license(self):
        for change in ({"cryptography-50.0.2.dist-info/METADATA": b"Name: cryptography\nVersion: 49.0.0\n"},
                       {"../escaped.py": b"escape"}, {"cryptography/../escaped.py": b"escape"},
                       {"/tmp/escaped.py": b"escape"}, {"cryptography\\escaped.py": b"escape"},
                       {"foreign/__init__.py": b"foreign"}):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                wheel = Path(temporary) / "test.whl"
                self.wheel(wheel, change)
                with self.assertRaises(ValueError):
                    module.wheel_contents(wheel)
        with tempfile.TemporaryDirectory() as temporary:
            wheel = Path(temporary) / "test.whl"
            self.wheel(wheel)
            with zipfile.ZipFile(wheel) as original:
                files = {name: original.read(name) for name in original.namelist() if not name.endswith("LICENSE.BSD")}
            with zipfile.ZipFile(wheel, "w") as changed:
                for name, data in files.items():
                    changed.writestr(name, data)
            with self.assertRaises(ValueError):
                module.wheel_contents(wheel)

    def test_wheel_rejects_payload_and_record_changes(self):
        for target in ("cryptography/__init__.py", "cryptography-50.0.2.dist-info/RECORD"):
            with self.subTest(target=target), tempfile.TemporaryDirectory() as temporary:
                wheel = Path(temporary) / "test.whl"
                files = self.wheel(wheel)
                files[target] += b"changed"
                with zipfile.ZipFile(wheel, "w") as changed:
                    for name, data in files.items():
                        changed.writestr(name, data)
                with self.assertRaises(ValueError):
                    module.wheel_contents(wheel)

    def test_source_archive_paths_links_and_duplicates(self):
        for name, kind in (("cryptography-50.0.2/src/test.py", tarfile.REGTYPE),
                           ("cryptography-50.0.2/../escape", tarfile.REGTYPE),
                           ("other/src/test.py", tarfile.REGTYPE),
                           ("cryptography-50.0.2/link", tarfile.SYMTYPE),
                           ("cryptography-50.0.2/link", tarfile.LNKTYPE)):
            with self.subTest(name=name, kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                archive = root / "source.tar.gz"
                entry = tarfile.TarInfo(name)
                entry.type = kind
                entry.linkname = "../../escape"
                entry.size = 4 if kind == tarfile.REGTYPE else 0
                with tarfile.open(archive, "w:gz") as bundle:
                    bundle.addfile(entry, io.BytesIO(b"test") if entry.size else None)
                output = root / "output"
                output.mkdir()
                if name == "cryptography-50.0.2/src/test.py":
                    module.extract(archive, output)
                    self.assertEqual((output / name).read_bytes(), b"test")
                else:
                    with self.assertRaises(ValueError):
                        module.extract(archive, output)
                self.assertFalse((root / "escape").exists())
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "source.tar.gz"
            with tarfile.open(archive, "w:gz") as bundle:
                for _ in range(2):
                    bundle.addfile(tarfile.TarInfo("cryptography-50.0.2/repeated"), io.BytesIO())
            with self.assertRaises(ValueError):
                module.extract(archive, root / "output")

    def test_artifact_matches_exact_source_native_receipt_and_wheel(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            original = root / "original.whl"
            self.wheel(original)
            output = root / "artifact"
            output.mkdir()
            wheel = output / "cryptography-50.0.2-cp39-abi3-macosx_15_0_x86_64.whl"
            module.add_license(original, wheel, (ROOT / "OpenSSL-LICENSE.txt").read_bytes())
            receipt = {"schemaVersion": 1, "sourceCommit": "a" * 40, "cryptographyVersion": module.VERSION,
                       "sourceUrl": module.URL, "sourceArchiveSha256": module.ARCHIVE_SHA256, "rustVersion": module.RUST,
                       "opensslVersion": module.pins.VERSION, "opensslArchiveSha256": module.pins.SHA256,
                       "nativeBuildReceiptSha256": module.digest(b"native receipt"), "wheel": wheel.name,
                       "wheelSha256": module.digest(wheel.read_bytes()),
                       "buildSourceHashes": {name: module.digest(module.canonical((ROOT / name).read_bytes())) for name in
                                             ("build_peer_wheel.py", "peer-wheel-requirements.txt")}}
            path = output / "peer-wheel.json"
            path.write_text(json.dumps(receipt))
            self.assertEqual(module.verify_artifact(output, "a" * 40, b"native receipt"), receipt)
            for commit, native in (("b" * 40, b"native receipt"), ("a" * 40, b"other native receipt")):
                with self.assertRaises(ValueError):
                    module.verify_artifact(output, commit, native)
            for field in ("wheelSha256", "buildSourceHashes", "cryptographyVersion", "sourceArchiveSha256"):
                changed = {**receipt, field: "changed"}
                path.write_text(json.dumps(changed))
                with self.assertRaises(ValueError):
                    module.verify_artifact(output, "a" * 40, b"native receipt")
            path.write_text(json.dumps(receipt))
            (output / "extra.whl").write_bytes(b"unexpected")
            with self.assertRaises(ValueError):
                module.verify_artifact(output, "a" * 40, b"native receipt")


if __name__ == "__main__":
    unittest.main()
