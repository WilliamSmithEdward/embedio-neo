"""The SDK updater's rules, file checks and GitHub calls, without network or a real SDK."""
import base64
import datetime as dt
import hashlib
import importlib.util
import io
import json
import plistlib
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import Mock, patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
import maui_https_probe  # noqa: E402
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


def workload_package(toolchain, ios="26.5.10318", maccatalyst=None):
    manifests = {sdk.MONO_TOOLCHAIN: f"{toolchain}/10.0.100", "Microsoft.NET.Sdk.iOS": f"{ios}/10.0.100",
                 "Microsoft.NET.Sdk.MacCatalyst": f"{maccatalyst or ios}/10.0.100"}
    stream = io.BytesIO()
    with zipfile.ZipFile(stream, "w") as package:
        package.writestr(sdk.WORKLOAD_SET_FILE, json.dumps(manifests))
    return stream.getvalue()


def image_readme(*versions):
    """A runner image README's Xcode table, followed by a table of the same shape's neighbours."""
    rows = [f"| {version} | 17F113 | /Applications/Xcode_{version.split()[0]}.app   | /Applications/Xcode.app |"
            for version in versions]
    return "\n".join(["### Xcode", "| Version | Build | Path | Symlinks |", "| ------- | ----- | ---- | -------- |", *rows, "",
                      "#### Installed SDKs", "| SDK | SDK Name | Xcode Version |", "| --- | -------- | ------------- |",
                      "| iOS 99.9 | iphoneos99.9 | 99.9 |"]).encode("utf-8")


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

    def test_a_range_for_a_referenced_project_may_follow_its_version(self):
        lock = json.loads(json.dumps(LOCK))
        packages = lock["dependencies"]["net10.0"]
        packages["EmbedIO-Neo.Quic.Native"] = {"type": "Project"}
        packages["EmbedIO-Neo"]["dependencies"]["EmbedIO-Neo.Quic.Native"] = "[2.16.0, )"

        def with_ranges(**ranges):
            moved = json.loads(json.dumps(lock))
            moved["dependencies"]["net10.0"]["EmbedIO-Neo"]["dependencies"].update(ranges)
            return render(moved)

        native, tasks = "EmbedIO-Neo.Quic.Native", "Microsoft.NET.ILLink.Tasks"
        for good in ("[2.19.1, )", "[3.0.0-preview.1, )"):
            with self.subTest(good=good):
                sdk.check_lock_change("a", render(lock), with_ranges(**{native: good}), "10.0.12", "10.0.13")
        for bad in ({native: "2.19.1"}, {native: "[2.19.1,3.0.0)"}, {tasks: "[10.0.14, )"}):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                sdk.check_lock_change("a", render(lock), with_ranges(**bad), "10.0.12", "10.0.13")

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


