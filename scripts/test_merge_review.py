"""Mock GitHub interactions: automated updates never arm or perform a merge."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/security"))
import merge_review
spec = importlib.util.spec_from_file_location("reviewed_yara_update", os.environ.get("YARA_REVIEW_MODULE", ROOT / ".github/security/yara_update.py"))
yara = importlib.util.module_from_spec(spec)
spec.loader.exec_module(yara)
URL = "https://github.com/owner/repository/pull/1"

class MergeReviewTests(unittest.TestCase):
    def test_unarmed_pr_is_only_read_and_left_open(self):
        with patch.object(merge_review.subprocess, "run", return_value=Mock(stdout='{"autoMergeRequest":null}')) as run:
            merge_review.defer_merge(URL)
        self.assertEqual(run.call_count, 1)
        self.assertEqual(run.call_args.args[0], ["gh", "pr", "view", URL, "--json", "autoMergeRequest"])

    def test_existing_arm_is_disabled_without_merging(self):
        with patch.object(merge_review.subprocess, "run", return_value=Mock(stdout='{"autoMergeRequest":{}}')) as run:
            merge_review.defer_merge(URL)
        self.assertEqual(run.call_count, 2)
        self.assertEqual(run.call_args.args[0], ["gh", "pr", "merge", "--disable-auto", URL])

    def test_unknown_state_and_github_failures_fail_visibly(self):
        for response in ("{}", "invalid json"):
            with self.subTest(response=response), patch.object(merge_review.subprocess, "run", return_value=Mock(stdout=response)) as run:
                with self.assertRaises(ValueError):
                    merge_review.defer_merge(URL)
                self.assertEqual(run.call_count, 1)
        for fail_at in (0, 1):
            failure = subprocess.CalledProcessError(1, "mock gh")
            responses = [Mock(stdout='{"autoMergeRequest":{}}')] * fail_at + [failure]
            with self.subTest(fail_at=fail_at), patch.object(merge_review.subprocess, "run", side_effect=responses):
                with self.assertRaises(subprocess.CalledProcessError):
                    merge_review.defer_merge(URL)

    def test_yara_existing_proposal_uses_default_review_policy(self):
        temporary = ROOT / "TestResults" / "merge-review-tests"
        temporary.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(dir=temporary) as directory:
            root = Path(directory)
            old = {"yara_forge": {"release":"20261001", "asset":"rules.zip", "url":"https://github.com/YARAHQ/yara-forge/releases/download/20261001/rules.zip", "sha256":"a"*64}}
            new = {"yara_forge": dict(old["yara_forge"], release="20261012", url="https://github.com/YARAHQ/yara-forge/releases/download/20261012/rules.zip", sha256="b"*64)}
            pins = root / yara.PIN_PATH
            pins.parent.mkdir(parents=True)
            pins.write_text(json.dumps(old))
            title, branch = yara.title_and_branch(old, new)
            (root / "proposal.json").write_text(json.dumps(dict(base_sha="a"*40,title=title,branch=branch,pins=new)))
            api = Mock(return_value=[{"state":"open", "html_url":URL}])
            with patch.object(subprocess, "run", return_value=Mock(returncode=0,stdout='{"autoMergeRequest":{}}',stderr="")) as run:
                self.assertEqual(yara.propose(root, root, "owner/repository", "a"*40, api=api), URL)
            self.assertEqual([call.args[0] for call in run.call_args_list], [
                ["gh", "pr", "view", URL, "--json", "autoMergeRequest"],
                ["gh", "pr", "merge", "--disable-auto", URL]])
            api.assert_called_once()

if __name__ == "__main__":
    unittest.main()
