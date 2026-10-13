"""The SDK updater's rules, file checks and GitHub calls, without network or a real SDK."""
import base64
import datetime as dt
import hashlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import Mock

sys.path.insert(0, str(Path(__file__).resolve().parent))
import update_dotnet_sdk as sdk  # noqa: E402

NOW = dt.datetime(2026, 10, 20, 12, tzinfo=dt.timezone.utc)
HASH = "a" * 128
WORKLOADS_ID = "microsoft.net.workloads.10.0.400"


def release(version, date, sdks, security=False):
    return {"release-version": version, "release-date": date, "security": security,
            "cve-list": [{"cve-id": "CVE-2026-1"}] if security else [],
            "sdks": [{"version": s, "runtime-version": version, "files": files(s)} for s in sdks]}


def files(version):
    base = f"https://builds.dotnet.microsoft.com/dotnet/Sdk/{version}/dotnet-sdk-{version}"
    return [{"rid": "linux-x64", "name": "dotnet-sdk-linux-x64.tar.gz", "url": base + "-linux-x64.tar.gz", "hash": HASH},
            {"rid": "win-x64", "name": "dotnet-sdk-win-x64.zip", "url": base + "-win-x64.zip", "hash": HASH}]


RELEASES = {"releases": [
    release("10.0.14", "2026-10-16", ["10.0.403", "10.0.114"]),
    release("10.0.13", "2026-10-13", ["10.0.402", "10.0.113"], security=True),
    release("10.0.12", "2026-09-09", ["10.0.401", "10.0.112"]),
]}

LOCK = {"version": 2, "dependencies": {"net10.0": {
    "Microsoft.NET.ILLink.Tasks": {"type": "Direct", "requested": "[10.0.12, )", "resolved": "10.0.12",
                                   "contentHash": "old+hash/=="},
    "EmbedIO-Neo": {"type": "Project", "dependencies": {"Microsoft.NET.ILLink.Tasks": "[10.0.12, )"}},
    "Other": {"type": "Transitive", "resolved": "10.0.1", "contentHash": "other=="},
}}}


def render(lock, newline="\n"):
    return (json.dumps(lock, indent=2, ensure_ascii=False)).replace("\n", newline)


def moved_lock(version="10.0.13", content_hash="new+hash/=="):
    lock = json.loads(json.dumps(LOCK))
    entry = lock["dependencies"]["net10.0"]["Microsoft.NET.ILLink.Tasks"]
    entry.update(requested=f"[{version}, )", resolved=version, contentHash=content_hash)
    lock["dependencies"]["net10.0"]["EmbedIO-Neo"]["dependencies"]["Microsoft.NET.ILLink.Tasks"] = f"[{version}, )"
    return lock


def workload_package(toolchain, ios="26.5.10318"):
    manifests = {sdk.MONO_TOOLCHAIN: f"{toolchain}/10.0.100", "Microsoft.NET.Sdk.iOS": f"{ios}/10.0.100"}
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as package:
        package.writestr(sdk.WORKLOAD_SET_FILE, json.dumps(manifests))
    return stream.getvalue()


class Feed:
    """nuget.org and releases.json, answered from memory."""

    def __init__(self, sets, releases=RELEASES):
        self.json, self.bytes = {sdk.RELEASES_URL.format(channel="10.0"): releases}, {}
        self.json[sdk.NUGET_INDEX.format(id=WORKLOADS_ID)] = {"versions": [sdk.package_from_set(s) for s in sets]}
        for workload, (toolchain, published, *options) in sets.items():
            version = sdk.package_from_set(workload)
            content = workload_package(toolchain, *options)
            catalog = f"https://api.nuget.org/v3/catalog0/{workload}.json"
            self.json[sdk.NUGET_REGISTRATION.format(id=WORKLOADS_ID, version=version)] = {
                "listed": True, "catalogEntry": catalog, "published": published}
            self.json[catalog] = {"packageHashAlgorithm": "SHA512",
                                  "packageHash": base64.b64encode(hashlib.sha512(content).digest()).decode()}
            self.bytes[sdk.NUGET_PACKAGE.format(id=WORKLOADS_ID, version=version)] = content

    def fetch(self, url):
        return json.loads(json.dumps(self.json[url]))

    def get(self, url):
        return self.bytes[url]


