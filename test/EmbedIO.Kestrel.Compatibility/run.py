"""Run one prebuilt compatibility group with a 55-second total deadline."""

import argparse
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine", choices=("kestrel", "embedio"))
    parser.add_argument("group", choices=("basic", "advanced", "lifecycle", "protocols", "tls", "startup"))
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    assembly = Path(__file__).resolve().parent / "bin/Release/net10.0/EmbedIO.Kestrel.Compatibility.dll"
    started = time.monotonic()
    process = subprocess.Popen([args.dotnet, str(assembly), args.engine, args.group])
    try:
        return process.wait(timeout=max(0.1, 50 - (time.monotonic() - started)))
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)
        raise RuntimeError("Compatibility group exceeded its one-minute total budget") from None


if __name__ == "__main__":
    raise SystemExit(main())