class RelockTests(unittest.TestCase):
    MAUI = "test/EmbedIO.AndroidSmoke/packages.lock.json"

    def relocked(self, base_lock, head_lock, restored):
        """Run relock on a commit holding `head_lock`, on top of one holding `base_lock`; its restore writes `restored`."""
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            lock = root / "src/Lib/packages.lock.json"
            git_repo(root, {"src/Lib/packages.lock.json": render(base_lock), self.MAUI: render(LOCK)})
            for message, text in (("base", render(base_lock)), ("head", render(head_lock))):
                lock.write_bytes(text.encode("utf-8"))
                subprocess.run(["git", "-C", str(root), "-c", "core.autocrlf=false", "add", "-A"], check=True)
                subprocess.run(["git", "-C", str(root), "-c", "user.name=test", "-c", "user.email=test@example.invalid",
                                "commit", "-q", "--allow-empty", "-m", message], check=True)

            def restore(_root, _sdk, _logs):
                lock.write_bytes(render(restored).encode("utf-8"))

            with patch.object(sdk, "restore_locks", restore):
                moves = sdk.relock(root, root / "sdk", root / "logs", "HEAD~1")
            return moves, json.loads(sdk.read(root / self.MAUI))

    def test_the_maui_locks_take_what_the_restored_locks_moved(self):
        moves, maui = self.relocked(LOCK, LOCK, moved_lock())
        self.assertEqual(moves, {"Microsoft.NET.ILLink.Tasks": ("10.0.12", "10.0.13", "new+hash/==")})
        self.assertEqual(maui, moved_lock())

    def test_a_move_dependabot_already_committed_still_counts(self):
        moves, maui = self.relocked(LOCK, moved_lock(), moved_lock())
        self.assertEqual(moves, {"Microsoft.NET.ILLink.Tasks": ("10.0.12", "10.0.13", "new+hash/==")})
        self.assertEqual(maui, moved_lock())

    def test_nothing_moved_leaves_the_maui_locks_alone(self):
        moves, maui = self.relocked(LOCK, LOCK, LOCK)
        self.assertEqual(moves, {})
        self.assertEqual(maui, LOCK)

    def test_a_package_the_restore_dropped_is_not_a_move(self):
        dropped = moved_lock()
        del dropped["dependencies"]["net10.0"]["Other"]
        moves, _ = self.relocked(LOCK, LOCK, dropped)
        self.assertEqual(list(moves), ["Microsoft.NET.ILLink.Tasks"])

    def test_a_forced_solution_restore_runs_the_relock(self):
        repository = Path(__file__).resolve().parent.parent
        # .gitignore ignores *.targets, so being present in a checkout proves nothing.
        self.assertEqual(sdk.tracked(repository, "Directory.Solution.targets"), ["Directory.Solution.targets"])
        self.assertIn("scripts/update_dotnet_sdk.py relock", sdk.read(repository / "Directory.Solution.targets"))

    def test_the_relock_job_can_push_every_lock_file_here(self):
        repository = Path(__file__).resolve().parent.parent
        spec = importlib.util.spec_from_file_location("relock", repository / ".github" / "relock.py")
        relock = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(relock)
        locks = sdk.lock_files(repository)
        self.assertTrue(sdk.MAUI_LOCKS.issubset(locks), "a MAUI lock this script names is not tracked")
        for path in locks:
            with self.subTest(path=path):
                self.assertTrue(relock.is_lock(path, "{}"), "relock.py's push job would refuse this name")
                if path not in sdk.MAUI_LOCKS:
                    self.assertEqual(len(list((repository / path).parent.glob("*.csproj"))), 1)

    def test_a_lock_target_for_one_runtime_is_declared_by_its_project(self):
        # The relock job restores on Linux and CI restores some of these on Windows. A runtime only the
        # Windows SDK infers, such as win-x86 for a net472 exe, leaves the Linux-built lock without that
        # target, and Windows then refuses the lock with NU1004.
        repository = Path(__file__).resolve().parent.parent
        for path in set(sdk.lock_files(repository)) - sdk.MAUI_LOCKS:
            lock = json.loads(sdk.read(repository / path).lstrip("\ufeff"))
            project = next((repository / path).parent.glob("*.csproj"))
            for target in lock["dependencies"]:
                if "/" in target:
                    with self.subTest(path=path, target=target):
                        self.assertIn(target.rsplit("/", 1)[1], sdk.read(project))


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

    def test_apple_platform_moves_are_named_for_the_owner(self):
        old = {"Microsoft.NET.Sdk.iOS": "26.0.9752/10.0.100", "Microsoft.NET.Sdk.macOS": "26.0.9752/10.0.100",
               "Microsoft.NET.Sdk.Android": "36.1.2/10.0.100", sdk.MONO_TOOLCHAIN: "10.0.112/10.0.100"}
        new = {"Microsoft.NET.Sdk.iOS": "27.0.10722/10.0.100", "Microsoft.NET.Sdk.macOS": "26.0.9800/10.0.100",
               "Microsoft.NET.Sdk.Android": "37.0.1/10.0.100", sdk.MONO_TOOLCHAIN: "10.0.113/10.0.100"}
        self.assertEqual(sdk.apple_platform_moves(old, new), [("Microsoft.NET.Sdk.iOS", "26.0", "27.0")])
        body = sdk.workloads_body("10.0.401", {"set": "10.0.402", "published": "2026-10-13", "security": True,
                                                "runtime": "10.0.13", "old_runtime": "10.0.12",
                                                "manifests": new, "old_manifests": old})
        self.assertIn("**Needs owner decision.**", body)
        self.assertIn("Microsoft.NET.Sdk.iOS 26.0 -> 27.0.", body)
        self.assertNotIn("Apple SDK packs change", sdk.workloads_body("10.0.401", {
            "set": "10.0.401.1", "published": "2026-10-01", "security": False, "runtime": "10.0.12",
            "old_runtime": "10.0.12", "manifests": old, "old_manifests": old}))

    def test_the_image_list_keeps_stable_xcodes_only(self):
        # The rows of actions/runner-images xcode-27-arm64-Readme.md on 2026-11-03, whose 27.2 is a release candidate.
        readme = "\n".join([
            "| 27.2           | 27B48   | /Applications/Xcode_27.2_Release_Candidate.app | /Applications/Xcode_27.2.0.app |",
            "| 27.1 (default) | 27A9275 | /Applications/Xcode_27.1.app                   | /Applications/Xcode.app |",
            "| 27.0           | 27A266a | /Applications/Xcode_27.app                     | /Applications/Xcode_27.0.0.app |",
            "| 26.4.1         | 17E202  | /Applications/Xcode_26.4.1.app | /Applications/Xcode_26.4.app |",
            "| 27.3_beta_1    | 27C1    | /Applications/Xcode_27.3_beta_1.app | |",
            "| 27.4           | 27D1    | /Applications/Xcode_27.3.app | |",
        ])
        self.assertEqual(sdk.runner_xcodes(readme), ["27.1", "27", "26.4.1"])
        with self.assertRaisesRegex(ValueError, "lists no Xcode"):
            sdk.runner_xcodes("### Xcode\n")

    def test_an_xcode_named_without_its_minor_compares_as_that_version(self):
        self.assertEqual(sdk.xcode_key("27"), sdk.xcode_key("27.0"))
        self.assertLess(sdk.xcode_key("27"), sdk.xcode_key("27.0.1"))
        self.assertLess(sdk.xcode_key("26.6"), sdk.xcode_key("27"))
        feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"),
                     "10.0.402": ("10.0.113", "2026-10-13T00:00:00Z", "27.0.10722")})
        feed.bytes[sdk.MACOS_IMAGE_README] = (b"| 27.1 | 27A9275 | /Applications/Xcode_27.1.app | |\n"
                                              b"| 27.0 | 27A266a | /Applications/Xcode_27.app | |\n")
        self.assertEqual(self.choose_for(feed)[0]["xcode"], "27")

    def apple_feed(self, *image):
        feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"),
                     "10.0.402": ("10.0.113", "2026-10-13T00:00:00Z", "27.1.10018", "27.0.10742")})
        if image:
            feed.bytes[sdk.MACOS_IMAGE_README] = image_readme(*image)
        return feed

    def choose_for(self, feed, xcode="26.6"):
        return sdk.choose_workloads("10.0.401", "10.0.402", RELEASES, feed.fetch, feed.get, NOW, xcode)

    def test_a_set_waits_for_the_xcode_its_apple_packs_need(self):
        for image in (("26.6 (default)", "26.5"), ("27.0", "26.6 (default)")):
            with self.subTest(image=image):
                move, notes = self.choose_for(self.apple_feed(*image))
                self.assertIsNone(move)
                self.assertTrue(any("10.0.402 waits for Xcode 27.1 on macos-26, which lists up to" in n for n in notes), notes)

    def test_the_oldest_stable_xcode_new_enough_is_taken(self):
        move, _ = self.choose_for(self.apple_feed("27.2", "27.1.1", "27.1", "26.6 (default)"))
        self.assertEqual((move["set"], move["old_xcode"], move["xcode"]), ("10.0.402", "26.6", "27.1"))
        self.assertEqual(move["targets"], {"ios": ["26.5", "27.1"], "maccatalyst": ["26.5", "27.0"]})

    def test_an_older_set_the_image_can_build_is_taken_instead(self):
        feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"),
                     "10.0.401.1": ("10.0.112", "2026-09-28T00:00:00Z", "26.5.10400"),
                     "10.0.402": ("10.0.113", "2026-10-13T00:00:00Z", "27.0.10722")})
        feed.bytes[sdk.MACOS_IMAGE_README] = image_readme("26.6 (default)")
        move, notes = self.choose_for(feed)
        self.assertEqual((move["set"], move["xcode"], move["targets"]), ("10.0.401.1", "26.6", {}))
        self.assertTrue(any("10.0.402 waits for Xcode" in n for n in notes), notes)

    def test_a_pin_new_enough_stays_and_the_image_is_not_asked(self):
        move, _ = self.choose_for(self.apple_feed(), "27.1")  # the feed has no README, so a read would raise
        self.assertEqual((move["xcode"], move["old_xcode"]), ("27.1", "27.1"))
        self.assertEqual(sorted(move["targets"]), ["ios", "maccatalyst"])

    def test_packs_the_apps_do_not_build_with_do_not_hold_a_set(self):
        old = {"Microsoft.NET.Sdk.iOS": "26.5.1/10.0.100", "Microsoft.NET.Sdk.tvOS": "26.5.1/10.0.100"}
        new = {"Microsoft.NET.Sdk.iOS": "26.5.2/10.0.100", "Microsoft.NET.Sdk.tvOS": "27.0.1/10.0.100"}
        self.assertEqual(sdk.apple_targets(old, new), {})

    def test_lock_targets_follow_their_platform_only(self):
        lock = ('{\r\n  "dependencies": {\r\n    "net10.0-ios26.5": {\r\n      "A": {"resolved": "26.5.0"}\r\n    },\r\n'
                '    "net10.0-ios26.5/iossimulator-arm64": {},\r\n    "net10.0-maccatalyst26.5": {},\r\n    "net10.0": {}\r\n  }\r\n}\r\n')
        moved = sdk.move_lock_targets(lock, {"ios": ["26.5", "27.1"]})
        self.assertEqual(moved, lock.replace("net10.0-ios26.5", "net10.0-ios27.1"))
        self.assertEqual(sdk.move_lock_targets(lock, {"ios": ["26.4", "27.1"]}), lock)
        self.assertEqual(sdk.move_lock_targets(lock, {}), lock)

    def test_pins_must_name_one_set(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            git_repo(root, {"scripts/a.sh": "dotnet workload install maui-android --version 10.0.401\n",
                            "scripts/b.ps1": "dotnet workload install $env:W --version 10.0.401\n",
                            "scripts/c.ps1": "dotnet workload restore\n"})
            self.assertEqual(sdk.workload_pins(root), ("10.0.401", ["scripts/a.sh", "scripts/b.ps1"]))
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


class WorkflowTests(unittest.TestCase):
    WORKFLOWS = Path(__file__).resolve().parent.parent / ".github" / "workflows"

    def test_no_workflow_holds_a_pin_the_updater_moves(self):
        # A commit changing a workflow needs a permission GITHUB_TOKEN cannot be given.
        for path in self.WORKFLOWS.glob("*.yml"):
            text = path.read_text(encoding="utf-8")
            with self.subTest(path=path.name):
                self.assertNotRegex(text, r"(?m)^\s*dotnet-version:\s*\d+\.\d+\.\d+\s*$")
                self.assertNotRegex(text, sdk.WORKLOAD_INSTALL)
                self.assertNotRegex(text, sdk.XCODE_PIN)
                self.assertNotIn("DEVELOPER_DIR", text)

    def test_the_macos_jobs_select_the_pinned_xcode_before_using_it(self):
        repository = self.WORKFLOWS.parent.parent
        self.assertRegex(sdk.xcode_pin(repository) or "", r"^\d+\.\d+")
        for name, first_use in (("mac-catalyst.yml", "actions/setup-dotnet"), ("maui-https.yml", "--ios-stage prepare")):
            text = (self.WORKFLOWS / name).read_text(encoding="utf-8")
            with self.subTest(name=name):
                self.assertLess(text.index("./scripts/maui_https_ci.ps1 xcode"), text.index(first_use))

    def test_the_hold_reads_the_image_the_jobs_run_on(self):
        self.assertIn(f"/{sdk.MACOS_IMAGE}-", sdk.MACOS_IMAGE_README)
        self.assertIn(f"runs-on: {sdk.MACOS_IMAGE}", (self.WORKFLOWS / "mac-catalyst.yml").read_text(encoding="utf-8"))
        matrix = (self.WORKFLOWS / "maui-https.yml").read_text(encoding="utf-8")
        self.assertEqual(set(re.findall(r'"platform":"(?:ios|maccatalyst)","os":"([^"]+)"', matrix)), {sdk.MACOS_IMAGE})

    def test_the_apple_locks_a_platform_move_renames_are_all_readable_here(self):
        repository = self.WORKFLOWS.parent.parent
        for name in ("MacCatalystSmoke/packages", "MauiHttpsSmoke/ios.packages", "MauiHttpsSmoke/maccatalyst.packages"):
            with self.subTest(name=name):
                self.assertIn(f"test/EmbedIO.{name}.lock.json", sdk.MAUI_LOCKS)
                self.assertRegex(sdk.read(repository / f"test/EmbedIO.{name}.lock.json"), sdk.LOCK_TARGET)

    def test_the_repo_bot_opens_the_pull_requests(self):
        # A pull request opened with the workflow's own token waits for approval and gets no normal CI.
        text = (self.WORKFLOWS / "update-dotnet-sdk.yml").read_text(encoding="utf-8")
        self.assertIn("GH_TOKEN: ${{ steps.app-token.outputs.token }}", text)
        self.assertNotIn("github.token", text)
        self.assertNotRegex(text, r"(?m)^\s*(contents|pull-requests|actions): write")

    def test_the_proposal_artifact_keeps_the_files_under_github(self):
        text = (self.WORKFLOWS / "update-dotnet-sdk.yml").read_text(encoding="utf-8")
        step = text[text.index("name: dotnet-sdk-proposal"):]
        self.assertIn("include-hidden-files: true", step[:step.index("- uses:")])


class ImageTests(unittest.TestCase):
    def test_reads_and_moves_the_pinned_sdk_image(self):
        self.assertEqual(sdk.image_tags(DOCKERFILE), ["10.0.401"])
        moved = sdk.rewrite_image(DOCKERFILE, "10.0.402", "10.0.13", {"10.0.402": NEW_DIGEST})
        self.assertEqual(moved, DOCKERFILE.replace("sdk:10.0.401 (runtime 10.0.12,", "sdk:10.0.402 (runtime 10.0.13,")
                         .replace("sha256:" + "e" * 64, NEW_DIGEST))

    def test_an_image_keeps_its_suffix(self):
        text = ("# mcr.microsoft.com/dotnet/sdk:10.0.401-alpine3.24-aot (runtime 10.0.12, Alpine 3.24).\n"
                "FROM mcr.microsoft.com/dotnet/sdk@sha256:" + "e" * 64 + "\n")
        self.assertEqual(sdk.image_tags(text), ["10.0.401-alpine3.24-aot"])
        self.assertEqual(sdk.image_tag("10.0.401", "10.0.402"), "10.0.402")
        moved = sdk.rewrite_image(text, "10.0.402", "10.0.13", {"10.0.402-alpine3.24-aot": NEW_DIGEST})
        self.assertEqual(moved, text.replace("10.0.401-alpine3.24-aot (runtime 10.0.12",
                                             "10.0.402-alpine3.24-aot (runtime 10.0.13")
                         .replace("sha256:" + "e" * 64, NEW_DIGEST))

    def test_the_musl_image_has_the_linker_its_aot_publish_needs(self):
        repository = Path(__file__).resolve().parent.parent
        text = sdk.read(repository / "native/EmbedIO.Quic.Tls/Dockerfile.musl-validation")
        self.assertEqual([tag.rpartition("-")[2] for tag in sdk.image_tags(text)], ["aot"])

    def test_every_sdk_image_here_is_a_pin_the_updater_moves(self):
        repository = Path(__file__).resolve().parent.parent
        for path in sdk.tracked(repository, "*Dockerfile*"):
            text = sdk.read(repository / path)
            images = [line for line in text.splitlines() if line.startswith("FROM") and "dotnet/sdk" in line]
            with self.subTest(path=path):
                self.assertEqual(len(sdk.image_tags(text)), len(images))

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
            self.assertEqual((proposal["new_sdk"], proposal["digests"], list(files_)),
                             ("10.0.402", {"10.0.402": NEW_DIGEST}, ["docker/Dockerfile"]))
            (out / "files/docker/Dockerfile").write_bytes(sdk.rewrite_image(DOCKERFILE, "10.0.402", "10.0.13", {"10.0.402": NEW_DIGEST})
                                                          .replace("apt-get update", "curl evil").encode())
            with self.assertRaises(ValueError):
                sdk.check_proposal(root, out)
            (root / "global.json").write_text('{"sdk": {"version": "10.0.403"}}', encoding="utf-8")
            with self.assertRaises(ValueError):
                sdk.check_proposal(root, out)


class ProposalTests(unittest.TestCase):
    BASE = {
        "global.json": '{\n  "sdk": {\n    "version": "10.0.401",\n    "rollForward": "disable"\n  }\n}\n',
        ".github/workflows/ci.yml": "jobs:\n  a:\n    steps:\n      - with:\n          global-json-file: global.json\n",
        "scripts/install.ps1": "dotnet workload install maui-android --version 10.0.401\n",
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
            "test/Probe/Probe.csproj": self.BASE["test/Probe/Probe.csproj"].replace("10.0.12", "10.0.13"),
            "src/Lib/packages.lock.json": render(moved_lock()),
        }
        self.write_sdk(self.sdk_files)

    def tearDown(self):
        self.temp.cleanup()

    def write_sdk(self, changed, digests=None):
        title, branch = sdk.sdk_title_and_branch("10.0.402", "10.0.13", "main")
        sdk.write_proposal(self.out / "sdk", {"kind": "sdk", "base_sha": "1" * 40, "base_ref": "main", "title": title,
                                              "branch": branch, "old_sdk": "10.0.401", "new_sdk": "10.0.402",
                                              "old_runtime": "10.0.12", "new_runtime": "10.0.13",
                                              "digests": digests or {}}, changed, "body\n")

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
            ".github/workflows/ci.yml": "jobs:\n  a:\n    steps:\n      - run: curl evil\n",
            "test/Probe/Probe.csproj": "<Project>\n  <RuntimeFrameworkVersion>10.0.13</RuntimeFrameworkVersion>\n  <Evil />\n</Project>\n",
            "scripts/install.ps1": "dotnet workload install maui-android --version 10.0.402\n",
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
        moved = self.BASE["scripts/install.ps1"].replace("10.0.401", "10.0.401.1")
        self.write_workloads({"scripts/install.ps1": moved})
        proposal, changed = sdk.check_proposal(self.root, self.out / "workloads")
        self.assertEqual((proposal["new_set"], list(changed)), ("10.0.401.1", ["scripts/install.ps1"]))
        for bad in ({"scripts/install.ps1": moved.replace("maui-android", "maui-ios")},
                    {"scripts/install.ps1": moved, "global.json": self.sdk_files["global.json"]}):
            with self.subTest(bad=sorted(bad)):
                self.write_workloads(bad)
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out / "workloads")
        with self.assertRaises(ValueError):
            sdk.check_workloads_proposal(self.root, {"old_set": "10.0.401", "new_set": "10.0.400", "base_ref": "main",
                                                     "new_runtime": "10.0.12", "title": "", "branch": ""}, {})

    def test_a_proposal_may_not_change_a_workflow(self):
        workflow = {".github/workflows/ci.yml": self.BASE[".github/workflows/ci.yml"] + "# moved\n"}
        self.write_sdk({**self.sdk_files, **workflow})
        with self.assertRaisesRegex(ValueError, "is a workflow"):
            sdk.check_proposal(self.root, self.out / "sdk")
        self.write_workloads(workflow)
        with self.assertRaisesRegex(ValueError, "is a workflow"):
            sdk.check_proposal(self.root, self.out / "workloads")

    def test_an_sdk_move_carries_every_pinned_image(self):
        git_repo(self.root, {"docker/Dockerfile": DOCKERFILE})
        digests = {"10.0.402": NEW_DIGEST}
        moved = sdk.rewrite_image(DOCKERFILE, "10.0.402", "10.0.13", digests)
        files = {**self.sdk_files, "docker/Dockerfile": moved}
        self.write_sdk(files, digests)
        self.assertIn("docker/Dockerfile", sdk.check_proposal(self.root, self.out / "sdk")[1])
        for bad_files, bad_digests in ((self.sdk_files, digests),
                                       (files, {}),
                                       (files, {"10.0.402": "sha256:short"}),
                                       ({**files, "docker/Dockerfile": moved.replace("apt-get update", "curl evil")}, digests)):
            with self.subTest(digests=bad_digests, moved="curl evil" in bad_files.get("docker/Dockerfile", "")):
                self.write_sdk(bad_files, bad_digests)
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out / "sdk")

    def test_propose_commits_each_move_through_the_api(self):
        self.write_workloads({"scripts/install.ps1":
                              self.BASE["scripts/install.ps1"].replace("10.0.401", "10.0.401.1")})
        api, review = self.api(pulls=[]), Mock()
        urls = sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", api, review)
        self.assertEqual(urls, ["https://github.com/owner/repo/pull/7", "https://github.com/owner/repo/pull/7"])
        calls = [c.args[0] for c in api.call_args_list]
        self.assertEqual(sum(1 for endpoint in calls if endpoint.endswith("/git/blobs")), len(self.sdk_files) + 1)
        # The Repo Bot App has no Actions permission, and its pull requests start their own checks.
        self.assertEqual([endpoint for endpoint in calls if "/actions/" in endpoint], [])
        heads = [c.args[2]["head"] for c in api.call_args_list if c.args[0] == "repos/owner/repo/pulls"]
        self.assertEqual(heads, ["update-dotnet-sdk/10.0.402", "update-dotnet-workloads/10.0.401.1"])
        self.assertEqual(review.call_count, 2)

    def test_a_workload_set_opens_as_a_draft_labelled_for_the_owner(self):
        self.write_workloads({"scripts/install.ps1":
                              self.BASE["scripts/install.ps1"].replace("10.0.401", "10.0.401.1")})
        api = self.api(pulls=[])
        sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", api, Mock())
        created = {c.args[2]["head"]: c.args[2] for c in api.call_args_list if c.args[0] == "repos/owner/repo/pulls"}
        labels = [c.args[2]["labels"] for c in api.call_args_list if c.args[0].endswith("/labels")]
        self.assertIs(created["update-dotnet-workloads/10.0.401.1"]["draft"], True)
        self.assertIs(created["update-dotnet-sdk/10.0.402"]["draft"], False)
        self.assertEqual(labels, [list(sdk.LABELS), [*sdk.LABELS, "needs owner decision"]])

    def test_an_sdk_move_keeps_the_ordinary_review(self):
        api, review = self.api(pulls=[]), Mock()
        sdk.propose(self.root, self.out, "owner/repo", "1" * 40, "main", api, review)
        created = [c.args[2] for c in api.call_args_list if c.args[0] == "repos/owner/repo/pulls"]
        labels = [c.args[2]["labels"] for c in api.call_args_list if c.args[0].endswith("/labels")]
        self.assertEqual([(pull["head"], pull["draft"]) for pull in created], [("update-dotnet-sdk/10.0.402", False)])
        self.assertEqual(labels, [list(sdk.LABELS)])
        self.assertEqual(review.call_count, 1)

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


