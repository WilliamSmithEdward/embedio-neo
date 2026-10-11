"""Run all regressions with the long quiet-period case isolated on copied binaries."""

import argparse
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
QUIET_TEST = "EmbedIO.Tests.TcpDrainReplacementTest.ReplacementAcceptsFreshRequestAfterFortySecondsWithoutTraffic"


def run(timeout, minimum):
    source = ROOT / "test/EmbedIO.Tests/bin/Release/net10.0"
    destination = ROOT / "TestResults/tcp-quiet-runner"
    if destination.exists():
        raise FileExistsError(f"Do not reuse earlier run evidence: {destination}")
    shutil.copytree(source, destination)
    quiet_results = ROOT / "TestResults/tcp-quiet"
    quiet_log = ROOT / "TestResults/tcp-quiet.log"
    main_command = ["dotnet", "test", "--project", "test/EmbedIO.Tests/EmbedIO.Tests.csproj",
                    "-c", "Release", "--no-build", "--report-trx", "--results-directory", "TestResults",
                    "--timeout", timeout, "--minimum-expected-tests", str(minimum - 1), "--coverlet",
                    "--filter", f"FullyQualifiedName!~{QUIET_TEST}"]
    quiet_command = ["dotnet", str(destination / "EmbedIO.Tests.dll"),
                     "--filter", f"FullyQualifiedName~{QUIET_TEST}", "--report-trx",
                     "--results-directory", str(quiet_results), "--timeout", "2m",
                     "--minimum-expected-tests", "1", "--coverlet"]
    with quiet_log.open("xb") as log:
        quiet = subprocess.Popen(quiet_command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT)
        try:
            main = subprocess.run(main_command, cwd=ROOT, check=False)
            quiet_status = quiet.wait()
        finally:
            if quiet.poll() is None:
                quiet.terminate()
                quiet.wait()
    print(quiet_log.read_text(encoding="utf-8-sig", errors="replace"))
    print(f"Main regression status: {main.returncode}; required quiet-period status: {quiet_status}")
    if quiet_status:
        # A quiet-case failure must never look like the main suite's
        # narrowly classified MsQuic rebind exit code 2.
        return 1
    return main.returncode


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", required=True, help="Unchanged platform regression budget")
    parser.add_argument("--minimum", required=True, type=int, help="Combined minimum across both required processes")
    args = parser.parse_args()
    if args.minimum < 2:
        parser.error("The combined minimum must include the main suite and the quiet case.")
    sys.exit(run(args.timeout, args.minimum))
