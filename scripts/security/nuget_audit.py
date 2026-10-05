"""Fail on any vulnerable NuGet package in `dotnet list package --vulnerable` JSON.

`dotnet list package --vulnerable --include-transitive --format json` exits 0
even when it finds a vulnerable package, so the Security workflow reads its
JSON with this instead. Any package listed under any project fails, whatever
the severity, and so does any problem the command reports, or a run that
audited no project at all.

Usage::

    python scripts/security/nuget_audit.py nuget-audit.json
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any


def findings(audit: dict[str, Any]) -> list[str]:
    """One line per vulnerable package or reported problem."""
    lines: list[str] = []
    for project in audit.get("projects") or []:
        for framework in project.get("frameworks") or []:
            packages = (framework.get("topLevelPackages") or []) + (framework.get("transitivePackages") or [])
            for item in packages:
                lines.append(f"{project.get('path', '')}: {item.get('id')} {item.get('resolvedVersion', '')}")
                for advisory in item.get("vulnerabilities") or []:
                    lines.append(f"  {advisory.get('severity')} {advisory.get('advisoryurl')}")
    for problem in audit.get("problems") or []:
        lines.append(str(problem.get("text") or json.dumps(problem)))
    return lines


def main(argv: list[str]) -> int:
    audit = json.loads(Path(argv[1]).read_text(encoding="utf-8-sig"))
    projects = audit.get("projects") or []
    if not projects:
        print("No projects were audited.")
        return 1
    found = findings(audit)
    for line in found:
        print(line)
    print(f"{len(projects)} project(s) audited; {'vulnerable packages or problems above' if found else 'nothing found'}.")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
