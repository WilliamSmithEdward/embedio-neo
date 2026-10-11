"""Run the same consumer against pinned upstream and both Neo assets; fail new drift."""

import argparse
import base64
import copy
import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FIXTURE = ROOT / "test" / "EmbedIO.Compatibility"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def normalized(value):
    # SWAN uses process-specific object IDs. Retain raw IDs in the source reports.
    if isinstance(value, dict):
        if list(value) == ["$circref"] and re.fullmatch(r"-?\d+", str(value["$circref"])):
            return {"$circref": "<object-id>"}
        return {key: normalized(item) for key, item in value.items()}
    if isinstance(value, list):
        return [normalized(item) for item in value]
    return value


def differences(old, new, path=""):
    if isinstance(old, dict) and isinstance(new, dict) and old.keys() == new.keys():
        result = []
        for key in sorted(old):
            result.extend(differences(old[key], new[key], path + "/" + key))
        return result
    equal = old == new and isinstance(old, bool) == isinstance(new, bool)
    return [] if equal else [{"path": path, "upstream": old, "neo": new}]


def validate_outcomes(report, upstream, contract):
    cases = report["cases"]
    errors = []
    statuses = {
        "controller-error": 500, "controller-head": 405, "controller-not-found": 404,
        "exact-extra": 404, "file-missing": 404, "file-invalid-range": 416,
        "file-not-modified": 304, "auth-missing": 401, "auth-wrong": 401,
        "file-range": 206, "file-suffix": 206,
        "auth-malformed": 401, "method-not-allowed": 405, "post-json-invalid": 400,
        "integer-invalid": 500 if upstream else 400,
        "integer-overflow": 500 if upstream else 400,
        "json-cycle": 200 if upstream else 500,
        "post-json-invalid-number": 200 if upstream else 400,
        "post-json-trailing-garbage": 200 if upstream else 400,
        "post-json-null": 400 if upstream else 200,
    }
    for name, value in cases.items():
        if name.startswith("http/") and isinstance(value, dict):
            expected = statuses.get(name.rsplit("/", 1)[-1], 200)
            if value["status"] != expected:
                errors.append(f"{name}: expected status {expected}, got {value['status']}")
        if name.startswith("websocket/"):
            _, _, binary, _ = name.split("/")
            payload = bytes([0, 1, 127, 128, 255]) if binary == "True" else "caf\u00e9 \U0001f600".encode("utf-8")
            if value != {"type": "Binary" if binary == "True" else "Text", "payload": base64.b64encode(payload).decode("ascii")}:
                errors.append(f"{name}: echo did not preserve message type and bytes")
        if name.startswith("lifecycle/") or name == "https/cancel":
            if value != "Stopped":
                errors.append(f"{name}: listener did not stop")
    for mode in ("EmbedIO",):
        if f"http/{mode}/dto" not in cases:
            continue
        for phase, expected in (("first", "1"), ("second", "2")):
            body = cases[f"http/{mode}/session-{phase}"]["body"]
            if base64.b64decode(body).decode("utf-8-sig") != expected:
                errors.append(f"{mode}: session counter {phase} was incorrect")
        if cases[f"http/{mode}/session-independent"] != "1":
            errors.append(f"{mode}: independent client inherited another session")
        if cases[f"http/{mode}/dto"]["body"] != {"Id": 42, "Name": "ordinary", "Amount": 12.5}:
            errors.append(f"{mode}: ordinary DTO changed")
    for name, expected in (("pinned-certificate", "encrypted"), ("untrusted-rejected", True),
                           ("hostname-rejected", True), ("healthy-after-rejections", "encrypted")):
        if cases[f"https/{name}"] != expected:
            errors.append(f"https/{name}: incorrect TLS outcome")
    return errors


