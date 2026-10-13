"""Repository-local policy: keep automated updates open for all-checks review."""
import json
import subprocess
import sys


def defer_merge(url):
    """Disable an existing future merge; never arm or perform a merge."""
    result = subprocess.run(["gh", "pr", "view", url, "--json", "autoMergeRequest"],
                            capture_output=True, text=True, encoding="utf-8", timeout=120, check=True)
    state = json.loads(result.stdout)
    if "autoMergeRequest" not in state:
        raise ValueError("The PR response omitted its auto-merge state")
    if state["autoMergeRequest"] is not None:
        subprocess.run(["gh", "pr", "merge", "--disable-auto", url],
                       capture_output=True, text=True, encoding="utf-8", timeout=120, check=True)
    print("Left open for exact-head review: every supplemental check must finish green; "
          "reviewed scanner findings also require the repository's acceptance and alert-dismissal evidence.")


if __name__ == "__main__":
    defer_merge(sys.argv[1])