class IosSimulatorTests(unittest.TestCase):
    IPHONE = "com.apple.CoreSimulator.SimDeviceType.iPhone-16"

    @staticmethod
    def runtime(version, hosts=("iPhone-16",), platform="iOS", available=True):
        return {"platform": platform, "version": version, "isAvailable": available,
                "identifier": f"com.apple.CoreSimulator.SimRuntime.{platform}-{version.replace('.', '-')}",
                "supportedDeviceTypes": [{"identifier": f"com.apple.CoreSimulator.SimDeviceType.{host}"} for host in hosts]}

    def newest(self, *runtimes):
        return maui_https_probe.newest_ios_runtime({"runtimes": list(runtimes)}, self.IPHONE).rsplit(".", 1)[1]

    def test_a_runtime_that_cannot_host_the_device_is_passed_over(self):
        # The xcode-27 image, 2026-11-03: creating an iPhone 16 on iOS 27.1 fails with "Incompatible device".
        self.assertEqual(self.newest(self.runtime("27.0"), self.runtime("27.1", hosts=("iPhone-Duo",))), "iOS-27-0")

    def test_versions_compare_as_numbers(self):
        self.assertEqual(self.newest(self.runtime("26.5"), self.runtime("26.10"), self.runtime("26.4.1")), "iOS-26-10")

    def test_other_platforms_and_unavailable_runtimes_do_not_count(self):
        self.assertEqual(self.newest(self.runtime("26.5"), self.runtime("27.0", platform="tvOS"),
                                     self.runtime("27.1", available=False)), "iOS-26-5")

    def test_no_usable_runtime_says_so(self):
        with self.assertRaisesRegex(RuntimeError, "iPhone-16"):
            self.newest(self.runtime("27.1", hosts=("iPhone-Duo",)))

    def test_the_smoke_script_pins_no_simulator(self):
        text = sdk.read(Path(__file__).resolve().parent / "run_maui_https_smoke.py")
        self.assertIn("newest_ios_runtime(", text)
        self.assertNotRegex(text, r"iOS-\d")


