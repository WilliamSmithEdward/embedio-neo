"""Report every load sample's exception phases without inferring fault ownership.

The denominator is client connections opened, not a server close census. All
successful workers dispose their connections before DONE. Runtime first-chance
counts include rethrows and are not counts of distinct errors. Diagnostic stacks
stay in the server stderr files; profile runs must not be used for timing claims.
"""
import argparse
import json
from pathlib import Path

from compare_load_benchmark import valid_sample


def difference(after, before):
    if not isinstance(after, dict) or not isinstance(before, dict):
        raise ValueError("missing exception phase snapshot")
    for counts in (before, after):
        if any(not isinstance(n, int) or isinstance(n, bool) or n < 0 for n in counts.values()):
            raise ValueError("invalid exception count")
    delta = {key: after.get(key, 0) - before.get(key, 0) for key in before.keys() | after.keys()}
    if any(n < 0 for n in delta.values()):
        raise ValueError("exception counter decreased")
    return {key: n for key, n in sorted(delta.items()) if n}


def phases(sample):
    stops = sample.get("serverStop") or {}
    points = [sample.get(key) for key in (
        "exceptionsAtReady", "exceptionsBeforeLoad", "exceptionsAfterLoad", "exceptionsBeforeStop")]
    points.append(stops.get("firstChanceExceptions"))
    return {name: difference(after, before) for name, before, after in zip(
        ("warmup-and-settle", "load-and-client-close", "post-load-idle", "listener-stop"), points, points[1:])}


def rate(count, denominator):
    # Missing/zero denominators are unavailable, never fabricated as zero.
    return f"{count / denominator:.6f}" if isinstance(denominator, int) and not isinstance(denominator, bool) and denominator > 0 else "unavailable"


def complete_sample(sample):
    client = sample.get("client") or {}
    return (valid_sample(sample) and not client.get("error")
            and sample.get("clientExitCode") == 0 and sample.get("serverExitCode") == 0
            and sample.get("clientKilled") is False and sample.get("serverKilled") is False
            and (sample.get("derived") or {}).get("openServerSocketsAfter") == 0)


def summarize(root):
    root = Path(root)
    failed = False
    paths = sorted(root.glob("samples/*/*.json"))
    if not paths:
        print(f"{root}: no samples")
        return False
    marked = any((root / marker).exists() for marker in ("INVALID.txt", "ABORTED.txt"))
    for path in paths:
        sample = json.loads(path.read_text(encoding="utf-8"))
        error = sample.get("error")
        if error or marked or not complete_sample(sample):
            print(f"{path}: FAILED/INVALID: {error or 'run marker or incomplete core metrics'}")
            failed = True
            continue
        try:
            counts = phases(sample)
        except ValueError as error:
            print(f"{path}: UNAVAILABLE: {error}")
            failed = True
            continue
        client = sample["client"]
        warm = sample.get("warmup") or {}
        print(f"{path}: requests={client.get('requests')} connections-opened={client.get('connectionsOpened')}")
        for phase, types in counts.items():
            total = sum(types.values())
            print(f"  {phase}: {total} throws {json.dumps(types, sort_keys=True)}")
            if phase in ("warmup-and-settle", "load-and-client-close"):
                report = warm if phase == "warmup-and-settle" else client
                print(f"    throws/1000-completed={rate(1000 * total, report.get('requests'))}; "
                      f"throws/opened-connection={rate(total, report.get('connectionsOpened'))}")
        print(f"  metrics: {json.dumps(sample.get('derived'), sort_keys=True)}")
    return not failed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path, nargs="+")
    args = parser.parse_args()
    # Evaluate every directory, including failed attempts.
    results = [summarize(root) for root in args.results]
    return 0 if all(results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
