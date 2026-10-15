"""Propose the newest .NET SDK patch and workload set for the feature band global.json pins.

global.json pins the exact SDK with roll-forward disabled, and other pins follow
it. update-dotnet-sdk.yml runs this script each week. It can open three pull
requests, each moving its pins together:

- An SDK move: `sdk.version` in global.json, each workflow line
  `dotnet-version: <old SDK>`, each `<RuntimeFrameworkVersion>` naming the old
  SDK's runtime (the test probes that need an exact runtime), and every
  packages.lock.json restored again with the new SDK. The MAUI locks need
  workloads this Linux job does not install, so they take the package moves the
  other locks show; the MAUI jobs' locked restores then check them.
- A workload set move: each `dotnet workload install ... --version <set>`. The
  set decides the Mono runtime the MAUI apps run and the Apple and Android SDK
  packs, so it moves on its own, deliberately, rather than with every SDK patch.
- An SDK image move: a Dockerfile pinning `mcr.microsoft.com/dotnet/sdk` by
  digest follows the SDK global.json pins, once MCR publishes that tag (it
  often lags the release by hours).

Rules:
- Only the channel and feature band global.json names, such as 10.0.4xx. A new
  band needs its own workload set and a person, so it is reported, not proposed.
- An SDK comes from Microsoft's releases.json for the channel; its archive is
  checked against the SHA-512 recorded there before it runs.
- A workload set comes from nuget.org; the package is checked against the
  SHA-512 nuget.org's catalog records before it is read. Only sets for the
  pinned SDK patch or earlier are taken.
- Each waits out a seven-day cooldown, waived when it brings a runtime that
  Microsoft marks as a security release.
- Nothing moves back, and a pin moved ahead by hand is kept.
- An SDK move may only change versions from the old runtime's to the new
  runtime's in the lock files (the SDK's implicit packages carry the runtime
  version). Any other lock-file change stops the update for a person.

Usage, from the two jobs of update-dotnet-sdk.yml:

    python scripts/update_dotnet_sdk.py prepare --out DIR --sdk-dir DIR
    python scripts/update_dotnet_sdk.py propose --in DIR

`prepare` runs with a read-only token. It works out the moves, downloads and
checks what it uses, rewrites the pins, restores, and writes each proposal's
changed files and pull request text under DIR. It sets `changed` in
$GITHUB_OUTPUT.

`propose` runs with write access and trusts nothing in DIR it cannot check: a
proposal must come from the commit and branch the workflow ran on, and every
changed file must be one its move may change, changed only as the rules allow.
It commits through the GitHub API (no git credentials on disk), opens each pull
request against that branch and leaves it open for exact-head review. A workload
set's pull request opens as a draft labelled "needs owner decision", because the
set can move the Apple packs to a newer Xcode; that call is the owner's. A pull
request opened with the workflow's own token starts no workflows, so it starts
CI, Security and Malware scan on the new branch itself.
"""
from __future__ import annotations

import argparse
import base64
import datetime as dt
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tarfile
import urllib.error
import urllib.request
import zipfile
from collections.abc import Callable
from pathlib import Path, PurePosixPath
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / ".github" / "security"))
from merge_review import defer_merge  # noqa: E402  (the repository's merge-review policy)

RELEASES_URL = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/{channel}/releases.json"
ARCHIVE_URL = "https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/"
RELEASE_NOTES = "https://github.com/dotnet/core/blob/main/release-notes/{channel}/{release}/{release}.md"
NUGET = "https://api.nuget.org/"
NUGET_INDEX = NUGET + "v3-flatcontainer/{id}/index.json"
NUGET_REGISTRATION = NUGET + "v3/registration5-semver1/{id}/{version}.json"
NUGET_PACKAGE = NUGET + "v3-flatcontainer/{id}/{version}/{id}.{version}.nupkg"
WORKLOAD_PACKAGE = "microsoft.net.workloads.{major}.{minor}.{band}00"
WORKLOAD_SET_FILE = "data/microsoft.net.workloads.workloadset.json"
MONO_TOOLCHAIN = "Microsoft.NET.Workload.Mono.ToolChain.Current"
MCR_MANIFEST = "https://mcr.microsoft.com/v2/dotnet/sdk/manifests/{tag}"
MCR_ACCEPT = "application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json"
COOLDOWN = dt.timedelta(days=7)
KINDS = ("sdk", "workloads", "image")
# Lock files whose projects need MAUI workloads to restore.
MAUI_LOCKS = frozenset({
    "test/EmbedIO.AndroidSmoke/packages.lock.json",
    "test/EmbedIO.MacCatalystSmoke/packages.lock.json",
    "test/EmbedIO.MauiHttpsSmoke/packages.android.lock.json",
    "test/EmbedIO.MauiHttpsSmoke/packages.ios.lock.json",
    "test/EmbedIO.MauiHttpsSmoke/packages.maccatalyst.lock.json",
    "test/EmbedIO.MauiHttpsSmoke/packages.windows.lock.json",
})
WORKLOAD_PIN_FILES = (".github/workflows/*.yml", "scripts/*.ps1", "scripts/*.sh")
# The required checks. They are started by hand because the pull request is not.
GATES = ("ci.yml", "security.yml", "malware-scan.yml")
LABELS = (".NET", "dependencies")
# A workload set decides the MAUI apps' Apple and Android packs, and an Apple pack move can need a
# newer Xcode, so those pull requests open as drafts that wait for the owner's decision.
OWNER_DECISION_KINDS = frozenset({"workloads"})
OWNER_DECISION_LABEL = "needs owner decision"
APPLE_MANIFEST = re.compile(r"Microsoft\.NET\.Sdk\.(iOS|MacCatalyst|macOS|tvOS)", re.IGNORECASE)
STABLE = re.compile(r"(\d+)\.(\d+)\.(\d+)")
LOCK_NAME = re.compile(r"packages(\.[a-z]+)?\.lock\.json")
SHA512_HEX = re.compile(r"[0-9a-f]{128}")
DOTNET_VERSION_LINE = re.compile(r"^(\s*dotnet-version:\s*)(\S+)(\s*)$")
RUNTIME_PIN = re.compile(r"<RuntimeFrameworkVersion>([^<]*)</RuntimeFrameworkVersion>")
WORKLOAD_INSTALL = re.compile(r"dotnet workload install\b.*?--version\s+(\S+)")
SDK_IMAGE_COMMENT = re.compile(r"^# mcr\.microsoft\.com/dotnet/sdk:(\S+) \(runtime (\S+), ([^)]+)\)\.$")
SDK_IMAGE_FROM = re.compile(r"^FROM mcr\.microsoft\.com/dotnet/sdk@(sha256:[0-9a-f]{64})$")
DIGEST = re.compile(r"sha256:[0-9a-f]{64}")