class AppleAppTests(unittest.TestCase):
    ROOT = Path(__file__).resolve().parent.parent

    def test_the_apple_apps_adopt_the_scene_lifecycle(self):
        # Built with the 27 SDK, UIKit stops an app at launch without this, and CI only builds with that SDK after a
        # workload set moves, so nothing else would say so before that pull request.
        apps = sorted(self.ROOT.glob("test/*/Platforms/*/Info.plist"))
        self.assertEqual(len(apps), 3)
        for info in apps:
            with self.subTest(app=str(info.relative_to(self.ROOT))):
                manifest = plistlib.loads(info.read_bytes())["UIApplicationSceneManifest"]
                scene = manifest["UISceneConfigurations"]["UIWindowSceneSessionRoleApplication"][0]
                self.assertEqual(scene["UISceneConfigurationName"], "__MAUI_DEFAULT_SCENE_CONFIGURATION__")
                self.assertIn(f'[Register("{scene["UISceneDelegateClassName"]}")]', sdk.read(info.with_name("Program.cs")))


class AppleMoveTests(unittest.TestCase):
    """A workload set move that reaches a new Apple platform, prepared and then checked as the propose job does."""
    CI = "scripts/maui_https_ci.ps1"
    IOS = "test/EmbedIO.MauiHttpsSmoke/ios.packages.lock.json"
    CATALYST = "test/EmbedIO.MacCatalystSmoke/packages.lock.json"
    ANDROID = "test/EmbedIO.AndroidSmoke/packages.lock.json"
    BASE = {
        CI: "$xcode = '/Applications/Xcode_26.6.app/Contents/Developer'\r\n"
            "dotnet workload install $env:W --version 10.0.401\r\n",
        IOS: '{"dependencies": {"net10.0-ios26.5": {}, "net10.0-ios26.5/iossimulator-arm64": {}}}\r\n',
        CATALYST: '{"dependencies": {"net10.0-maccatalyst26.5": {}, "net10.0-maccatalyst26.5/maccatalyst-arm64": {}}}\r\n',
        ANDROID: '{"dependencies": {"net10.0-android36.0": {}}}\r\n',
    }

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root, self.out = Path(self.temp.name) / "repo", Path(self.temp.name) / "workloads"
        git_repo(self.root, self.BASE)
        self.feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"),
                          "10.0.402": ("10.0.113", "2026-10-13T00:00:00Z", "27.1.10018", "27.0.10742")})
        self.feed.bytes[sdk.MACOS_IMAGE_README] = image_readme("27.2", "27.1", "26.6 (default)")

    def tearDown(self):
        self.temp.cleanup()

    def prepare(self):
        return sdk.prepare_workloads(self.root, self.out, "1" * 40, "main", "10.0.402", RELEASES, self.feed.fetch,
                                     self.feed.get, NOW)

    def proposed(self, path):
        return sdk.read(self.out / "files" / path)

    def test_the_whole_move_passes_the_propose_job_checks(self):
        self.assertTrue(self.prepare()[0])
        proposal, files = sdk.check_proposal(self.root, self.out)
        self.assertEqual(sorted(files), sorted([self.CI, self.IOS, self.CATALYST]))
        self.assertEqual((proposal["old_xcode"], proposal["new_xcode"]), ("26.6", "27.1"))
        self.assertEqual(self.proposed(self.CI), self.BASE[self.CI].replace("Xcode_26.6", "Xcode_27.1").replace("10.0.401", "10.0.402"))
        self.assertEqual(self.proposed(self.IOS), self.BASE[self.IOS].replace("ios26.5", "ios27.1"))
        self.assertEqual(self.proposed(self.CATALYST), self.BASE[self.CATALYST].replace("maccatalyst26.5", "maccatalyst27.0"))
        body = (self.out / "body.md").read_text(encoding="utf-8")
        self.assertIn("move from Xcode 26.6 to 27.1", body)
        self.assertIn("ios 26.5 -> 27.1, maccatalyst 26.5 -> 27.0", body)
        self.assertIn("gh workflow run maui-https.yml --ref <branch>", body)

    def test_a_held_set_proposes_nothing(self):
        self.feed.bytes[sdk.MACOS_IMAGE_README] = image_readme("26.6 (default)")
        changed, notes = self.prepare()
        self.assertFalse(changed)
        self.assertFalse(self.out.exists())
        self.assertEqual(notes, ["Workload set 10.0.402 waits for Xcode 27.1 on macos-26, which lists up to 26.6.",
                                 "Workload set 10.0.401 stays for now."])

    def edit(self, field=None, value=None, path=None, text=None):
        proposal = json.loads((self.out / "proposal.json").read_text(encoding="utf-8"))
        if field:
            proposal[field] = value
        (self.out / "proposal.json").write_text(json.dumps(proposal), encoding="utf-8")
        if path:
            (self.out / "files" / path).write_bytes(text.encode("utf-8"))

    def test_anything_else_is_refused(self):
        self.prepare()
        ci, ios = self.proposed(self.CI), self.proposed(self.IOS)
        for name, change in (("another Xcode in the file", {"path": self.CI, "text": ci.replace("Xcode_27.1", "Xcode_27.2")}),
                             ("a stated Xcode the file does not have", {"field": "new_xcode", "value": "27.2"}),
                             ("a lock's packages", {"path": self.IOS, "text": ios.replace("{}", '{"Evil": {}}', 1)})):
            with self.subTest(name=name):
                self.prepare()
                self.edit(**change)
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out)

    def agreeing(self, new_xcode, targets):
        """A proposal whose files are exactly what its stated values produce, so only a check of the values can refuse it."""
        self.prepare()
        changed = sdk.workload_move_files(self.root, "10.0.401", "10.0.402", "26.6", new_xcode, targets)
        shutil.rmtree(self.out / "files")
        proposal = json.loads((self.out / "proposal.json").read_text(encoding="utf-8"))
        proposal.update(new_xcode=new_xcode, targets=targets)
        sdk.write_proposal(self.out, proposal, changed, "body\n")

    def test_stated_values_are_checked_even_when_the_files_agree(self):
        good = {"ios": ["26.5", "27.1"], "maccatalyst": ["26.5", "27.0"]}
        self.agreeing("27.1", good)
        sdk.check_proposal(self.root, self.out)
        for name, xcode, targets in (("Xcode moved back", "26.5", good),
                                     ("not a version", "27.1_0", good),
                                     ("a platform that breaks out of the lock's string", "27.1",
                                      {**good, "ios": ["26.5", '27.1": {}, "x']})):
            with self.subTest(name=name):
                self.agreeing(xcode, targets)
                with self.assertRaises(ValueError):
                    sdk.check_proposal(self.root, self.out)

    def test_a_target_left_behind_is_refused(self):
        self.prepare()
        proposal = json.loads((self.out / "proposal.json").read_text(encoding="utf-8"))
        proposal["files"].remove(self.IOS)
        (self.out / "proposal.json").write_text(json.dumps(proposal), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "leaves workload pins unmoved"):
            sdk.check_proposal(self.root, self.out)

    def test_a_move_without_a_new_platform_changes_only_the_workload_version(self):
        self.feed = Feed({"10.0.401": ("10.0.112", "2026-09-08T21:35:33Z"), "10.0.402": ("10.0.113", "2026-10-13T00:00:00Z")})
        self.assertTrue(self.prepare()[0])
        proposal, files = sdk.check_proposal(self.root, self.out)
        self.assertEqual((list(files), proposal["new_xcode"], proposal["targets"]), ([self.CI], "26.6", {}))
        self.assertEqual(self.proposed(self.CI), self.BASE[self.CI].replace("10.0.401", "10.0.402"))