def git_repo(root, contents):
    for path, text in contents.items():
        (root / path).parent.mkdir(parents=True, exist_ok=True)
        (root / path).write_bytes(text.encode("utf-8"))
    subprocess.run(["git", "init", "-q", str(root)], check=True)
    subprocess.run(["git", "-C", str(root), "-c", "core.autocrlf=false", "add", "-A"], check=True)


class ChooseTests(unittest.TestCase):
    def test_takes_a_security_release_at_once_and_waits_out_the_cooldown_otherwise(self):
        move, notes = sdk.choose(RELEASES, "10.0.401", NOW)
        self.assertEqual((move["sdk"], move["runtime"], move["security"]), ("10.0.402", "10.0.13", True))
        self.assertTrue(any("10.0.403" in note and "cooldown" in note for note in notes))
        move, _ = sdk.choose(RELEASES, "10.0.401", NOW + dt.timedelta(days=4))
        self.assertEqual(move["sdk"], "10.0.403")

    def test_stays_in_the_pinned_feature_band(self):
        move, _ = sdk.choose(RELEASES, "10.0.112", NOW + dt.timedelta(days=30))
        self.assertEqual(move["sdk"], "10.0.114")

    def test_reports_a_newer_band_without_proposing_it(self):
        releases = {"releases": [release("10.0.15", "2026-11-10", ["10.0.500"])] + RELEASES["releases"]}
        move, notes = sdk.choose(releases, "10.0.403", NOW + dt.timedelta(days=60))
        self.assertIsNone(move)
        self.assertTrue(any("10.0.500" in note and "feature band" in note for note in notes))

    def test_never_moves_back_and_skips_previews(self):
        releases = {"releases": [release("10.0.13", "2026-10-13", ["10.0.402", "10.0.400-rc.1.1"], True)]}
        self.assertIsNone(sdk.choose(releases, "10.0.405", NOW)[0])
        self.assertIsNone(sdk.choose(releases, "10.0.402", NOW)[0])

    def test_finds_the_runtime_of_the_pinned_sdk(self):
        self.assertEqual(sdk.runtime_of(RELEASES, "10.0.401"), "10.0.12")
        with self.assertRaises(ValueError):
            sdk.runtime_of(RELEASES, "10.0.399")


class ArchiveTests(unittest.TestCase):
    def test_selects_the_platform_archive_with_its_recorded_hash(self):
        entry = sdk.archive_entry(files("10.0.402"), "10.0.402", "linux-x64")
        self.assertEqual(entry["name"], "dotnet-sdk-linux-x64.tar.gz")
        self.assertEqual(entry["sha512"], HASH)
        self.assertEqual(sdk.archive_entry(files("10.0.402"), "10.0.402", "win-x64")["name"], "dotnet-sdk-win-x64.zip")

    def test_rejects_another_host_or_a_missing_hash(self):
        moved = [dict(f, url=f["url"].replace("builds.dotnet.microsoft.com", "example.com")) for f in files("10.0.402")]
        with self.assertRaises(ValueError):
            sdk.archive_entry(moved, "10.0.402", "linux-x64")
        unhashed = [dict(f, hash="") for f in files("10.0.402")]
        with self.assertRaises(ValueError):
            sdk.archive_entry(unhashed, "10.0.402", "linux-x64")


