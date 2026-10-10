"""Compare engines in a load-benchmark result directory against an A/A noise band.

For every scenario, report each engine's median requests/s, server CPU per request
and server bytes per request relative to a reference engine. A ratio is marked
"beyond noise" only when it exceeds every one of: the largest median ratio seen
between two identical runners in the A/A directories (same scenario and metric),
the min-max spread of either engine's own samples, and a fixed floor (--floor,
default 3%). Medians and spreads use valid samples only; failed samples are counted.

usage:
  python -I scripts/compare_load_benchmark.py <comparison dir> --reference candidate \
      --aa <A/A dir> [--aa <A/A dir> ...] [--markdown out.md]
"""
import argparse
import json
from pathlib import Path
import statistics

METRICS = (
    ("requestsPerSecond", "req/s"),
    ("serverCpuMicrosecondsPerRequest", "CPU us/req"),
    ("serverAllocatedBytesPerRequest", "B/req"),
)


def samples(directory):
    rows = {}
    for path in sorted(Path(directory, "samples").glob("*/*.json")):
        sample = json.loads(path.read_text(encoding="utf-8"))
        engine = path.stem.rsplit("-r", 1)[0]
        key = (sample.get("scenario") or path.parent.name, engine)
        rows.setdefault(key, []).append(sample)
    return rows


def values(rows, metric):
    return [s["derived"][metric] for s in rows if s.get("error") is None and (s.get("derived") or {}).get(metric) is not None]


def median(rows, metric):
    found = values(rows, metric)
    return (statistics.median(found) if found else None), len(found)


def spread(rows, metric):
    found = [value for value in values(rows, metric) if value > 0]
    return max(found) / min(found) if len(found) > 1 else 1.0


def noise(aa_dirs):
    """Largest |log ratio| between the two identical engines per (scenario, metric)."""
    band = {}
    for directory in aa_dirs:
        rows = samples(directory)
        for scenario in {key[0] for key in rows}:
            a, b = rows.get((scenario, "candidate"), []), rows.get((scenario, "baseline"), [])
            for metric, _ in METRICS:
                ma, _ = median(a, metric)
                mb, _ = median(b, metric)
                if ma and mb:
                    ratio = max(ma / mb, mb / ma)
                    band[(scenario, metric)] = max(band.get((scenario, metric), 1.0), ratio)
    return band


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("comparison")
    parser.add_argument("--reference", default="candidate")
    parser.add_argument("--aa", action="append", default=[])
    parser.add_argument("--markdown", type=Path)
    parser.add_argument("--floor", type=float, default=0.03)
    args = parser.parse_args()
    rows = samples(args.comparison)
    band = noise(args.aa)
    scenarios = sorted({key[0] for key in rows}, key=lambda name: min(
        s.get("startedUtc", "") for key, group in rows.items() if key[0] == name for s in group))
    lines = ["| Scenario | Engine | Valid | " + " | ".join(f"{label} (vs {args.reference})" for _, label in METRICS) + " | A/A noise (req/s) |",
             "| --- | --- | --- | " + " | ".join("---" for _ in METRICS) + " | --- |"]
    for scenario in scenarios:
        reference = rows.get((scenario, args.reference), [])
        for engine in sorted({key[1] for key in rows if key[0] == scenario}, key=lambda name: (name != args.reference, name)):
            group = rows[(scenario, engine)]
            cells = []
            valid = 0
            for metric, _ in METRICS:
                value, valid = median(group, metric)
                ref, _ = median(reference, metric)
                if value is None:
                    cells.append("-")
                elif engine == args.reference or not ref:
                    cells.append(f"{value:,.0f}" if value >= 100 else f"{value:,.1f}")
                else:
                    ratio = value / ref
                    limit = max(band.get((scenario, metric), 1.0), spread(group, metric), spread(reference, metric), 1 + args.floor)
                    beyond = max(ratio, 1 / ratio) > limit
                    marker = f" **beyond ±{(limit - 1) * 100:.0f}%**" if beyond else f" (within ±{(limit - 1) * 100:.0f}%)"
                    cells.append(f"{value:,.0f} ({ratio:.2f}x){marker}" if value >= 100 else f"{value:,.1f} ({ratio:.2f}x){marker}")
            limit = band.get((scenario, "requestsPerSecond"))
            lines.append(f"| {scenario} | {engine} | {valid}/{len(group)} | " + " | ".join(cells)
                         + f" | {'-' if limit is None else f'±{(limit - 1) * 100:.1f}%'} |")
    text = "\n".join(lines) + "\n"
    if args.markdown:
        args.markdown.write_text(text, encoding="utf-8", newline="\n")
    print(text)


if __name__ == "__main__":
    main()