class PrepareTests(unittest.TestCase):
    def test_an_sdk_move_puts_the_images_in_its_proposal(self):
        pinned = '{\n  "sdk": {\n    "version": "10.0.401",\n    "rollForward": "disable"\n  }\n}\n'
        real_run = subprocess.run

        def run(command, **kwargs):
            if command[1] == "--version":
                return subprocess.CompletedProcess(command, 0, stdout="10.0.402\n")
            return real_run(command, **kwargs)

        with tempfile.TemporaryDirectory() as temp:
            root, out = Path(temp) / "repo", Path(temp) / "runner" / "temp" / "sdk"
            git_repo(root, {"global.json": pinned, "docker/Dockerfile": DOCKERFILE})
            with patch.object(sdk, "download", return_value=HASH), patch.object(sdk, "extract"), \
                    patch.object(sdk, "restore_locks", return_value={}), patch.object(sdk, "MAUI_LOCKS", frozenset()), \
                    patch.object(sdk.subprocess, "run", run):
                moved, _ = sdk.prepare_sdk(root, out, Path(temp) / "dotnet", "1" * 40, "main", RELEASES, NOW, None,
                                           "linux-x64", lambda tag: NEW_DIGEST)
            self.assertTrue(moved)
            (root / "global.json").write_bytes(pinned.encode("utf-8"))  # the base the propose job checks against
            _, files_ = sdk.check_proposal(root, out)
            self.assertEqual(sorted(files_), ["docker/Dockerfile", "global.json"])
            self.assertIn(NEW_DIGEST.encode(), files_["docker/Dockerfile"])
            self.assertIn(f"`dotnet/sdk:10.0.402`: `{NEW_DIGEST}`", (out / "body.md").read_text(encoding="utf-8"))

    def test_an_sdk_move_waits_for_every_image_it_needs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            pinned = '{"sdk": {"version": "10.0.401"}}'
            git_repo(root, {"global.json": pinned, "docker/Dockerfile": DOCKERFILE})
            changed, notes = sdk.prepare_sdk(root, root / "out", root / "sdk", "1" * 40, "main", RELEASES, NOW, None,
                                             "linux-x64", lambda tag: None)
            self.assertFalse(changed)
            self.assertTrue(any("waits for MCR to publish dotnet/sdk:10.0.402" in note for note in notes), notes)
            self.assertEqual(sdk.read(root / "global.json"), pinned)
            self.assertFalse((root / "sdk").exists())

    def test_an_image_gets_its_own_proposal_only_without_an_sdk_move(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            git_repo(root, {"global.json": '{"sdk": {"version": "10.0.402"}}'})
            feed = Feed({"10.0.402": ("10.0.113", "2026-10-14T00:00:00Z")})
            for moved, calls in ((True, 0), (False, 1)):
                with self.subTest(moved=moved), patch.object(sdk, "prepare_sdk", return_value=(moved, [])), \
                        patch.object(sdk, "prepare_image", return_value=(False, [])) as image:
                    sdk.prepare(root, root / "out", root / "sdk", "1" * 40, "main", feed.fetch, feed.get, NOW)
                    self.assertEqual(image.call_count, calls)

    def test_current_pins_propose_nothing_and_a_bad_archive_stops_before_use(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            git_repo(root, {"global.json": '{"sdk": {"version": "10.0.402"}}',
                            "scripts/a.sh": "dotnet workload install maui-ios --version 10.0.402\n"})
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
