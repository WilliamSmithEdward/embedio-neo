"""Summarize load-benchmark samples and compare two engines per scenario.

usage: python -I scripts/summarize_load_benchmark.py <results-dir> [<results-dir> ...]
           [--compare baseline candidate] [--markdown <file>]

Every sample is printed, failed and invalid ones included, so nothing is
hidden. Per-engine medians use accepted samples only. A directory containing
INVALID.txt or ABORTED.txt is listed but contributes no accepted samples.
With --compare, a table of candidate / baseline ratios per scenario follows:
requests per second, server CPU per request, allocated bytes per request and
client p50/p99 latency.
"""
import argparse
import json
import math
from pathlib import Path
import statistics
import sys

from compare_load_benchmark import valid_sample

METRICS = (("rps", 0), ("cpu", 1), ("alloc", 0), ("p50", 3), ("p99", 3), ("p999", 3), ("workitems", 2), ("contentions", 0))


def load(roots):
    rows = {}
    invalid = []
    for root in roots:
        root = Path(root)
        markers = [m for m in (root / "INVALID.txt", root / "ABORTED.txt") if m.exists()]
        if markers:
            invalid.append((root, markers[0].read_text(encoding="utf-8", errors="replace").strip()))
        for path in sorted(root.glob("samples/*/*.json")):
            sample = json.loads(path.read_text(encoding="utf-8"))
            derived = sample.get("derived") or {}
            client = sample.get("client") or {}
            window = (sample.get("serverWindow") or {}).get("window") or {}
            requests = client.get("requests")
            seconds = window.get("elapsedSeconds") or 1
            workitems = window.get("threadPoolWorkItems")
            error = sample.get("error")
            if error is None and not valid_sample(sample):
                error = "incomplete or invalid core metrics"
            if markers:
                error = (error + "; " if error else "") + "run marked " + markers[0].name
            rows.setdefault((sample["scenario"], sample["engine"]), []).append({
                "source": str(path),
                "round": sample.get("round"),
                "error": error,
                "rps": derived.get("requestsPerSecond"),
                "cpu": derived.get("serverCpuMicrosecondsPerRequest"),
                "alloc": derived.get("serverAllocatedBytesPerRequest"),
                "p50": client.get("p50Milliseconds"),
                "p99": client.get("p99Milliseconds"),
                "p999": client.get("p999Milliseconds"),
                "clientcpu": derived.get("clientCpuUtilization"),
                "background": derived.get("backgroundCpuSeconds"),
                "heap": derived.get("retainedManagedHeapBytes"),
                "handles": derived.get("retainedHandles"),
                "threads": derived.get("retainedThreads"),
                "exceptions": window.get("firstChanceExceptions"),
                "contentions": None if window.get("lockContentions") is None else window["lockContentions"] / seconds,
                "workitems": None if not requests or workitems is None else workitems / requests,
                "requests": requests,
                "failedRequests": client.get("failedRequests"),
            })
    return rows, invalid


def fmt(value, digits=1):
    return "-" if value is None else f"{value:,.{digits}f}"


def median(samples, key):
    values = [s[key] for s in samples if not s["error"] and isinstance(s[key], (int, float))
              and not isinstance(s[key], bool) and math.isfinite(s[key]) and s[key] >= 0]
    return statistics.median(values) if values else None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("roots", nargs="+")
    parser.add_argument("--compare", nargs=2, metavar=("BASELINE", "CANDIDATE"))
    parser.add_argument("--markdown", type=Path, help="also write the comparison table to this file")
    args = parser.parse_args()
    rows, invalid = load(args.roots)
    for root, text in invalid:
        print(f"INVALID RUN {root}: {text}")
    for (scenario, engine), samples in sorted(rows.items()):
        print(f"\n## {scenario} / {engine}")
        print("round | rps | cpu us/req | alloc B/req | p50 ms | p99 ms | p99.9 ms | client cpu | background s | work items/req | contentions/s | exceptions | retained heap/handles/threads | error")
        for s in sorted(samples, key=lambda item: item["round"] or 0):
            print(f"{s['round']} | {fmt(s['rps'], 0)} | {fmt(s['cpu'])} | {fmt(s['alloc'], 0)} | {fmt(s['p50'], 3)} | {fmt(s['p99'], 3)} | {fmt(s['p999'], 3)} | "
                  f"{fmt(None if s['clientcpu'] is None else s['clientcpu'] * 100, 0)}% | {fmt(s['background'])} | {fmt(s['workitems'], 2)} | {fmt(s['contentions'], 0)} | "
                  f"{json.dumps(s['exceptions'], separators=(',', ':'))} | {s['heap']}/{s['handles']}/{s['threads']} | {s['error'] or ''}")
        accepted = [s for s in samples if not s["error"]]
        print("median | " + " | ".join(fmt(median(samples, key), digits) for key, digits in METRICS[:5]) + f" | accepted {len(accepted)}/{len(samples)}")

    if not args.compare:
        return 0
    base_name, cand_name = args.compare
    lines = ["| Scenario | Samples (base/cand) | req/s base -> cand (ratio) | CPU us/req (ratio) | alloc B/req (ratio) | p50 ms (ratio) | p99 ms (ratio) |",
             "| --- | --- | --- | --- | --- | --- | --- |"]
    scenarios = sorted({scenario for scenario, _ in rows})
    for scenario in scenarios:
        base = rows.get((scenario, base_name), [])
        cand = rows.get((scenario, cand_name), [])
        if not base or not cand:
            continue
        cells = []
        for key, digits in METRICS[:5]:
            b, c = median(base, key), median(cand, key)
            ratio = "-" if not b or c is None else f"{c / b:.3f}"
            cells.append(f"{fmt(b, digits)} -> {fmt(c, digits)} ({ratio})")
        accepted = f"{sum(1 for s in base if not s['error'])}/{len(base)} / {sum(1 for s in cand if not s['error'])}/{len(cand)}"
        lines.append(f"| {scenario} | {accepted} | " + " | ".join(cells) + " |")
    table = "\n".join(lines) + "\n"
    print(f"\n## {cand_name} relative to {base_name} (medians of accepted samples)\n")
    print(table)
    if args.markdown:
        args.markdown.write_text(table, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