Api = Callable[..., Any]
FetchJson = Callable[[str], Any]
FetchBytes = Callable[[str], bytes]
Lookup = Callable[[str], "str | None"]


class ApiError(RuntimeError):
    pass


def gh_api(endpoint: str, method: str = "GET", payload: Any = None) -> Any:
    args = ["gh", "api", "--method", method, endpoint]
    if payload is not None:
        args += ["--input", "-"]
    result = subprocess.run(args, input=json.dumps(payload) if payload is not None else None,
                            capture_output=True, text=True, encoding="utf-8", timeout=120)
    if result.returncode != 0:
        raise ApiError(f"{method} {endpoint}: {(result.stdout + result.stderr).strip()}")
    return json.loads(result.stdout) if result.stdout.strip() else None


def fetch_json(url: str) -> Any:
    with urllib.request.urlopen(url, timeout=60) as response:
        return json.load(response)


def fetch_bytes(url: str) -> bytes:
    with urllib.request.urlopen(url, timeout=120) as response:
        return response.read()


def version(text: str) -> tuple[int, int, int]:
    match = STABLE.fullmatch(text)
    if not match:
        raise ValueError(f"not a stable .NET version: {text!r}")
    major, minor, patch = (int(part) for part in match.groups())
    return major, minor, patch


def feature_band(sdk: str) -> tuple[int, int, int]:
    major, minor, patch = version(sdk)
    return major, minor, patch // 100


def released(release: dict[str, Any]) -> dt.datetime:
    return dt.datetime.fromisoformat(release["release-date"]).replace(tzinfo=dt.timezone.utc)


def runtime_of(releases: dict[str, Any], sdk: str) -> str:
    for release in releases["releases"]:
        for entry in release.get("sdks") or []:
            if entry["version"] == sdk:
                return entry["runtime-version"]
    raise ValueError(f"releases.json lists no SDK {sdk}")


def security_release(releases: dict[str, Any], runtime: str) -> bool:
    return any(release["release-version"] == runtime and release.get("security") is True
               for release in releases["releases"])


def choose(releases: dict[str, Any], current: str, now: dt.datetime) -> tuple[dict[str, Any] | None, list[str]]:
    """The SDK to move to, or None, with notes for the job summary."""
    notes, candidates, newer_band = [], [], None
    for release in releases["releases"]:
        security = release.get("security") is True
        for entry in release.get("sdks") or []:
            if not STABLE.fullmatch(entry["version"]):
                continue
            if feature_band(entry["version"]) > feature_band(current):
                newer_band = max(newer_band or entry["version"], entry["version"], key=version)
            if feature_band(entry["version"]) != feature_band(current):
                continue
            if version(entry["version"]) <= version(current):
                continue
            if not security and now - released(release) < COOLDOWN:
                notes.append(f"SDK {entry['version']} ({release['release-date']}) waits for the "
                             "seven-day cooldown.")
                continue
            candidates.append((entry, release))
    if newer_band:
        notes.append(f"SDK {newer_band} starts a newer feature band. Moving bands needs that band's "
                     "workload set, so it is left to a person.")
    if not candidates:
        return None, notes
    entry, release = max(candidates, key=lambda pair: version(pair[0]["version"]))
    return {
        "sdk": entry["version"],
        "runtime": entry["runtime-version"],
        "release": release["release-version"],
        "release_date": release["release-date"],
        "security": release.get("security") is True,
        "cves": [cve["cve-id"] for cve in release.get("cve-list") or []],
        "files": entry["files"],
    }, notes


def platform_rid() -> str:
    machine = os.uname().machine if hasattr(os, "uname") else os.environ.get("PROCESSOR_ARCHITECTURE", "")
    arch = {"x86_64": "x64", "amd64": "x64", "AMD64": "x64", "aarch64": "arm64", "arm64": "arm64",
            "ARM64": "arm64"}.get(machine)
    system = {"linux": "linux", "darwin": "osx", "win32": "win"}.get(sys.platform)
    if not arch or not system:
        raise ValueError(f"no .NET SDK archive for {sys.platform}/{machine}")
    return f"{system}-{arch}"


def archive_entry(files: list[dict[str, Any]], sdk: str, rid: str) -> dict[str, str]:
    """The SDK archive for `rid`, with the SHA-512 releases.json records for it."""
    name = f"dotnet-sdk-{rid}{'.zip' if rid.startswith('win-') else '.tar.gz'}"
    matches = [f for f in files if f.get("rid") == rid and f.get("name") == name]
    if len(matches) != 1:
        raise ValueError(f"releases.json has no single {name} for SDK {sdk}")
    entry = matches[0]
    if not entry["url"].startswith(ARCHIVE_URL.format(version=sdk)):
        raise ValueError(f"{name} for SDK {sdk} is not served from {ARCHIVE_URL.format(version=sdk)}")
    if not SHA512_HEX.fullmatch(entry.get("hash", "")):
        raise ValueError(f"releases.json records no SHA-512 for {name} of SDK {sdk}")
    return {"name": name, "url": entry["url"], "sha512": entry["hash"]}


def download(url: str, target: Path) -> str:
    """Download `url` to `target`; returns the file's SHA-512."""
    digest = hashlib.sha512()
    with urllib.request.urlopen(url, timeout=120) as response, target.open("wb") as stream:
        while chunk := response.read(1 << 20):
            digest.update(chunk)
            stream.write(chunk)
    return digest.hexdigest()


def sha512_of(path: Path) -> str:
    digest = hashlib.sha512()
    with path.open("rb") as stream:
        while chunk := stream.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def extract(archive: Path, target: Path) -> None:
    target.mkdir(parents=True, exist_ok=True)
    if archive.suffix == ".zip":
        with zipfile.ZipFile(archive) as package:
            for name in package.namelist():
                path = PurePosixPath(name)
                if path.is_absolute() or ".." in path.parts:
                    raise ValueError(f"unsafe path {name!r} in {archive.name}")
            package.extractall(target)
    else:
        with tarfile.open(archive, "r:gz") as package:
            package.extractall(target, filter="data")


def tracked(root: Path, *patterns: str) -> list[str]:
    result = subprocess.run(["git", "-C", str(root), "ls-files", "-z", "--", *patterns],
                            capture_output=True, check=True, timeout=120)
    return sorted(path for path in result.stdout.decode("utf-8").split("\0") if path)


def lock_files(root: Path) -> list[str]:
    return [path for path in tracked(root, "*.lock.json") if LOCK_NAME.fullmatch(PurePosixPath(path).name)]


