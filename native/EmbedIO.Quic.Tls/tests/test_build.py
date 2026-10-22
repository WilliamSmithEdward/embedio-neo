"""Exercise the build archive trust boundary without invoking a compiler."""
import importlib.util
import io
import os
from pathlib import Path
import tarfile
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("native_build", Path(__file__).resolve().parents[1] / "build.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ArchiveTests(unittest.TestCase):
    def archive(self, folder, name, kind=tarfile.REGTYPE, link=""):
        archive = folder / "source.tar.gz"
        item = tarfile.TarInfo(name)
        item.type, item.linkname, item.mode = kind, link, 0o7777
        contents = b"source"
        item.size = len(contents) if kind == tarfile.REGTYPE else 0
        with tarfile.open(archive, "w:gz") as bundle:
            bundle.addfile(item, io.BytesIO(contents) if item.size else None)
        return archive

    def test_regular_source_file_and_permissions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            work = root / "work"
            work.mkdir()
            archive = self.archive(root, "openssl-3.5.9/sub/Configure")
            module.extract_source(archive, work)
            result = work / "openssl-3.5.9/sub/Configure"
            self.assertEqual(result.read_bytes(), b"source")
            if os.name != "nt":
                self.assertEqual(result.stat().st_mode & 0o7777, 0o755)

    def test_rejects_paths_links_and_special_files(self):
        cases = [("../outside", tarfile.REGTYPE, ""), ("/absolute", tarfile.REGTYPE, ""),
                 ("wrong-root/file", tarfile.REGTYPE, ""),
                 ("openssl-3.5.9/../../outside", tarfile.REGTYPE, ""),
                 ("openssl-3.5.9/link", tarfile.SYMTYPE, "../outside"),
                 ("openssl-3.5.9/link", tarfile.LNKTYPE, "../outside"),
                 ("openssl-3.5.9/device", tarfile.CHRTYPE, "")]
        for name, kind, link in cases:
            with self.subTest(name=name, kind=kind), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                sentinel = root / "outside"
                sentinel.write_bytes(b"unchanged")
                work = root / "work"
                work.mkdir()
                with self.assertRaises(RuntimeError):
                    module.extract_source(self.archive(root, name, kind, link), work)
                self.assertEqual(sentinel.read_bytes(), b"unchanged")
                self.assertEqual(list(work.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
