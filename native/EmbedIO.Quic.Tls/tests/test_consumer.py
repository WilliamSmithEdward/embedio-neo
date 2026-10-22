"""Package consumers must prove native delivery through the core dependency."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("quic_consumer", Path(__file__).resolve().parents[3] /
                                            "test/EmbedIO.Quic.PackageConsumer/validate.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class ConsumerTests(unittest.TestCase):
    def test_runtime_resolves_transitively_from_exact_core(self):
        group = {"EmbedIO-Neo": {"type": "Direct", "resolved": "2.16.0",
                                "dependencies": {"EmbedIO-Neo.Quic.Native": "2.16.0"}},
                 "EmbedIO-Neo.Quic.Native": {"type": "Transitive", "resolved": "2.16.0"}}
        for change in (None, "rid-valid", "rid-direct", "rid-version", "direct", "missing", "version", "core", "edge", "empty"):
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temporary:
                candidate = copy.deepcopy(group)
                if change == "direct":
                    candidate["EmbedIO-Neo.Quic.Native"]["type"] = "Direct"
                elif change == "missing":
                    del candidate["EmbedIO-Neo.Quic.Native"]
                elif change == "version":
                    candidate["EmbedIO-Neo.Quic.Native"]["resolved"] = "2.15.0"
                elif change == "core":
                    candidate["EmbedIO-Neo"]["type"] = "Project"
                elif change == "edge":
                    candidate["EmbedIO-Neo"]["dependencies"] = {}
                lock = Path(temporary) / "packages.lock.json"
                dependencies = {} if change == "empty" else {"net10.0": candidate}
                if change in ("rid-valid", "rid-direct", "rid-version"):
                    runtime = copy.deepcopy(candidate["EmbedIO-Neo.Quic.Native"])
                    if change == "rid-direct":
                        runtime["type"] = "Direct"
                    elif change == "rid-version":
                        runtime["resolved"] = "2.15.0"
                    dependencies["net10.0/linux-x64"] = {"EmbedIO-Neo.Quic.Native": runtime}
                lock.write_text(json.dumps({"dependencies": dependencies}), encoding="utf-8")
                if change in (None, "rid-valid"):
                    module.verify_transitive_runtime(lock, "2.16.0")
                else:
                    with self.assertRaises(RuntimeError):
                        module.verify_transitive_runtime(lock, "2.16.0")


if __name__ == "__main__":
    unittest.main()
