"""Stage the pinned, instrumented test-only endurance harness under TestResults."""

import argparse
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
HARNESS_REVISION = "d6097ea7a28f40266171e77537e10f8160c1bda9"
HARNESS_PATH = "test/EmbedIO.LoadBenchmark/"


def prepare(label):
    if not label or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-_" for c in label):
        raise ValueError("Use a lowercase alphanumeric label with hyphens or underscores.")
    destination = (ROOT / "TestResults" / label / "EnduranceHarness").resolve()
    if not destination.is_relative_to(ROOT.resolve() / "TestResults"):
        raise ValueError("The staging path must stay inside this checkout's TestResults directory.")
    if destination.exists():
        raise FileExistsError(f"Preserve previous evidence; choose a new label: {destination}")
    names = subprocess.check_output(
        ["git", "ls-tree", "--name-only", HARNESS_REVISION, HARNESS_PATH], cwd=ROOT,
        text=True, encoding="utf-8",
    ).splitlines()
    if not names:
        raise RuntimeError("Fetch the pinned PR #276 harness commit before staging it.")
    names = subprocess.check_output(
        ["git", "ls-tree", "-r", "--name-only", HARNESS_REVISION, HARNESS_PATH], cwd=ROOT,
        text=True, encoding="utf-8",
    ).splitlines()
    destination.mkdir(parents=True)
    for name in names:
        relative = Path(name).relative_to(HARNESS_PATH)
        if len(relative.parts) != 1 or not (
            relative.suffix == ".cs" or relative.name in
            ("EmbedIO.LoadBenchmark.csproj", "packages.lock.json", "README.md")
        ):
            continue
        content = subprocess.check_output(["git", "show", f"{HARNESS_REVISION}:{name}"], cwd=ROOT)
        (destination / relative).write_bytes(content)
    project = destination / "EmbedIO.LoadBenchmark.csproj"
    text = project.read_text(encoding="utf-8-sig")
    project.write_text(text.replace("../../src/EmbedIO/EmbedIO.csproj", "../../../src/EmbedIO/EmbedIO.csproj"), encoding="utf-8-sig")
    patch = ROOT / "test" / "EmbedIO.LoadBenchmark" / "tcp-endurance-diagnostics.patch"
    directory = destination.relative_to(ROOT).as_posix()
    command = ["git", "apply", f"--directory={directory}", "--whitespace=error", str(patch)]
    subprocess.run(command[:2] + ["--check"] + command[2:], cwd=ROOT, check=True)
    subprocess.run(command, cwd=ROOT, check=True)
    print(destination)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("label", help="New ignored TestResults subdirectory; previous outputs are never overwritten")
    prepare(parser.parse_args().label)
