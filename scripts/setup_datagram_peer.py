"""Provision the hash-locked test peer in an isolated runner temporary directory."""
import os
from pathlib import Path
import subprocess
import venv


def main():
    peer_root = Path(os.environ["RUNNER_TEMP"]) / "embedio-datagram-peer"
    venv.EnvBuilder(with_pip=True).create(peer_root)
    peer_python = peer_root / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
    requirements = Path(__file__).resolve().parents[1] / "test/EmbedIO.Conformance/drivers/requirements.txt"
    subprocess.run([str(peer_python), "-m", "pip", "install", "--require-hashes",
                    "--only-binary=:all:", "-r", str(requirements)], check=True)
    with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8", newline="\n") as stream:
        stream.write(f"EMBEDIO_DATAGRAM_PEER_PYTHON={peer_python}\n")
        stream.write("EMBEDIO_REQUIRE_DATAGRAM_PEER=1\n")


if __name__ == "__main__":
    main()
