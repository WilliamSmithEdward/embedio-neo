"""Summarize an EmbedIO.LoadBenchmark endurance run.

Reads steps.jsonl, server-samples.jsonl and clients/*.report.json|intervals.jsonl from
one output directory and writes endurance-summary.md next to them. Retention is
judged only from forced-GC snapshots taken after traffic stopped and in-flight work
reached zero (quiesced snapshots), so warm-up and pooling are separated from growth
by comparing early and late quiesced values and fitting a least-squares slope.
"""
import argparse
import json
import statistics
from datetime import datetime
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path)
parser.add_argument("--warmup-snapshots", type=int, default=2, help="quiesced snapshots excluded from the slope as warm-up")
parser.add_argument("--series-label", help="quiesced snapshot label for the like-for-like series (default: the most frequent)")
args = parser.parse_args()
root = args.output.resolve()
for marker in ("INVALID.txt", "ABORTED.txt"):
    if (root / marker).exists():
        parser.error(f"Rejected endurance evidence: {root / marker}")


def lines(path):
    with path.open(encoding="utf-8") as stream:
        return [json.loads(line) for line in stream if line.strip()]


def utc(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00"))


steps = lines(root / "steps.jsonl")
samples = lines(root / "server-samples.jsonl")
environment = json.loads((root / "environment.json").read_text(encoding="utf-8"))
start = utc(samples[0]["utc"]) if samples else None


def hours(text):
    return (utc(text) - start).total_seconds() / 3600 if start else 0


def quiesced_points():
    """Every forced snapshot after quiescence, in order, with its step label."""
    points = []
    for step in steps:
        if "index" not in step:
            continue
        holders = [step] + list(step.get("cycles", []))
        for holder in holders:
            snapshot = holder.get("quiesced")
            if snapshot and not holder.get("quiesceStuck") and snapshot.get("inFlightRequests") == 0 and snapshot.get("webSocketsActive") == 0:
                label = step["step"] + (f" c{holder['cycle']}" if "cycle" in holder else "")
                points.append((label, snapshot, holder))
    return points


def slope(xs, ys):
    if len(xs) < 3:
        return None
    mx, my = statistics.fmean(xs), statistics.fmean(ys)
    denominator = sum((x - mx) ** 2 for x in xs)
    return None if denominator == 0 else sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denominator


def nested(snapshot, *path):
    """A value inside a snapshot, or None when the snapshot does not record it."""
    for key in path:
        if not isinstance(snapshot, dict) or snapshot.get(key) is None:
            return None
        snapshot = snapshot[key]
    return snapshot


def added(*values):
    return None if any(value is None for value in values) else sum(values)


def shown(value, scale, signed=False):
    if value is None:
        return "-"
    sign = "+" if signed else ""
    return f"{value:{sign},}" if scale == 1 else f"{value / scale:{sign},.1f}"


# Native figures that quiesced snapshots record where the platform provides them: Linux
# resident memory and glibc malloc totals, Windows heap totals and the handle census.
NATIVE = [
    ("Resident anonymous MiB", lambda s: nested(s, "linuxMemory", "status", "RssAnon"), 2**20),
    ("Resident file-backed MiB", lambda s: nested(s, "linuxMemory", "status", "RssFile"), 2**20),
    ("Private dirty MiB", lambda s: nested(s, "linuxMemory", "smapsRollup", "Private_Dirty"), 2**20),
    ("malloc in use MiB", lambda s: added(nested(s, "linuxMemory", "malloc", "allocatedBytes"), nested(s, "linuxMemory", "malloc", "mmappedBytes")), 2**20),
    ("malloc free MiB", lambda s: nested(s, "linuxMemory", "malloc", "freeBytes"), 2**20),
    ("Win32 heaps allocated MiB", lambda s: nested(s, "nativeHeaps", "allocatedBytes"), 2**20),
    ("Win32 heaps committed MiB", lambda s: nested(s, "nativeHeaps", "committedBytes"), 2**20),
    ("Handles in census", lambda s: nested(s, "handleTypes", "handles"), 1),
]


out = []
out.append(f"# Endurance summary: {root.name}\n")
out.append(f"- Revision: `{environment.get('revision')}`; runner SHA-256 `{environment.get('runnerSha256')}`; core SHA-256 `{environment.get('embedioSha256')}`")
out.append(f"- Runtime: {environment.get('runtime')}; OS: {environment.get('os')}; CPU: {environment.get('processor')} ({environment.get('logicalProcessors')} logical)")
out.append(f"- Physical memory at start: {environment.get('totalPhysicalBytes', 0) / 2**30:.1f} GiB total, {environment.get('availablePhysicalBytes', 0) / 2**30:.1f} GiB available")
out.append(f"- Server CPUs {environment.get('serverCpus')}, client CPUs {environment.get('clientCpus')}, rate scale {environment.get('rateScale')}, settle {environment.get('settleSeconds')} s, drain timeout {environment.get('drainTimeoutSeconds')} s")
if samples:
    out.append(f"- Server samples: {len(samples)} over {(utc(samples[-1]['utc']) - start).total_seconds() / 3600:.2f} h")
out.append("")

# Steps
out.append("## Steps\n")
out.append("| # | Step | Result | Seconds | Requests ok | Client errors | Disrupted | Client aborts | Health | Quiesce ms | Stale-instance requests |")
out.append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
first_errors = []
for step in steps:
    if "index" not in step:
        continue
    holders = [step] + list(step.get("cycles", []))
    ok = errors = disrupted = aborts = 0
    health = []
    quiesce = []
    stale = 0
    for holder in holders:
        client = holder.get("client")
        if client:
            for workload in client["workloads"]:
                ok += workload["ok"]
                errors += workload["errors"]
                disrupted += workload["disrupted"]
                aborts += workload["clientAborts"]
                for error in workload["firstErrors"]:
                    first_errors.append((step["step"], holder.get("cycle"), workload["name"], error))
        if holder.get("health"):
            health.append("ok" if holder["health"]["errors"] == 0 else f"{holder['health']['errors']} errors")
        if "quiesceMilliseconds" in holder:
            quiesce.append(holder["quiesceMilliseconds"])
        if holder.get("quiesced"):
            stale = max(stale, holder["quiesced"].get("staleGenerationRequests", 0))
    result = "FAILED" if step.get("failed") else "ok"
    if step.get("error"):
        result += f" ({step['error']})"
    out.append(f"| {step['index']} | `{step['step']}` | {result} | {step.get('seconds', 0):.0f} | {ok:,} | {errors} | {disrupted} | {aborts} | {', '.join(health) or '-'} | {max(quiesce) if quiesce else 0:.0f} | {stale} |")
out.append("")

if first_errors:
    out.append("## First errors (first 20 per workload, in order)\n")
    for step, cycle, name, error in first_errors[:60]:
        out.append(f"- `{step}`{f' cycle {cycle}' if cycle else ''} `{name}` at {error['atSeconds']:.1f} s ({error['utc']}): {error['error']}")
    out.append("")

# Quiesced retention
points = quiesced_points()
metrics = [
    ("managedHeapBytes", "Managed heap KiB", 1024),
    ("gcCommittedBytes", "GC committed MiB", 2**20),
    ("workingSetBytes", "Working set MiB", 2**20),
    ("privateBytes", "Private MiB", 2**20),
    ("handles", "Handles", 1),
    ("threads", "Threads", 1),
    ("threadPoolThreads", "Pool threads", 1),
    ("timers", "Timers", 1),
]
out.append("## Quiesced snapshots (forced GC, zero in-flight)\n")
out.append("Snapshots with incomplete quiescence or missing activity counters are excluded from retention trends.\n")
out.append("| Hour | After | " + " | ".join(label for _, label, _ in metrics) + " | Server TCP | Available GiB |")
out.append("| --- | --- | " + " | ".join("---" for _ in metrics) + " | --- | --- |")
for label, snapshot, _ in points:
    tcp = ", ".join(f"{key} {value}" for key, value in sorted(snapshot.get("serverTcp", {}).items())) or "none"
    values = " | ".join(f"{snapshot[key] / scale:,.1f}" if scale != 1 else f"{snapshot[key]:,}" for key, _, scale in metrics)
    out.append(f"| {hours(snapshot['utc']):.2f} | `{label}` | {values} | {tcp} | {snapshot.get('availablePhysicalBytes', 0) / 2**30:.1f} |")
out.append("")

usable = points[args.warmup_snapshots:]
if len(usable) >= 3:
    out.append(f"### Retention trend (excluding the first {args.warmup_snapshots} quiesced snapshots as warm-up)\n")
    out.append("| Metric | First | Min | Max | Last | Last - first | Least-squares slope per hour |")
    out.append("| --- | --- | --- | --- | --- | --- | --- |")
    xs = [hours(snapshot["utc"]) for _, snapshot, _ in usable]
    for key, label, scale in metrics:
        ys = [snapshot[key] / scale for _, snapshot, _ in usable]
        per_hour = slope(xs, ys)
        out.append(f"| {label} | {ys[0]:,.1f} | {min(ys):,.1f} | {max(ys):,.1f} | {ys[-1]:,.1f} | {ys[-1] - ys[0]:+,.1f} | {'-' if per_hour is None else f'{per_hour:+,.2f}'} |")
    out.append("")

# Native memory and handles, where the snapshots record them
native = [(label, read, scale) for label, read, scale in NATIVE if any(read(snapshot) is not None for _, snapshot, _ in points)]
if native:
    out.append("## Native memory and handles (quiesced snapshots)\n")
    out.append("Linux figures come from /proc/self/status, smaps_rollup and glibc's mallinfo2; malloc in use includes "
               "separately mapped blocks. Windows figures come from HeapSummary and a snapshot of the handle table.\n")
    out.append("| Hour | After | " + " | ".join(label for label, _, _ in native) + " |")
    out.append("| --- | --- | " + " | ".join("---" for _ in native) + " |")
    for label, snapshot, _ in points:
        out.append(f"| {hours(snapshot['utc']):.2f} | `{label}` | " + " | ".join(shown(read(snapshot), scale) for _, read, scale in native) + " |")
    out.append("")
    if len(usable) >= 3:
        out.append(f"### Native retention trend (excluding the first {args.warmup_snapshots} quiesced snapshots as warm-up)\n")
        out.append("| Metric | First | Min | Max | Last | Last - first | Least-squares slope per hour |")
        out.append("| --- | --- | --- | --- | --- | --- | --- |")
        for label, read, scale in native:
            rows = [(hours(snapshot["utc"]), read(snapshot) / scale) for _, snapshot, _ in usable if read(snapshot) is not None]
            if len(rows) < 2:
                continue
            ys = [y for _, y in rows]
            per_hour = slope([x for x, _ in rows], ys)
            out.append(f"| {label} | {ys[0]:,.1f} | {min(ys):,.1f} | {max(ys):,.1f} | {ys[-1]:,.1f} | {ys[-1] - ys[0]:+,.1f} | {'-' if per_hour is None else f'{per_hour:+,.2f}'} |")
        out.append("")

# Like-for-like series: the snapshots taken at the same point of every cycle, so each
# change between neighbours is one cycle's net retention. By default the most frequent
# label wins, ties going to the one taken last in a cycle.
occurrences = {}
for position, (label, _, _) in enumerate(usable):
    count, _ = occurrences.get(label, (0, 0))
    occurrences[label] = (count + 1, position)
series_label = args.series_label or (max(occurrences, key=lambda name: occurrences[name]) if occurrences else None)
series = [snapshot for label, snapshot, _ in usable if label == series_label]
if len(series) >= 3:
    out.append(f"## Like-for-like series: every `{series_label}` snapshot\n")
    out.append("| Metric | Values | Change from the previous | Last - first |")
    out.append("| --- | --- | --- | --- |")
    for label, read, scale in [(label, lambda s, key=key: s.get(key), scale) for key, label, scale in metrics] + native:
        values = [read(snapshot) for snapshot in series]
        if any(value is None for value in values):
            continue
        changes = [later - earlier for earlier, later in zip(values, values[1:])]
        out.append(f"| {label} | {', '.join(shown(value, scale) for value in values)} | "
                   f"{', '.join(shown(change, scale, signed=True) for change in changes)} | {shown(values[-1] - values[0], scale, signed=True)} |")
    out.append("")

# Handles by type and default-heap block sizes: first against last snapshot of the
# like-for-like series when it records them, otherwise of all usable snapshots.
compared = series if len(series) >= 2 else [snapshot for _, snapshot, _ in usable]
census = [snapshot for snapshot in compared if nested(snapshot, "handleTypes", "types")]
if len(census) >= 2:
    first, last = census[0]["handleTypes"]["types"], census[-1]["handleTypes"]["types"]
    names = sorted(set(first) | set(last), key=lambda name: (-abs(last.get(name, 0) - first.get(name, 0)), name))
    out.append(f"## Handles by type, {hours(census[0]['utc']):.2f} h to {hours(census[-1]['utc']):.2f} h\n")
    out.append("| Type | First | Last | Change |")
    out.append("| --- | --- | --- | --- |")
    for name in names:
        out.append(f"| {name} | {first.get(name, 0)} | {last.get(name, 0)} | {last.get(name, 0) - first.get(name, 0):+} |")
    out.append("")
heaps = [snapshot for snapshot in compared if nested(snapshot, "nativeHeaps", "defaultHeapBlocks")]
if len(heaps) >= 2:
    first = {size: count for size, count in heaps[0]["nativeHeaps"]["defaultHeapBlocks"]}
    last = {size: count for size, count in heaps[-1]["nativeHeaps"]["defaultHeapBlocks"]}
    grown = sorted(((size, first.get(size, 0), count) for size, count in last.items() if count > first.get(size, 0)),
                   key=lambda row: (-row[0] * (row[2] - row[1]), row[0]))[:15]
    if grown:
        out.append(f"## Default-heap block sizes that grew, {hours(heaps[0]['utc']):.2f} h to {hours(heaps[-1]['utc']):.2f} h\n")
        out.append("Each snapshot lists only the 80 sizes with the largest totals, so a size missing from the first reads as 0 there.\n")
        out.append("| Block bytes | First | Last | Change | Change bytes |")
        out.append("| --- | --- | --- | --- | --- |")
        for size, before, after in grown:
            out.append(f"| {size:,} | {before:,} | {after:,} | {after - before:+,} | {size * (after - before):+,} |")
        out.append("")

# Drain and restart
drains = []
for step in steps:
    for holder in step.get("cycles", []):
        if holder.get("drain"):
            drains.append((step["step"], holder["cycle"], holder["drain"], holder.get("restart", {})))
if drains:
    out.append("## Drain and restart\n")
    out.append("| Step | Cycle | In flight at drain | In flight after | Drain total ms | Per listener (drain ms, error) | Restart ms |")
    out.append("| --- | --- | --- | --- | --- | --- | --- |")
    for name, cycle, drain, restart in drains:
        listeners = "; ".join(f"{entry['listener']}: {entry['drainMilliseconds']:.1f}{', ' + entry['error'] if entry.get('error') else ''}{', run ' + entry['runError'] if entry.get('runError') else ''}" for entry in drain["listeners"])
        out.append(f"| `{name}` | {cycle} | {drain['inFlightAtStart']} | {drain['inFlightAfterDrain']} | {drain['totalMilliseconds']:.1f} | {listeners} | {restart.get('startMilliseconds', 0):.1f} |")
    out.append("")

# Server activity per load window: exact deltas between the samples taken when the
# client started and when it stopped, so quiesce GCs and health checks are excluded.
out.append("## Server activity during load (between load start and stop samples)\n")
out.append("| Step | Requests/s | CPU cores | Alloc MB/s | Alloc KB/request | Gen0/1/2 per min | GC pause % | Max in-flight | Max pool pending | Max pool threads | Max threads | Max handles | Max working set MiB | First-chance exceptions per 1k requests |")
out.append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
exception_rows = []
for step in steps:
    if "index" not in step:
        continue
    for holder in [step] + list(step.get("cycles", [])):
        first, last = holder.get("serverBefore"), holder.get("serverAtStop")
        if not first or not last:
            continue
        seconds = (utc(last["utc"]) - utc(first["utc"])).total_seconds() or 1
        window = [sample for sample in samples if utc(first["utc"]) <= utc(sample["utc"]) <= utc(last["utc"])] or [first, last]
        requests = last["requests"] - first["requests"]
        collections = [(last["gcCollections"][index] - first["gcCollections"][index]) * 60 / seconds for index in range(3)]
        pause = (last["gcPauseMilliseconds"] - first["gcPauseMilliseconds"]) / (seconds * 10)
        thrown = {name: count - first["firstChanceExceptions"].get(name, 0) for name, count in last["firstChanceExceptions"].items()}
        thrown = {name: count for name, count in thrown.items() if count}
        label = step["step"] + (f" c{holder['cycle']}" if "cycle" in holder else "")
        exception_rows.append((label, requests, thrown))
        out.append(
            f"| `{label}` | {requests / seconds:,.0f} | {(last['cpuSeconds'] - first['cpuSeconds']) / seconds:.2f} | "
            f"{(last['allocatedBytes'] - first['allocatedBytes']) / seconds / 1e6:,.1f} | {(last['allocatedBytes'] - first['allocatedBytes']) / max(1, requests) / 1000:,.1f} | "
            f"{collections[0]:.1f}/{collections[1]:.1f}/{collections[2]:.2f} | {pause:.2f} | "
            f"{max(s['inFlightRequests'] for s in window)} | {max(s['threadPoolPending'] for s in window)} | {max(s['threadPoolThreads'] for s in window)} | "
            f"{max(s['threads'] for s in window)} | {max(s['handles'] for s in window)} | {max(s['workingSetBytes'] for s in window) / 2**20:,.0f} | "
            f"{sum(thrown.values()) * 1000 / max(1, requests):.1f} |")
out.append("")
if exception_rows:
    out.append("### First-chance exceptions by load window\n")
    for label, requests, thrown in exception_rows:
        detail = ", ".join(f"{name.split('.')[-1]} {count:,}" for name, count in sorted(thrown.items(), key=lambda pair: -pair[1])) or "none"
        out.append(f"- `{label}` ({requests:,} requests): {detail}")
    out.append("")

# Background load
if len(samples) >= 2:
    busy = []
    for previous, current in zip(samples, samples[1:]):
        seconds = (utc(current["utc"]) - utc(previous["utc"])).total_seconds()
        if seconds > 0:
            busy.append(((current["machineBusyCpuSeconds"] - previous["machineBusyCpuSeconds"]) - (current["cpuSeconds"] - previous["cpuSeconds"])) / seconds)
    out.append(f"Machine busy CPU excluding the server (client plus background), cores: median {statistics.median(busy):.2f}, max {max(busy):.2f}. "
               f"Available physical memory ranged {min(s['availablePhysicalBytes'] for s in samples) / 2**30:.1f}-{max(s['availablePhysicalBytes'] for s in samples) / 2**30:.1f} GiB.\n")

# First-chance exceptions
final = samples[-1] if samples else {}
if final.get("firstChanceExceptions"):
    out.append("## Server first-chance exceptions (cumulative)\n")
    for name, count in sorted(final["firstChanceExceptions"].items(), key=lambda pair: -pair[1]):
        out.append(f"- {name}: {count:,}")
    out.append("")

# Latency trend of the steady small-request workloads
interval_files = sorted((root / "clients").glob("*.intervals.jsonl"))
trend = {}
for path in interval_files:
    if "health" in path.name:
        continue
    for line in lines(path):
        for name, values in line["workloads"].items():
            if name.startswith("small-") and values["ok"]:
                trend.setdefault(name, []).append((line["utc"], values["p99"], values["max"], values["ok"]))
if trend:
    out.append("## Small-request p99 trend (per client interval)\n")
    out.append("| Workload | Intervals | p99 median ms | p99 first quarter median | p99 last quarter median | Worst p99 | Worst max |")
    out.append("| --- | --- | --- | --- | --- | --- | --- |")
    for name, rows in sorted(trend.items()):
        rows.sort(key=lambda row: utc(row[0]))
        p99 = [row[1] for row in rows]
        quarter = max(1, len(p99) // 4)
        out.append(f"| {name} | {len(rows)} | {statistics.median(p99):.2f} | {statistics.median(p99[:quarter]):.2f} | {statistics.median(p99[-quarter:]):.2f} | {max(p99):.1f} | {max(row[2] for row in rows):.1f} |")
    out.append("")

(root / "endurance-summary.md").write_text("\n".join(out) + "\n", encoding="utf-8", newline="\n")
print("\n".join(out))
