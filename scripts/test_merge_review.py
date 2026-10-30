"""Mock GitHub interactions: the .NET SDK updater's pull requests never arm or perform a merge."""
from pathlib import Path
import subprocess
import sys
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / ".github/security"))
import merge_review
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

if __name__ == "__main__":
    unittest.main()