def target_api_differences(modern, standard):
    # Compare exact entries by type so an approved additive convenience never
    # permits unrelated missing types, overloads, interfaces or defaults.
    observed = {}
    for name in sorted(modern.keys() | standard.keys()):
        neo_only = sorted(set(modern.get(name, [])) - set(standard.get(name, [])))
        standard_only = sorted(set(standard.get(name, [])) - set(modern.get(name, [])))
        if neo_only or standard_only:
            observed[name] = {"neoOnly": neo_only, "neoStandardOnly": standard_only}
    return observed


def compare(reports, contract):
    upstream = reports["Upstream"]
    errors = []
    details = {}
    expected_names = set(contract["caseNames"])
    for variant, report in reports.items():
        if set(report["cases"]) != expected_names:
            errors.append(f"{variant}: case manifest changed; review additions/removals explicitly")
        else:
            errors.extend(f"{variant}: {error}" for error in validate_outcomes(report, variant == "Upstream", contract))
    for variant in ("Neo", "NeoStandard"):
        report = reports[variant]
        observed = differences(normalized(upstream["cases"]), normalized(report["cases"]))
        expected = contract["differences"]
        matches = len(observed) == len(expected) and all(
            actual["path"] == entry["path"] and not differences(actual["neo"], entry["neo"])
            and (not differences(actual["upstream"], entry["upstream"])
                 or any(not differences(actual["upstream"], alternative) for alternative in entry.get("upstreamAlternatives", [])))
            for actual, entry in zip(observed, expected)
        )
        if not matches:
            errors.append(f"{variant}: unexpected or stale behavioral difference")
        removed = {name: sorted(set(entries) - set(report["api"].get(name, [])))
                   for name, entries in upstream["api"].items()
                   if set(entries) - set(report["api"].get(name, []))}
        expected_removed = {entry["type"]: entry["entries"] for entry in contract["apiChanges"]}
        if removed != expected_removed:
            errors.append(f"{variant}: unexpected or stale API removal/change")
        details[variant] = {"behavioralDifferences": observed, "removedApiEntries": removed,
                            "addedApiEntries": {name: sorted(set(entries) - set(upstream["api"].get(name, [])))
                                                for name, entries in report["api"].items()
                                                if set(entries) - set(upstream["api"].get(name, []))}}
    if reports["Neo"]["cases"] != reports["NeoStandard"]["cases"]:
        errors.append("Neo target assets differ in consumer behavior")
    target_differences = target_api_differences(reports["Neo"]["api"], reports["NeoStandard"]["api"])
    reviewed_targets = contract.get("targetApiDifferences", [])
    expected_targets = {entry["type"]: {"neoOnly": entry["neoOnly"], "neoStandardOnly": entry["neoStandardOnly"]}
                        for entry in reviewed_targets}
    if target_differences != expected_targets or len(expected_targets) != len(reviewed_targets):
        errors.append("Neo target assets have an unexpected or stale inventoried API difference")
    details["targetApiDifferences"] = target_differences
    return {"caseCount": len(expected_names), "comparisons": len(expected_names) * 2,
            "errors": errors, "details": details,
            "implementations": {variant: {key: report[key] for key in report if key not in ("cases", "api")}
                                for variant, report in reports.items()}}