class RewriteTests(unittest.TestCase):
    def test_global_json_moves_only_the_sdk_version(self):
        text = '{\n  "test": { "runner": "Microsoft.Testing.Platform" },\n  "sdk": {\n    "version": "10.0.401",\n    "rollForward": "disable"\n  }\n}\n'
        updated = sdk.rewrite_global_json(text, "10.0.401", "10.0.402")
        self.assertEqual(updated, text.replace("10.0.401", "10.0.402"))
        with self.assertRaises(ValueError):
            sdk.rewrite_global_json(text, "10.0.400", "10.0.402")

    def test_workflow_lines_move_only_the_matching_dotnet_version(self):
        text = ("      - uses: actions/setup-dotnet@x # v6.0.0\r\n        with:\r\n          dotnet-version: 10.0.401\r\n"
                "          other: 10.0.401\r\n          dotnet-version: 9.0.100\r\n")
        updated, count = sdk.rewrite_lines(text, sdk.DOTNET_VERSION_LINE, 2, "10.0.401", "10.0.402")
        self.assertEqual(count, 1)
        self.assertEqual(updated, text.replace("dotnet-version: 10.0.401", "dotnet-version: 10.0.402"))

    def test_runtime_pins_move_and_version_tokens_stay_whole(self):
        text = "<RuntimeFrameworkVersion>10.0.1</RuntimeFrameworkVersion>\n<Version>10.0.12</Version>\n"
        updated, count = sdk.rewrite_lines(text, sdk.RUNTIME_PIN, 1, "10.0.1", "10.0.2")
        self.assertEqual((count, updated), (1, text.replace(">10.0.1<", ">10.0.2<")))
        self.assertIsNone(sdk.swap("10.0.12", "10.0.1", "10.0.2"))
        self.assertEqual(sdk.swap("[10.0.1, )", "10.0.1", "10.0.2"), "[10.0.2, )")

    def test_workload_install_lines_move_only_their_version(self):
        text = "  run: dotnet workload install maui-android --version 10.0.401\n  run: echo --version 10.0.401\n"
        updated, count = sdk.rewrite_lines(text, sdk.WORKLOAD_INSTALL, 1, "10.0.401", "10.0.401.1")
        self.assertEqual(count, 1)
        self.assertEqual(updated, text.replace("--version 10.0.401\n  run", "--version 10.0.401.1\n  run", 1))