def read(path: Path) -> str:
    return path.read_bytes().decode("utf-8")


def write(path: Path, text: str) -> None:
    path.write_bytes(text.encode("utf-8"))


def rewrite_global_json(text: str, old: str, new: str) -> str:
    if json.loads(text.lstrip("﻿"))["sdk"]["version"] != old:
        raise ValueError(f"global.json does not pin SDK {old}")
    updated, count = re.subn(r'("version"\s*:\s*")' + re.escape(old) + '"',
                             lambda match: match.group(1) + new + '"', text)
    if count != 1 or json.loads(updated.lstrip("﻿"))["sdk"]["version"] != new:
        raise ValueError("global.json has an unexpected shape")
    return updated


def rewrite_lines(text: str, pattern: re.Pattern[str], group: int, old: str, new: str) -> tuple[str, int]:
    """Replace `old` with `new` where it is group `group` of a `pattern` match, line by line."""
    count = 0

    def replace(match: re.Match[str]) -> str:
        nonlocal count
        if match.group(group) != old:
            return match.group(0)
        count += 1
        start, end = match.start(group) - match.start(), match.end(group) - match.start()
        return match.group(0)[:start] + new + match.group(0)[end:]

    lines = []
    for line in text.splitlines(keepends=True):
        body = line.rstrip("\r\n")
        lines.append(pattern.sub(replace, body) + line[len(body):])
    return "".join(lines), count


def swap(text: str, old: str, new: str) -> str | None:
    """`text` with the whole version `old` replaced by `new`, or None when `old` is not in it."""
    replaced, count = re.subn(r"(?<![\d.])" + re.escape(old) + r"(?![\d.])", new, text)
    return replaced if count else None


def lock_entries(lock: dict[str, Any]) -> dict[tuple[str, str], dict[str, Any]]:
    return {(target, name): entry for target, packages in lock["dependencies"].items()
            for name, entry in packages.items()}


def check_lock_change(path: str, old_text: str, new_text: str, old_runtime: str, new_runtime: str) -> None:
    """Allow only moves from the old runtime's version to the new runtime's."""
    def moved(before: Any, after: Any) -> bool:
        return isinstance(before, str) and after == swap(before, old_runtime, new_runtime)

    old, new = json.loads(old_text.lstrip("﻿")), json.loads(new_text.lstrip("﻿"))
    if {k: v for k, v in old.items() if k != "dependencies"} != {k: v for k, v in new.items() if k != "dependencies"}:
        raise ValueError(f"{path}: the move changes more than the dependencies")
    before, after = lock_entries(old), lock_entries(new)
    if before.keys() != after.keys():
        raise ValueError(f"{path}: the move adds or removes packages: "
                         f"{sorted(set(before) ^ set(after))[:5]}")
    for key, entry in before.items():
        update = after[key]
        if entry == update:
            continue
        if entry.keys() != update.keys():
            raise ValueError(f"{path}: {key[1]} changes shape")
        resolved_moved = entry.get("resolved") != update.get("resolved")
        for field in entry:
            if entry[field] == update[field]:
                continue
            if field in ("resolved", "requested") and moved(entry[field], update[field]):
                continue
            if field == "contentHash" and resolved_moved:
                continue
            if field == "dependencies" and entry[field].keys() == update[field].keys() and all(
                    entry[field][name] == update[field][name] or moved(entry[field][name], update[field][name])
                    for name in entry[field]):
                continue
            raise ValueError(f"{path}: {key[1]} ({key[0]}) changes {field} beyond a "
                             f"{old_runtime} -> {new_runtime} move")


def package_moves(changes: dict[str, tuple[str, str]]) -> dict[str, tuple[str, str, str]]:
    """Package -> (old version, new version, new content hash), from regenerated locks."""
    moves: dict[str, tuple[str, str, str]] = {}
    for path, (old_text, new_text) in changes.items():
        before, after = lock_entries(json.loads(old_text.lstrip("﻿"))), lock_entries(json.loads(new_text.lstrip("﻿")))
        for key, entry in before.items():
            update = after[key]
            if "resolved" not in entry or entry["resolved"] == update["resolved"]:
                continue
            move = (entry["resolved"], update["resolved"], update["contentHash"])
            if moves.setdefault(key[1], move) != move:
                raise ValueError(f"{path}: {key[1]} moves inconsistently across lock files")
    return moves


def mirror(text: str, moves: dict[str, tuple[str, str, str]]) -> str:
    """Apply the regenerated locks' package moves to a lock file that cannot be restored here."""
    lock = json.loads(text.lstrip("﻿"))
    for packages in lock["dependencies"].values():
        for name, entry in packages.items():
            move = moves.get(name)
            if move and entry.get("resolved") == move[0]:
                entry["resolved"], entry["contentHash"] = move[1], move[2]
                if "requested" in entry:
                    entry["requested"] = swap(entry["requested"], move[0], move[1]) or entry["requested"]
            for dependency, requested in (entry.get("dependencies") or {}).items():
                move = moves.get(dependency)
                if move and requested in (move[0], f"[{move[0]}, )"):
                    entry["dependencies"][dependency] = swap(requested, move[0], move[1])
    newline = "\r\n" if "\r\n" in text else "\n"
    rendered = json.dumps(lock, indent=2, ensure_ascii=False)
    if text.rstrip("\r\n") != text:
        rendered += "\n"
    return ("﻿" if text.startswith("﻿") else "") + rendered.replace("\n", newline)


def dotnet_env(sdk_dir: Path) -> dict[str, str]:
    env = dict(os.environ)
    env.update(DOTNET_ROOT=str(sdk_dir), DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_NOLOGO="1",
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_MULTILEVEL_LOOKUP="0")
    env["PATH"] = str(sdk_dir) + os.pathsep + env.get("PATH", "")
    return env


def restore_locks(root: Path, sdk_dir: Path, logs: Path) -> dict[str, tuple[str, str]]:
    """Restore every lock project but the MAUI ones; returns path -> (old text, new text)."""
    dotnet = sdk_dir / ("dotnet.exe" if os.name == "nt" else "dotnet")
    logs.mkdir(parents=True, exist_ok=True)
    locks = lock_files(root)
    unknown = [path for path in locks if path not in MAUI_LOCKS and PurePosixPath(path).name != "packages.lock.json"]
    if unknown:
        raise ValueError(f"lock files this script does not know how to restore: {unknown}")
    old = {path: read(root / path) for path in locks}
    for path in locks:
        if path in MAUI_LOCKS:
            continue
        projects = sorted((root / path).parent.glob("*.csproj"))
        if len(projects) != 1:
            raise ValueError(f"{path} has no single project beside it")
        log = logs / (path.replace("/", "_") + ".log")
        with log.open("w", encoding="utf-8") as stream:
            result = subprocess.run([str(dotnet), "restore", str(projects[0]), "--force-evaluate",
                                     "--no-http-cache", "-v", "q"], cwd=root, env=dotnet_env(sdk_dir),
                                    stdout=stream, stderr=subprocess.STDOUT, timeout=900)
        if result.returncode != 0:
            raise RuntimeError(f"restore failed for {path}; see {log}")
    return {path: (old[path], read(root / path)) for path in locks
            if path not in MAUI_LOCKS and read(root / path) != old[path]}