def verify_guards(reports, contract):
    """Prove the comparator rejects regressions rather than just accepting its inputs."""
    mutations = (
        lambda sample: sample["Neo"]["cases"]["http/EmbedIO/dto"].update(status=500),
        lambda sample: sample["Neo"]["cases"]["http/EmbedIO/dto"].update(body={"Id": 0}),
        lambda sample: sample["Neo"]["cases"]["utility/valid/3"].update(value=1),
        lambda sample: sample["Neo"]["cases"].pop("utility/valid/0"),
        lambda sample: sample["Neo"]["api"].pop("EmbedIO.WebServer"),
        lambda sample: sample["NeoStandard"]["cases"]["websocket/EmbedIO/True/False"].update(payload="AA=="),
    )
    for mutation in mutations:
        sample = copy.deepcopy(reports)
        mutation(sample)
        if not compare(sample, contract)["errors"]:
            raise RuntimeError("Comparator accepted a deliberately introduced regression")
    stale = copy.deepcopy(contract)
    stale["differences"].pop()
    if not compare(reports, stale)["errors"]:
        raise RuntimeError("Comparator accepted an unreviewed difference")
    target_checks = 0
    if contract.get("targetApiDifferences"):
        target_mutations = (
            lambda sample: sample["Neo"]["api"]["EmbedIO.HttpTunnel"].remove("interface: System.IAsyncDisposable"),
            lambda sample: sample["NeoStandard"]["api"]["EmbedIO.HttpTunnel"].append("Method: unreviewed target-only overload"),
            lambda sample: sample["Neo"]["api"].update({"Unreviewed.TargetOnlyType": ["base: System.Object"]}),
        )
        for mutation in target_mutations:
            sample = copy.deepcopy(reports)
            mutation(sample)
            if not compare(sample, contract)["errors"]:
                raise RuntimeError("Comparator accepted an unreviewed target API change")
        stale_targets = copy.deepcopy(contract)
        stale_targets["targetApiDifferences"].pop()
        if not compare(reports, stale_targets)["errors"]:
            raise RuntimeError("Comparator accepted a missing target API approval")
        target_checks = len(target_mutations) + 1
    return len(mutations) + 1 + target_checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", type=Path, default=ROOT / "TestResults" / "parity-audit")
    parser.add_argument("--compare-only", action="store_true")
    args = parser.parse_args()
    results = args.results.resolve()
    results.mkdir(parents=True, exist_ok=True)
    reports = {}
    for variant in ("Upstream", "Neo", "NeoStandard"):
        project = FIXTURE / variant / (variant + ".csproj")
        if not args.compare_only:
            commands = [
                ["dotnet", "restore", str(project), "--locked-mode"],
                ["dotnet", "list", str(project), "package", "--no-restore", "--vulnerable", "--include-transitive", "--format", "json"],
                [sys.executable, str(ROOT / "scripts" / "security" / "nuget_audit.py"), str(results / f"{variant}-audit.json")],
                ["dotnet", "build", str(project), "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false"],
                ["dotnet", str(project.parent / "bin" / "Release" / "net10.0" / (variant + ".dll")), str(results / (variant + ".json"))],
            ]
            for stage, command in zip(("restore", "audit", "audit-check", "build", "run"), commands):
                extension = "json" if stage == "audit" else "log"
                with (results / f"{variant}-{stage}.{extension}").open("w", encoding="utf-8") as log:
                    subprocess.run(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=180)
        reports[variant] = read(results / (variant + ".json"))
    expected_frameworks = {"Upstream": ".NETStandard,Version=v2.0", "Neo": ".NETCoreApp,Version=v10.0", "NeoStandard": ".NETStandard,Version=v2.0"}
    if any(report["targetFramework"] != expected_frameworks[variant] for variant, report in reports.items()):
        raise RuntimeError("Incorrect assembly target was tested")
    if not reports["Upstream"]["assembly"].startswith("EmbedIO, Version=3.5.2.0,"):
        raise RuntimeError("Incorrect upstream assembly was tested")
    contract = read(FIXTURE / "reviewed-differences.json")
    profile = reports["Upstream"]["profile"]
    if any(report["profile"] != profile for report in reports.values()):
        raise RuntimeError("Inconsistent comparison profiles")
    if reports["Upstream"]["assemblySha256"] != contract["baseline"]["assemblySha256"]:
        raise RuntimeError("Upstream assembly bytes do not match the pinned baseline")
    contract = {**contract, **contract["profiles"][profile]}
    summary = compare(reports, contract)
    summary["profile"] = profile
    summary["comparatorNegativeChecks"] = verify_guards(reports, contract)
    summary["sourceCommit"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    summary["workingTree"] = subprocess.check_output(["git", "status", "--short"], cwd=ROOT, text=True)
    (results / "summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=True), encoding="utf-8")
    print(f"{summary['caseCount']} cases; {summary['comparisons']} upstream/Neo comparisons; {len(summary['errors'])} errors")
    for error in summary["errors"]:
        print(error)
    return 1 if summary["errors"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