class LockTests(unittest.TestCase):
    def test_runtime_moves_pass_and_anything_else_fails(self):
        old = render(LOCK)
        sdk.check_lock_change("a", old, render(moved_lock()), "10.0.12", "10.0.13")
        for bad in (moved_lock("10.0.14"),
                    {**LOCK, "version": 3},
                    {"version": 2, "dependencies": {"net10.0": {**LOCK["dependencies"]["net10.0"], "Added": {"type": "Transitive", "resolved": "1.0.0", "contentHash": "x"}}}}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                sdk.check_lock_change("a", old, render(bad), "10.0.12", "10.0.13")
        rehashed = json.loads(old)
        rehashed["dependencies"]["net10.0"]["Other"]["contentHash"] = "changed=="
        with self.assertRaises(ValueError):
            sdk.check_lock_change("a", old, render(rehashed), "10.0.12", "10.0.13")

    def test_moves_from_regenerated_locks_mirror_into_unrestorable_ones(self):
        moves = sdk.package_moves({"a": (render(LOCK), render(moved_lock()))})
        self.assertEqual(moves, {"Microsoft.NET.ILLink.Tasks": ("10.0.12", "10.0.13", "new+hash/==")})
        for prefix, newline, ending in (("", "\n", ""), ("\ufeff", "\r\n", "\r\n")):
            with self.subTest(newline=newline):
                text = prefix + render(LOCK, newline) + ending
                self.assertEqual(sdk.mirror(text, moves), prefix + render(moved_lock(), newline) + ending)

    def test_inconsistent_moves_stop_the_update(self):
        other = moved_lock(content_hash="different==")
        with self.assertRaises(ValueError):
            sdk.package_moves({"a": (render(LOCK), render(moved_lock())), "b": (render(LOCK), render(other))})


class WorkloadTests(unittest.TestCase):
    def test_sets_and_packages_map_both_ways(self):
        self.assertEqual(sdk.set_from_package("10.401.1", 10, 0), "10.0.401.1")
        self.assertEqual(sdk.set_from_package("10.401.0", 10, 0), "10.0.401")
        self.assertIsNone(sdk.set_from_package("10.400.0-rc.1.1", 10, 0))
        self.assertIsNone(sdk.set_from_package("11.100.0", 10, 0))
        self.assertEqual(sdk.package_from_set("10.0.401"), "10.401.0")
        self.assertEqual(sdk.package_from_set("10.0.401.1"), "10.401.1")
        self.assertLess(sdk.set_key("10.0.401"), sdk.set_key("10.0.401.1"))

    def test_the_mono_toolchain_names_the_runtime(self):
        self.assertEqual(sdk.toolchain_runtime({sdk.MONO_TOOLCHAIN: "10.0.113/10.0.100"}), "10.0.13")
        with self.assertRaises(ValueError):
            sdk.toolchain_runtime({sdk.MONO_TOOLCHAIN: "10.0.213/10.0.100"})

    def test_a_security_runtime_is_taken_at_once_and_the_rest_waits(self):
        feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"),
                     "10.0.401.1": ("10.0.112", "2026-10-18T00:00:00Z"),
                     "10.0.402": ("10.0.113", "2026-10-19T00:00:00Z")})
        move, _ = sdk.choose_workloads("10.0.401", "10.0.402", RELEASES, feed.fetch, feed.get, NOW)
        self.assertEqual((move["set"], move["runtime"], move["old_runtime"], move["security"]),
                         ("10.0.402", "10.0.13", "10.0.12", True))
        move, notes = sdk.choose_workloads("10.0.401", "10.0.401", RELEASES, feed.fetch, feed.get, NOW)
        self.assertIsNone(move)
        self.assertTrue(any("10.0.402" in n and "waits for SDK" in n for n in notes))
        self.assertTrue(any("10.0.401.1" in n and "cooldown" in n for n in notes))
        move, _ = sdk.choose_workloads("10.0.401", "10.0.401", RELEASES, feed.fetch, feed.get, NOW + dt.timedelta(days=7))
        self.assertEqual((move["set"], move["security"]), ("10.0.401.1", False))

    def test_a_package_that_does_not_match_the_catalog_is_refused(self):
        feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"), "10.0.401.1": ("10.0.112", "2026-09-28T22:59:00Z")})
        feed.bytes[sdk.NUGET_PACKAGE.format(id=WORKLOADS_ID, version="10.401.1")] = workload_package("10.0.199")
        with self.assertRaises(ValueError):
            sdk.choose_workloads("10.0.401", "10.0.402", RELEASES, feed.fetch, feed.get, NOW)
        feed.json[sdk.NUGET_REGISTRATION.format(id=WORKLOADS_ID, version="10.401.1")]["listed"] = False
        with self.assertRaises(ValueError):
            sdk.choose_workloads("10.0.401", "10.0.402", RELEASES, feed.fetch, feed.get, NOW)

    def test_pins_must_name_one_set(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            git_repo(root, {".github/workflows/a.yml": "run: dotnet workload install maui-android --version 10.0.401\n",
                            "scripts/b.ps1": "dotnet workload install $env:W --version 10.0.401\n",
                            "scripts/c.ps1": "dotnet workload restore\n"})
            self.assertEqual(sdk.workload_pins(root), ("10.0.401", [".github/workflows/a.yml", "scripts/b.ps1"]))
            (root / "scripts/b.ps1").write_text("dotnet workload install $env:W --version 10.0.402\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                sdk.workload_pins(root)
        with tempfile.TemporaryDirectory() as temp:
            git_repo(Path(temp), {"scripts/c.ps1": "dotnet workload restore\n"})
            self.assertEqual(sdk.workload_pins(Path(temp)), (None, []))


DOCKERFILE = ("FROM golang@sha256:" + "1" * 64 + " AS h2spec\r\n"
              "# mcr.microsoft.com/dotnet/sdk:10.0.401 (runtime 10.0.12, Ubuntu 24.04).\r\n"
              "FROM mcr.microsoft.com/dotnet/sdk@sha256:" + "e" * 64 + "\r\n"
              "RUN apt-get update\r\n")
NEW_DIGEST = "sha256:" + "f" * 64


class ImageTests(unittest.TestCase):
    def test_reads_and_moves_the_pinned_sdk_image(self):
        self.assertEqual(sdk.image_tags(DOCKERFILE), ["10.0.401"])
        moved = sdk.rewrite_image(DOCKERFILE, "10.0.402", "10.0.13", NEW_DIGEST)
        self.assertEqual(moved, DOCKERFILE.replace("sdk:10.0.401 (runtime 10.0.12,", "sdk:10.0.402 (runtime 10.0.13,")
                         .replace("sha256:" + "e" * 64, NEW_DIGEST))

    def test_follows_global_json_once_mcr_has_the_tag(self):
        with tempfile.TemporaryDirectory() as temp:
            root, out = Path(temp) / "repo", Path(temp) / "image"
            git_repo(root, {"global.json": '{"sdk": {"version": "10.0.402"}}', "docker/Dockerfile": DOCKERFILE,
                            "other/Dockerfile": "FROM alpine@sha256:" + "2" * 64 + "\n"})
            changed, notes = sdk.prepare_image(root, out, "1" * 40, "main", RELEASES, lambda tag: None)
            self.assertFalse(changed)
            self.assertTrue(any("no dotnet/sdk:10.0.402" in note for note in notes))
            changed, _ = sdk.prepare_image(root, out, "1" * 40, "main", RELEASES, lambda tag: NEW_DIGEST)
            self.assertTrue(changed)
            proposal, files_ = sdk.check_proposal(root, out)
            self.assertEqual((proposal["new_sdk"], proposal["digest"], list(files_)), ("10.0.402", NEW_DIGEST, ["docker/Dockerfile"]))
            (out / "files/docker/Dockerfile").write_bytes(sdk.rewrite_image(DOCKERFILE, "10.0.402", "10.0.13", NEW_DIGEST)
                                                          .replace("apt-get update", "curl evil").encode())
            with self.assertRaises(ValueError):
                sdk.check_proposal(root, out)
            (root / "global.json").write_text('{"sdk": {"version": "10.0.403"}}', encoding="utf-8")
            with self.assertRaises(ValueError):
                sdk.check_proposal(root, out)


class ProposalTests(unittest.TestCase):
    BASE = {
        "global.json": '{\n  "sdk": {\n    "version": "10.0.401",\n    "rollForward": "disable"\n  }\n}\n',
        ".github/workflows/ci.yml": "jobs:\n  a:\n    steps:\n      - with:\n          dotnet-version: 10.0.401\n",
        ".github/workflows/android.yml": "      - run: dotnet workload install maui-android --version 10.0.401\n",
        "test/Probe/Probe.csproj": "<Project>\n  <RuntimeFrameworkVersion>10.0.12</RuntimeFrameworkVersion>\n</Project>\n",
        "src/Lib/packages.lock.json": render(LOCK),
        "README.md": "SDK 10.0.401\n",
    }

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / "repo"
        self.out = Path(self.temp.name) / "proposal"
        git_repo(self.root, self.BASE)
        self.sdk_files = {
            "global.json": self.BASE["global.json"].replace("10.0.401", "10.0.402"),
            ".github/workflows/ci.yml": self.BASE[".github/workflows/ci.yml"].replace("10.0.401", "10.0.402"),
            "test/Probe/Probe.csproj": self.BASE["test/Probe/Probe.csproj"].replace("10.0.12", "10.0.13"),
            "src/Lib/packages.lock.json": render(moved_lock()),
        }
        self.write_sdk(self.sdk_files)

    def tearDown(self):
        self.temp.cleanup()

    def write_sdk(self, changed):
        title, branch = sdk.sdk_title_and_branch("10.0.402", "10.0.13", "main")
        sdk.write_proposal(self.out / "sdk", {"kind": "sdk", "base_sha": "1" * 40, "base_ref": "main", "title": title,
                                              "branch": branch, "old_sdk": "10.0.401", "new_sdk": "10.0.402",
                                              "old_runtime": "10.0.12", "new_runtime": "10.0.13"}, changed, "body\n")

    def write_workloads(self, changed, new_set="10.0.401.1"):
        title, branch = sdk.workloads_title_and_branch(new_set, "10.0.12", "main")
        sdk.write_proposal(self.out / "workloads", {"kind": "workloads", "base_sha": "1" * 40, "base_ref": "main",
                                                    "title": title, "branch": branch, "old_set": "10.0.401",
                                                    "new_set": new_set, "new_runtime": "10.0.12"}, changed, "body\n")

    def test_a_complete_sdk_move_passes(self):
        proposal, changed = sdk.check_proposal(self.root, self.out / "sdk")
        self.assertEqual(proposal["new_sdk"], "10.0.402")
        self.assertEqual(sorted(changed), sorted(self.sdk_files))

    def test_other_files_and_other_edits_are_refused(self):
        cases = {
            "README.md": "SDK 10.0.402\n",
            ".github/workflows/ci.yml": "jobs:\n  a:\n    steps:\n      - run: curl evil\n          dotnet-version: 10.0.402\n",
            "test/Probe/Probe.csproj": "<Project>\n  <RuntimeFrameworkVersion>10.0.13</RuntimeFrameworkVersion>\n  <Evil />\n</Project>\n",
            ".github/workflows/android.yml": "      - run: dotnet workload install maui-android --version 10.0.402\n",
        }
        for path, text in cases.items():
            with self.subTest(path=path):
                self.write_sdk({**self.sdk_files, path: text})
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out / "sdk")
                (self.out / "sdk" / "files" / path).unlink()

    def test_a_pin_left_behind_is_refused(self):
        changed = dict(self.sdk_files)
        del changed["test/Probe/Probe.csproj"]
        (self.out / "sdk" / "files" / "test/Probe/Probe.csproj").unlink()
        self.write_sdk(changed)
        with self.assertRaises(ValueError):
            sdk.check_proposal(self.root, self.out / "sdk")

    def test_workload_moves_change_only_the_install_versions(self):
        moved = self.BASE[".github/workflows/android.yml"].replace("10.0.401", "10.0.401.1")
        self.write_workloads({".github/workflows/android.yml": moved})
        proposal, changed = sdk.check_proposal(self.root, self.out / "workloads")
        self.assertEqual((proposal["new_set"], list(changed)), ("10.0.401.1", [".github/workflows/android.yml"]))
        for bad in ({".github/workflows/android.yml": moved.replace("maui-android", "maui-ios")},
                    {".github/workflows/android.yml": moved, "global.json": self.sdk_files["global.json"]}):
            with self.subTest(bad=sorted(bad)):
                self.write_workloads(bad)
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out / "workloads")
        with self.assertRaises(ValueError):
            sdk.check_workloads_proposal(self.root, {"old_set": "10.0.401", "new_set": "10.0.400", "base_ref": "main",
                                                     "new_runtime": "10.0.12", "title": "", "branch": ""}, {})

    def test_propose_commits_each_move_through_the_api_and_starts_the_gates(self):
        self.write_workloads({".github/workflows/android.yml":
                              self.BASE[".github/workflows/android.yml"].replace("10.0.401", "10.0.401.1")})
        api, review = self.api(pulls=[]), Mock()
        urls = sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", api, review)
        self.assertEqual(urls, ["https://github.com/owner/repo/pull/7", "https://github.com/owner/repo/pull/7"])
        calls = [c.args[0] for c in api.call_args_list]
        self.assertEqual(sum(1 for endpoint in calls if endpoint.endswith("/git/blobs")), len(self.sdk_files) + 1)
        dispatched = [endpoint for endpoint in calls if endpoint.endswith("/dispatches")]
        self.assertEqual(dispatched, [f"repos/owner/repo/actions/workflows/{w}/dispatches" for w in sdk.GATES] * 2)
        heads = [c.args[2]["head"] for c in api.call_args_list if c.args[0] == "repos/owner/repo/pulls"]
        self.assertEqual(heads, ["update-dotnet-sdk/10.0.402", "update-dotnet-workloads/10.0.401.1"])
        self.assertEqual(review.call_count, 2)

    def test_propose_leaves_existing_pull_requests_alone(self):
        open_api = self.api(pulls=[{"state": "open", "html_url": "https://github.com/owner/repo/pull/3"}])
        self.assertEqual(sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", open_api, Mock()),
                         ["https://github.com/owner/repo/pull/3"])
        closed_api = self.api(pulls=[{"state": "closed", "html_url": "https://github.com/owner/repo/pull/3"}])
        self.assertEqual(sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", closed_api, Mock()), [])
        for api in (open_api, closed_api):
            self.assertFalse(any(c.args[0].endswith("/git/blobs") for c in api.call_args_list))

    def test_propose_refuses_another_commit_or_branch(self):
        with self.assertRaises(ValueError):
            sdk.propose(self.root, self.out, "owner/repo", "2" * 40, "main", self.api(pulls=[]), Mock())
        with self.assertRaises(ValueError):
            sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "release", self.api(pulls=[]), Mock())

    def api(self, pulls):
        def respond(endpoint, method="GET", payload=None):
            if "/pulls?" in endpoint:
                return pulls
            if "/git/matching-refs/" in endpoint:
                return []
            if endpoint.endswith("/git/blobs"):
                return {"sha": hashlib.sha1(payload["content"].encode()).hexdigest()}
            if "/git/commits/" in endpoint:
                return {"tree": {"sha": "t" * 40}}
            if endpoint.endswith("/git/trees"):
                return {"sha": "u" * 40}
            if endpoint.endswith("/git/commits"):
                return {"sha": "c" * 40}
            if endpoint.endswith("/pulls"):
                return {"number": 7, "html_url": "https://github.com/owner/repo/pull/7"}
            return None
        return Mock(side_effect=respond)


class PrepareTests(unittest.TestCase):
    def test_current_pins_propose_nothing_and_a_bad_archive_stops_before_use(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            git_repo(root, {"global.json": '{"sdk": {"version": "10.0.402"}}',
                            ".github/workflows/a.yml": "run: dotnet workload install maui-ios --version 10.0.402\n"})
            feed = Feed({"10.0.402": ("10.0.113", "2026-10-14T00:00:00Z")})
            changed, notes = sdk.prepare(root, root / "out", root / "sdk", "1" * 40, "main", feed.fetch, feed.get, NOW)
            self.assertFalse(changed)
            self.assertTrue(any("current" in note for note in notes))
            (root / "global.json").write_text('{"sdk": {"version": "10.0.401"}}', encoding="utf-8")
            archive = root / "dotnet-sdk.tar.gz"
            archive.write_bytes(b"not the SDK")
            with self.assertRaises(ValueError):
                sdk.prepare(root, root / "out", root / "sdk", "1" * 40, "main", feed.fetch, feed.get, NOW, archive, "linux-x64")
            self.assertFalse((root / "sdk").exists())


if __name__ == "__main__":
    unittest.main()