# Workload sets: nuget.org package microsoft.net.workloads.<band> version 10.401.1
# is workload set 10.0.401.1; 10.401.0 is 10.0.401.

def set_key(workload_set: str) -> tuple[int, ...]:
    if not re.fullmatch(r"\d+\.\d+\.\d+(\.\d+)?", workload_set):
        raise ValueError(f"not a workload set version: {workload_set!r}")
    return tuple(int(part) for part in workload_set.split("."))


def set_from_package(package_version: str, major: int, minor: int) -> str | None:
    match = STABLE.fullmatch(package_version)
    if not match or int(match.group(1)) != major:
        return None
    revision = int(match.group(3))
    return f"{major}.{minor}.{int(match.group(2))}" + (f".{revision}" if revision else "")


def package_from_set(workload_set: str) -> str:
    major, _, patch, *revision = set_key(workload_set)
    return f"{major}.{patch}.{revision[0] if revision else 0}"


def toolchain_runtime(manifests: dict[str, str]) -> str:
    """The runtime of the Mono packs a set installs: toolchain manifest 10.0.112 is runtime 10.0.12."""
    major, minor, patch = version(manifests[MONO_TOOLCHAIN].split("/")[0])
    if patch // 100 != 1:
        raise ValueError(f"unexpected {MONO_TOOLCHAIN} version {manifests[MONO_TOOLCHAIN]}")
    return f"{major}.{minor}.{patch % 100}"


def workload_pins(root: Path) -> tuple[str | None, list[str]]:
    """The workload set every `dotnet workload install ... --version` names (None when nothing
    installs a workload), and the files naming it."""
    versions, files = set(), []
    for path in tracked(root, *WORKLOAD_PIN_FILES):
        found = {match.group(1) for line in read(root / path).splitlines() for match in WORKLOAD_INSTALL.finditer(line)}
        if found:
            versions |= found
            files.append(path)
    if len(versions) > 1:
        raise ValueError(f"the workload installs name {sorted(versions)}, not one workload set")
    return (versions.pop() if versions else None), files


def workload_set(package_id: str, workload: str, fetch: FetchJson, get: FetchBytes) -> tuple[dict[str, str], str]:
    """The set's manifest versions and publication time, from a package checked against nuget.org's catalog."""
    package_version = package_from_set(workload)
    registration = fetch(NUGET_REGISTRATION.format(id=package_id, version=package_version))
    if registration.get("listed") is False:
        raise ValueError(f"workload set {workload} is unlisted")
    if not registration["catalogEntry"].startswith(NUGET):
        raise ValueError(f"workload set {workload} has a catalog entry outside nuget.org")
    catalog = fetch(registration["catalogEntry"])
    if catalog.get("packageHashAlgorithm") != "SHA512":
        raise ValueError(f"nuget.org records no SHA-512 for workload set {workload}")
    content = get(NUGET_PACKAGE.format(id=package_id, version=package_version))
    if base64.b64encode(hashlib.sha512(content).digest()).decode("ascii") != catalog["packageHash"]:
        raise ValueError(f"workload set {workload} does not match the SHA-512 in nuget.org's catalog")
    with zipfile.ZipFile(io.BytesIO(content)) as package:
        manifests = json.loads(package.read(WORKLOAD_SET_FILE).decode("utf-8-sig"))
    return manifests, registration["published"]


