"""Summarize completed issue-279 TCP endurance evidence without hiding disruption errors."""

import argparse
from collections import Counter
import json
from pathlib import Path


def summarize(directory):
    reports = []
    connects = []
    incomplete = []
    workload_errors = 0
    for path in sorted((directory / "clients").glob("*.report.json")):
        report = json.loads(path.read_text(encoding="utf-8-sig"))
        reports.append(report)
    for path in sorted((directory / "clients").glob("*.stderr.log")):
        starts = set()
        ends = set()
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            if not line.startswith("{"):
                continue
            event = json.loads(line)
            kind = event.get("kind")
            if kind == "tcp-connect-start":
                starts.add(event["id"])
            elif kind == "tcp-connect-end":
                ends.add(event["id"])
                connects.append(dict(event, evidenceFile=path.name))
            elif kind == "workload-error":
                workload_errors += 1
        if starts != ends:
            incomplete.append(dict(file=path.name, startsWithoutEnds=sorted(starts - ends), endsWithoutStarts=sorted(ends - starts)))
    endpoint_states = {}
    endpoint_faults = []
    for line in (directory / "server.stderr.log").read_text(encoding="utf-8-sig").splitlines():
        if not line.startswith("{"):
            continue
        event = json.loads(line)
        if event.get("kind") == "tcp-endpoint":
            endpoint_states[event["id"]] = event
            if event.get("fault"):
                endpoint_faults.append(event)
    samples = [json.loads(line) for line in (directory / "server-samples.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    timeouts = [e for e in connects if "TimedOut" in e["outcome"] or "(10060)" in e["outcome"] or "(110)" in e["outcome"]]
    outcomes = Counter("connected" if e["outcome"] == "connected" else e["outcome"].splitlines()[0] for e in connects)
    steps = [json.loads(line) for line in (directory / "steps.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    completed = any(s.get("step") == "server-exit" and s.get("exitCode") == 0 for s in steps)
    disposed = any(s.get("step") == "server-dispose" and s.get("killed") is False for s in steps)

    def failures(value):
        if isinstance(value, dict):
            return value.get("failed") is True or any(failures(v) for v in value.values())
        if isinstance(value, list):
            return any(failures(v) for v in value)
        return False

    result = dict(
        completedWithCleanServerExit=completed and disposed,
        failedPlanStep=failures(steps),
        completedClientPhases=len(reports),
        completedMixedCycles=len(list((directory / "clients").glob("*mixed.report.json"))),
        validatedExchanges=sum(w["ok"] for r in reports for w in r["workloads"]),
        reportedClientErrors=sum(r["errors"] for r in reports),
        workersAlwaysFinished=all(r["workersDrainedWithin60Seconds"] for r in reports),
        recordedWorkloadErrorsIncludingDisruption=workload_errors,
        directHttp1TcpConnectCompletions=len(connects),
        tcpConnectOutcomes=dict(outcomes),
        longestConnectMilliseconds=max((e["elapsedMs"] for e in connects), default=0),
        tcpTimeouts=timeouts,
        endpointFaults=endpoint_faults,
        finalEndpointStates=list(endpoint_states.values()),
        finalAcceptWorkersCompleted=all(e["worker"] == "RanToCompletion" for e in endpoint_states.values()),
        openEndpointAtExit=any(not e["closed"] for e in endpoint_states.values()),
        incompleteConnectEvidence=incomplete,
        maximumStaleGenerationRequests=max((s["staleGenerationRequests"] for s in samples), default=0),
        finalGeneration=samples[-1]["generation"] if samples else None,
    )
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = summarize(args.directory)
    text = json.dumps(result, indent=2)
    if args.output:
        with args.output.open("x", encoding="utf-8") as output:
            output.write(text + "\n")
    print(text)
    # An unpaired trace, a hidden disruption timeout or a shutdown-bound violation
    # prevents a clean result even when the original harness reports zero errors.
    if not result["completedWithCleanServerExit"] or result["failedPlanStep"] or not result["completedClientPhases"] or result["tcpTimeouts"] or result["incompleteConnectEvidence"] or result["reportedClientErrors"] or result["endpointFaults"] or not result["finalEndpointStates"] or result["openEndpointAtExit"] or not result["finalAcceptWorkersCompleted"] or not result["workersAlwaysFinished"] or result["maximumStaleGenerationRequests"]:
        raise SystemExit(1)