def choose_workloads(current: str, sdk: str, releases: dict[str, Any], fetch: FetchJson, get: FetchBytes,
                     now: dt.datetime) -> tuple[dict[str, Any] | None, list[str]]:
    """The workload set to move to, or None, with notes for the job summary."""
    major, minor, patch = version(sdk)
    package_id = WORKLOAD_PACKAGE.format(major=major, minor=minor, band=patch // 100)
    if set_key(current)[:2] != (major, minor) or set_key(current)[2] // 100 != patch // 100:
        raise ValueError(f"workload set {current} is not in the feature band of SDK {sdk}")
    sets = {s for s in (set_from_package(v, major, minor) for v in fetch(NUGET_INDEX.format(id=package_id))["versions"]) if s}
    notes = []
    current_manifests, _ = workload_set(package_id, current, fetch, get)
    current_runtime = toolchain_runtime(current_manifests)
    for candidate in sorted(sets, key=set_key, reverse=True):
        if set_key(candidate) <= set_key(current):
            break
        if set_key(candidate)[2] > patch:
            notes.append(f"Workload set {candidate} waits for SDK {major}.{minor}.{set_key(candidate)[2]}.")
            continue
        manifests, published = workload_set(package_id, candidate, fetch, get)
        runtime = toolchain_runtime(manifests)
        security = version(runtime) > version(current_runtime) and security_release(releases, runtime)
        published_at = dt.datetime.fromisoformat(published.replace("Z", "+00:00"))
        if not security and now - published_at < COOLDOWN:
            notes.append(f"Workload set {candidate} ({published[:10]}) waits for the seven-day cooldown.")
            continue
        return {"set": candidate, "runtime": runtime, "old_runtime": current_runtime, "security": security,
                "published": published[:10], "manifests": manifests, "old_manifests": current_manifests}, notes
    return None, notes


# SDK images: a Dockerfile pins one as a comment naming the tag above a FROM line with its digest.

def mcr_digest(tag: str) -> str | None:
    """MCR's manifest-list digest for dotnet/sdk:<tag>, or None when the tag is not published yet."""
    request = urllib.request.Request(MCR_MANIFEST.format(tag=tag), method="HEAD", headers={"Accept": MCR_ACCEPT})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            digest = response.headers.get("Docker-Content-Digest", "")
    except urllib.error.HTTPError as error:
        if error.code == 404:
            return None
        raise
    if not DIGEST.fullmatch(digest):
        raise ValueError(f"MCR returned no digest for dotnet/sdk:{tag}")
    return digest


def image_tags(text: str) -> list[str]:
    lines = [line.rstrip("\r\n") for line in text.splitlines()]
    return [comment.group(1) for comment, image in zip(map(SDK_IMAGE_COMMENT.match, lines), lines[1:])
            if comment and SDK_IMAGE_FROM.match(image)]


def rewrite_image(text: str, sdk: str, runtime: str, digest: str) -> str:
    """Point every pinned SDK image in a Dockerfile at `sdk`, keeping its comment's OS and the line endings."""
    lines = text.splitlines(keepends=True)
    for index in range(len(lines) - 1):
        comment = SDK_IMAGE_COMMENT.match(lines[index].rstrip("\r\n"))
        if comment and SDK_IMAGE_FROM.match(lines[index + 1].rstrip("\r\n")):
            endings = [line[len(line.rstrip("\r\n")):] for line in lines[index:index + 2]]
            lines[index] = f"# mcr.microsoft.com/dotnet/sdk:{sdk} (runtime {runtime}, {comment.group(3)})." + endings[0]
            lines[index + 1] = f"FROM mcr.microsoft.com/dotnet/sdk@{digest}" + endings[1]
    return "".join(lines)


def branch_for(prefix: str, target: str, base: str) -> str:
    branch = f"{prefix}/{target}"
    if base != "main":
        branch += "-for-" + re.sub(r"[^A-Za-z0-9._-]+", "-", base)
    return branch


def sdk_title_and_branch(new_sdk: str, new_runtime: str, base: str) -> tuple[str, str]:
    return (f"Update the .NET SDK to {new_sdk} (runtime {new_runtime})",
            branch_for("update-dotnet-sdk", new_sdk, base))


def workloads_title_and_branch(new_set: str, new_runtime: str, base: str) -> tuple[str, str]:
    return (f"Update the .NET workload set to {new_set} (Mono runtime {new_runtime})",
            branch_for("update-dotnet-workloads", new_set, base))


def image_title_and_branch(sdk: str, base: str) -> tuple[str, str]:
    return f"Update the pinned .NET SDK image to {sdk}", branch_for("update-dotnet-sdk-image", sdk, base)


CHECKS_NOTE = ("CI, Security and Malware scan were started on this branch by the workflow, because a pull "
               "request opened with the workflow's own token starts no workflows. This PR remains open for "
               "review of every exact-head check; no merge is armed.")


def sdk_body(old_sdk: str, old_runtime: str, move: dict[str, Any], moves: dict[str, tuple[str, str, str]],
             channel: str) -> str:
    notes = RELEASE_NOTES.format(channel=channel, release=move["release"])
    reason = ("a security release, so the seven-day cooldown does not apply" if move["security"]
              else "at least seven days old")
    lines = [
        "The weekly .NET SDK update, proposed by `update-dotnet-sdk.yml`.",
        "",
        f"- SDK {old_sdk} -> {move['sdk']}, runtime {old_runtime} -> {move['runtime']}.",
        f"- [.NET {move['release']}]({notes}), released {move['release_date']}: {reason}.",
    ]
    if move["cves"]:
        lines.append(f"- CVEs listed for the release: {', '.join(move['cves'])}.")
    lines += [
        "",
        "Moved together: `global.json`, each workflow `dotnet-version`, each `RuntimeFrameworkVersion` "
        "pin and the lock files. The SDK archive matched the SHA-512 in Microsoft's `releases.json`.",
        "",
    ]
    if moves:
        lines += ["| Package | Old | New |", "|---|---|---|"]
        lines += [f"| {name} | {old} | {new} |" for name, (old, new, _) in sorted(moves.items())]
    else:
        lines.append("No package version moved in the lock files.")
    lines += [
        "",
        "The MAUI lock files take the same package moves; their workloads are not installed in the "
        "update job, so the MAUI jobs' locked restores check them. The MAUI workload set moves in its "
        "own pull request. Package versions in `Directory.Packages.props` are Dependabot's.",
        "",
        CHECKS_NOTE,
    ]
    return "\n".join(lines) + "\n"


def apple_platform_moves(old: dict[str, str], new: dict[str, str]) -> list[tuple[str, str, str]]:
    """Apple SDK packs whose platform version (the major.minor of the pack version) changes."""
    def platform(manifest: str | None) -> str:
        return ".".join(manifest.split("/")[0].split(".")[:2]) if manifest else "-"
    return [(name, platform(old.get(name)), platform(new.get(name)))
            for name in sorted(set(old) | set(new))
            if APPLE_MANIFEST.fullmatch(name) and platform(old.get(name)) != platform(new.get(name))]


def workloads_body(old_set: str, move: dict[str, Any]) -> str:
    reason = (f"it brings runtime {move['runtime']}, a security release, so the seven-day cooldown does not apply"
              if move["security"] else "at least seven days old")
    lines = [
        "The weekly .NET workload set update, proposed by `update-dotnet-sdk.yml`.",
        "",
        "**Needs owner decision.** Workload sets open as drafts labelled `needs owner decision`: a set "
        "decides the MAUI apps' Mono runtime and their Apple and Android SDK packs. Mark it ready for "
        "review once the owner has decided.",
        "",
        f"- Workload set {old_set} -> {move['set']}, published {move['published']}: {reason}.",
        f"- The MAUI apps' Mono runtime moves {move['old_runtime']} -> {move['runtime']}"
        if move["runtime"] != move["old_runtime"] else f"- The MAUI apps' Mono runtime stays {move['runtime']}.",
    ]
    apple = apple_platform_moves(move["old_manifests"], move["manifests"])
    if apple:
        lines.append("- Apple SDK packs change platform version, which usually needs the matching Xcode: "
                     + ", ".join(f"{name} {old} -> {new}" for name, old, new in apple) + ".")
    lines += [
        "",
        "The package matched the SHA-512 in nuget.org's catalog. Its manifests:",
        "",
        "| Manifest | Old | New |",
        "|---|---|---|",
    ]
    for name in sorted(set(move["old_manifests"]) | set(move["manifests"])):
        old, new = move["old_manifests"].get(name, "-"), move["manifests"].get(name, "-")
        lines.append(f"| {name} | {old} | {new} |" if old == new else f"| **{name}** | {old} | **{new}** |")
    lines += [
        "",
        "Moved: each `dotnet workload install ... --version`. A new Apple SDK pack can need a newer Xcode "
        "than the macOS jobs select, and Android packs a newer Android SDK; the MAUI jobs check both.",
        "",
        CHECKS_NOTE,
    ]
    return "\n".join(lines) + "\n"


def image_body(old_sdks: list[str], sdk: str, runtime: str, digest: str) -> str:
    return "\n".join([
        "The weekly .NET SDK image update, proposed by `update-dotnet-sdk.yml`.",
        "",
        f"- `mcr.microsoft.com/dotnet/sdk` {', '.join(old_sdks)} -> {sdk} (runtime {runtime}), the SDK `global.json` "
        "pins.",
        f"- Digest `{digest}`: MCR's manifest-list digest for the tag, which Docker checks on every pull.",
        "",
        "MCR publishes SDK images some hours after a release, so this follows the SDK move rather than "
        "joining it. Only the image comment and `FROM` lines change.",
        "",
        CHECKS_NOTE,
    ]) + "\n"


def write_proposal(out: Path, proposal: dict[str, Any], changed: dict[str, str], body: str) -> None:
    out.mkdir(parents=True, exist_ok=True)
    for path, text in changed.items():
        (out / "files" / path).parent.mkdir(parents=True, exist_ok=True)
        write(out / "files" / path, text)
    (out / "proposal.json").write_text(json.dumps({**proposal, "files": sorted(changed)}, indent=2) + "\n",
                                       encoding="utf-8")
    (out / "body.md").write_text(body, encoding="utf-8")


def prepare_workloads(root: Path, out: Path, base_sha: str, base_ref: str, sdk: str, releases: dict[str, Any],
                      fetch: FetchJson, get: FetchBytes, now: dt.datetime) -> tuple[bool, list[str]]:
    """Writes a workload set proposal from the base content; never touches the checkout."""
    current, files = workload_pins(root)
    if current is None:
        return False, ["No workload install is pinned."]
    move, notes = choose_workloads(current, sdk, releases, fetch, get, now)
    if move is None:
        notes.append(f"Workload set {current} is current for SDK {sdk}.")
        return False, notes
    changed = {path: rewrite_lines(read(root / path), WORKLOAD_INSTALL, 1, current, move["set"])[0] for path in files}
    title, branch = workloads_title_and_branch(move["set"], move["runtime"], base_ref)
    write_proposal(out, {"kind": "workloads", "base_sha": base_sha, "base_ref": base_ref, "title": title,
                         "branch": branch, "old_set": current, "new_set": move["set"], "new_runtime": move["runtime"]},
                   changed, workloads_body(current, move))
    notes.append(f"Proposed workload set {current} -> {move['set']} (Mono runtime {move['old_runtime']} -> "
                 f"{move['runtime']}).")
    return True, notes


def prepare_image(root: Path, out: Path, base_sha: str, base_ref: str, releases: dict[str, Any],
                  lookup: Lookup) -> tuple[bool, list[str]]:
    """Writes an SDK image proposal from the base content when a pinned image lags global.json."""
    sdk = json.loads(read(root / "global.json").lstrip("﻿"))["sdk"]["version"]
    notes, changed, old_sdks, digest, runtime = [], {}, set(), None, None
    for path in tracked(root, "*Dockerfile*"):
        text = read(root / path)
        stale = {tag for tag in image_tags(text) if tag != sdk}
        if not stale:
            continue
        digest = digest or lookup(sdk)
        if digest is None:
            notes.append(f"MCR has no dotnet/sdk:{sdk} image yet; {path} keeps sdk:{', '.join(sorted(stale))}.")
            continue
        runtime = runtime or runtime_of(releases, sdk)
        changed[path] = rewrite_image(text, sdk, runtime, digest)
        old_sdks |= stale
    if not changed:
        return False, notes
    title, branch = image_title_and_branch(sdk, base_ref)
    write_proposal(out, {"kind": "image", "base_sha": base_sha, "base_ref": base_ref, "title": title,
                         "branch": branch, "new_sdk": sdk, "new_runtime": runtime, "digest": digest},
                   changed, image_body(sorted(old_sdks), sdk, runtime, digest))
    notes.append(f"Proposed the SDK image dotnet/sdk:{sdk} ({digest}) for {', '.join(sorted(changed))}.")
    return True, notes


def prepare_sdk(root: Path, out: Path, sdk_dir: Path, base_sha: str, base_ref: str, releases: dict[str, Any],
                now: dt.datetime, archive: Path | None, rid: str | None) -> tuple[bool, list[str]]:
    """Moves the SDK pins in the checkout, restores, and writes the SDK proposal."""
    old_sdk = json.loads(read(root / "global.json").lstrip("﻿"))["sdk"]["version"]
    major, minor, _ = version(old_sdk)
    move, notes = choose(releases, old_sdk, now)
    if move is None:
        notes.append(f"SDK {old_sdk} is current for its feature band.")
        return False, notes
    old_runtime = runtime_of(releases, old_sdk)
    entry = archive_entry(move["files"], move["sdk"], rid or platform_rid())
    out.mkdir(parents=True, exist_ok=True)
    if archive is None:
        archive = out.parent.parent / entry["name"]
        digest = download(entry["url"], archive)
    else:
        digest = sha512_of(archive)
    if digest != entry["sha512"]:
        raise ValueError(f"{entry['name']} does not match the SHA-512 in releases.json")
    extract(archive, sdk_dir)

    changed = {}
    path = root / "global.json"
    write(path, rewrite_global_json(read(path), old_sdk, move["sdk"]))
    changed["global.json"] = read(path)
    for name in tracked(root, ".github/workflows/*.yml"):
        text, count = rewrite_lines(read(root / name), DOTNET_VERSION_LINE, 2, old_sdk, move["sdk"])
        if count:
            write(root / name, text)
            changed[name] = text
    for name in tracked(root, "*.csproj", "*.csproj.template"):
        text, count = rewrite_lines(read(root / name), RUNTIME_PIN, 1, old_runtime, move["runtime"])
        if count:
            write(root / name, text)
            changed[name] = text
    dotnet = sdk_dir / ("dotnet.exe" if os.name == "nt" else "dotnet")
    reported = subprocess.run([str(dotnet), "--version"], cwd=root, env=dotnet_env(sdk_dir),
                              capture_output=True, text=True, check=True, timeout=120).stdout.strip()
    if reported != move["sdk"]:
        raise ValueError(f"the downloaded SDK reports {reported}, not {move['sdk']}")
    lock_changes = restore_locks(root, sdk_dir, out.parent.parent / "restore-logs")
    for name, (old_text, new_text) in lock_changes.items():
        check_lock_change(name, old_text, new_text, old_runtime, move["runtime"])
        changed[name] = new_text
    moves = package_moves(lock_changes)
    for name in sorted(MAUI_LOCKS):
        old_text = read(root / name)
        new_text = mirror(old_text, moves)
        if new_text != old_text:
            check_lock_change(name, old_text, new_text, old_runtime, move["runtime"])
            write(root / name, new_text)
            changed[name] = new_text

    title, branch = sdk_title_and_branch(move["sdk"], move["runtime"], base_ref)
    write_proposal(out, {"kind": "sdk", "base_sha": base_sha, "base_ref": base_ref, "title": title,
                               "branch": branch, "old_sdk": old_sdk, "new_sdk": move["sdk"],
                               "old_runtime": old_runtime, "new_runtime": move["runtime"]},
                   changed, sdk_body(old_sdk, old_runtime, move, moves, f"{major}.{minor}"))
    notes.append(f"Proposed SDK {old_sdk} -> {move['sdk']} (runtime {old_runtime} -> {move['runtime']}), "
                 f"{len(changed)} files.")
    return True, notes


def prepare(root: Path, out: Path, sdk_dir: Path, base_sha: str, base_ref: str, fetch: FetchJson,
            get: FetchBytes, now: dt.datetime, archive: Path | None = None, rid: str | None = None,
            lookup: Lookup = mcr_digest) -> tuple[bool, list[str]]:
    """Writes out/sdk, out/workloads and out/image proposals where a move is due."""
    sdk = json.loads(read(root / "global.json").lstrip("﻿"))["sdk"]["version"]
    major, minor, _ = version(sdk)
    releases = fetch(RELEASES_URL.format(channel=f"{major}.{minor}"))
    # These read the base content, so they run before the SDK move edits the checkout.
    workloads, notes = prepare_workloads(root, out / "workloads", base_sha, base_ref, sdk, releases, fetch, get, now)
    image, image_notes = prepare_image(root, out / "image", base_sha, base_ref, releases, lookup)
    moved, sdk_notes = prepare_sdk(root, out / "sdk", sdk_dir, base_sha, base_ref, releases, now, archive, rid)
    return workloads or image or moved, sdk_notes + notes + image_notes


def check_sdk_proposal(root: Path, proposal: dict[str, Any], files: dict[str, tuple[str, str]]) -> None:
    old_sdk, new_sdk = proposal["old_sdk"], proposal["new_sdk"]
    old_runtime, new_runtime = proposal["old_runtime"], proposal["new_runtime"]
    if json.loads(read(root / "global.json").lstrip("﻿"))["sdk"]["version"] != old_sdk:
        raise ValueError("the proposal starts from a different SDK than global.json pins")
    if feature_band(new_sdk) != feature_band(old_sdk) or version(new_sdk) <= version(old_sdk):
        raise ValueError("the proposal does not move the SDK forward within its feature band")
    if version(new_runtime) <= version(old_runtime) or version(new_runtime)[:2] != version(old_runtime)[:2]:
        raise ValueError("the proposal does not move the runtime forward within its channel")
    if (proposal["title"], proposal["branch"]) != sdk_title_and_branch(new_sdk, new_runtime, proposal["base_ref"]):
        raise ValueError("the proposal's title or branch does not match its move")
    workflows = set(tracked(root, ".github/workflows/*.yml"))
    projects = set(tracked(root, "*.csproj", "*.csproj.template"))
    locks = set(lock_files(root))
    for path, (old_text, new_text) in files.items():
        if path == "global.json":
            if rewrite_global_json(old_text, old_sdk, new_sdk) != new_text:
                raise ValueError("global.json changes beyond the SDK version")
        elif path in workflows:
            if rewrite_lines(old_text, DOTNET_VERSION_LINE, 2, old_sdk, new_sdk)[0] != new_text:
                raise ValueError(f"{path} changes beyond its dotnet-version pins")
        elif path in projects:
            if rewrite_lines(old_text, RUNTIME_PIN, 1, old_runtime, new_runtime)[0] != new_text:
                raise ValueError(f"{path} changes beyond its RuntimeFrameworkVersion pin")
        elif path in locks:
            check_lock_change(path, old_text, new_text, old_runtime, new_runtime)
        else:
            raise ValueError(f"an SDK move does not change {path}")
    # A pin left behind would fail CI, but say so here rather than there.
    expected = {"global.json"}
    expected |= {p for p in workflows if rewrite_lines(read(root / p), DOTNET_VERSION_LINE, 2, old_sdk, new_sdk)[1]}
    expected |= {p for p in projects if rewrite_lines(read(root / p), RUNTIME_PIN, 1, old_runtime, new_runtime)[1]}
    if missing := sorted(expected - set(files)):
        raise ValueError(f"the proposal leaves pins unmoved in {missing}")


def check_workloads_proposal(root: Path, proposal: dict[str, Any], files: dict[str, tuple[str, str]]) -> None:
    old_set, new_set = proposal["old_set"], proposal["new_set"]
    current, pinned = workload_pins(root)
    if current != old_set:
        raise ValueError("the proposal starts from a different workload set than the workflows pin")
    if set_key(new_set) <= set_key(old_set) or set_key(new_set)[:2] != set_key(old_set)[:2] \
            or set_key(new_set)[2] // 100 != set_key(old_set)[2] // 100:
        raise ValueError("the proposal does not move the workload set forward within its feature band")
    version(proposal["new_runtime"])
    if (proposal["title"], proposal["branch"]) != workloads_title_and_branch(new_set, proposal["new_runtime"],
                                                                             proposal["base_ref"]):
        raise ValueError("the proposal's title or branch does not match its move")
    for path, (old_text, new_text) in files.items():
        if path not in pinned:
            raise ValueError(f"a workload set move does not change {path}")
        if rewrite_lines(old_text, WORKLOAD_INSTALL, 1, old_set, new_set)[0] != new_text:
            raise ValueError(f"{path} changes beyond its workload set pins")
    if missing := sorted(set(pinned) - set(files)):
        raise ValueError(f"the proposal leaves workload pins unmoved in {missing}")


def check_image_proposal(root: Path, proposal: dict[str, Any], files: dict[str, tuple[str, str]]) -> None:
    sdk, runtime, digest = proposal["new_sdk"], proposal["new_runtime"], proposal["digest"]
    if json.loads(read(root / "global.json").lstrip("﻿"))["sdk"]["version"] != sdk:
        raise ValueError("the proposal's SDK image is not the SDK global.json pins")
    version(runtime)
    if not DIGEST.fullmatch(digest):
        raise ValueError("the proposal's image digest is malformed")
    if (proposal["title"], proposal["branch"]) != image_title_and_branch(sdk, proposal["base_ref"]):
        raise ValueError("the proposal's title or branch does not match its move")
    dockerfiles = set(tracked(root, "*Dockerfile*"))
    for path, (old_text, new_text) in files.items():
        if path not in dockerfiles or not image_tags(old_text):
            raise ValueError(f"an SDK image move does not change {path}")
        if rewrite_image(old_text, sdk, runtime, digest) != new_text:
            raise ValueError(f"{path} changes beyond its SDK image pin")


def check_proposal(root: Path, inp: Path) -> tuple[dict[str, Any], dict[str, bytes]]:
    """The proposal in `inp` and its files, after checking every change against its rules."""
    proposal = json.loads((inp / "proposal.json").read_text(encoding="utf-8"))
    tracked_files = set(tracked(root))
    files: dict[str, tuple[str, str]] = {}
    for path in proposal["files"]:
        if path not in tracked_files:
            raise ValueError(f"{path} is not a tracked file")
        source = inp / "files" / path
        if not source.resolve().is_relative_to((inp / "files").resolve()):
            raise ValueError(f"{path} lies outside the proposal")
        old_text, new_text = read(root / path), read(source)
        if old_text == new_text:
            raise ValueError(f"{path} is listed but unchanged")
        files[path] = (old_text, new_text)
    if proposal.get("kind") == "sdk":
        check_sdk_proposal(root, proposal, files)
    elif proposal.get("kind") == "workloads":
        check_workloads_proposal(root, proposal, files)
    elif proposal.get("kind") == "image":
        check_image_proposal(root, proposal, files)
    else:
        raise ValueError(f"unknown proposal kind {proposal.get('kind')!r}")
    return proposal, {path: (inp / "files" / path).read_bytes() for path in files}


def mode_of(root: Path, path: str) -> str:
    entry = subprocess.run(["git", "-C", str(root), "ls-files", "-s", "--", path],
                           capture_output=True, text=True, check=True, timeout=120).stdout.split()
    if not entry or entry[0] not in ("100644", "100755"):
        raise ValueError(f"{path} is not a tracked file")
    return entry[0]


def propose_one(root: Path, inp: Path, repository: str, base_sha: str, base_ref: str, api: Api,
                review_merge: Callable[[str], None]) -> str | None:
    proposal, files = check_proposal(root, inp)
    if proposal["base_sha"] != base_sha or not re.fullmatch(r"[0-9a-f]{40}", base_sha):
        raise ValueError("the proposal was prepared from a different commit")
    if proposal["base_ref"] != base_ref:
        raise ValueError("the proposal was prepared for a different branch")
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("unexpected repository name")
    title, branch = proposal["title"], proposal["branch"]
    base = f"repos/{repository}"
    owner = repository.split("/")[0]
    pulls = api(f"{base}/pulls?state=all&head={owner}:{branch}&per_page=10")
    if pulls:
        pull = pulls[0]
        if pull["state"] != "open":
            print(f"{pull['html_url']} was closed; leaving that decision alone.")
            return None
        print(f"{pull['html_url']} already proposes this update.")
        return pull["html_url"]
    refs = api(f"{base}/git/matching-refs/heads/{branch}")
    if any(ref["ref"] == f"refs/heads/{branch}" for ref in refs):
        raise ValueError(f"branch {branch} exists without a pull request; look at it first")
    tree_entries = []
    for path, content in sorted(files.items()):
        blob = api(f"{base}/git/blobs", "POST", {"content": base64.b64encode(content).decode("ascii"),
                                                  "encoding": "base64"})
        tree_entries.append({"path": path, "mode": mode_of(root, path), "type": "blob", "sha": blob["sha"]})
    commit = api(f"{base}/git/commits/{base_sha}")
    tree = api(f"{base}/git/trees", "POST", {"base_tree": commit["tree"]["sha"], "tree": tree_entries})
    created = api(f"{base}/git/commits", "POST", {"message": title, "tree": tree["sha"], "parents": [base_sha]})
    api(f"{base}/git/refs", "POST", {"ref": f"refs/heads/{branch}", "sha": created["sha"]})
    # check_proposal lets workload pins change only under the workloads rules, so the kind decides.
    owner_decision = proposal["kind"] in OWNER_DECISION_KINDS
    pull = api(f"{base}/pulls", "POST", {"title": title, "head": branch, "base": base_ref,
                                         "body": (inp / "body.md").read_text(encoding="utf-8"),
                                         "draft": owner_decision})
    # The gates first: nothing after this may leave the pull request without its checks.
    for workflow in GATES:
        api(f"{base}/actions/workflows/{workflow}/dispatches", "POST", {"ref": branch})
    labels = [*LABELS, OWNER_DECISION_LABEL] if owner_decision else list(LABELS)
    api(f"{base}/issues/{pull['number']}/labels", "POST", {"labels": labels})
    api(f"{base}/issues/{pull['number']}/assignees", "POST", {"assignees": [owner]})
    review_merge(pull["html_url"])
    return pull["html_url"]


def propose(root: Path, inp: Path, repository: str, base_sha: str, base_ref: str, api: Api = gh_api,
            review_merge: Callable[[str], None] = defer_merge) -> list[str]:
    """Opens a pull request for each proposal in `inp`; returns their URLs."""
    urls = []
    for kind in KINDS:
        if (inp / kind / "proposal.json").exists():
            url = propose_one(root, inp / kind, repository, base_sha, base_ref, api, review_merge)
            if url:
                urls.append(url)
    return urls


def report(lines: list[str]) -> None:
    print("\n".join(lines))
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
            stream.write("\n".join(f"- {line}" for line in lines) + "\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("prepare", "propose"))
    parser.add_argument("--out", type=Path)
    parser.add_argument("--in", dest="inp", type=Path)
    parser.add_argument("--sdk-dir", type=Path)
    parser.add_argument("--archive", type=Path, help="a local SDK archive, still checked against releases.json")
    args = parser.parse_args(argv)
    root = Path.cwd()
    if not os.environ.get("GITHUB_REF", "").startswith("refs/heads/"):
        raise ValueError("run this workflow on a branch")
    base_ref = os.environ["GITHUB_REF_NAME"]
    if args.mode == "prepare":
        changed, notes = prepare(root, args.out, args.sdk_dir, os.environ["GITHUB_SHA"], base_ref, fetch_json,
                                 fetch_bytes, dt.datetime.now(dt.timezone.utc), args.archive)
        report(notes)
        if os.environ.get("GITHUB_OUTPUT"):
            with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
                stream.write(f"changed={'true' if changed else 'false'}\n")
    else:
        urls = propose(root, args.inp, os.environ["GITHUB_REPOSITORY"], os.environ["GITHUB_SHA"], base_ref)
        report([f"Proposed: {url}" for url in urls] or ["Nothing new to propose."])
    return 0


if __name__ == "__main__":
    sys.exit(main())
