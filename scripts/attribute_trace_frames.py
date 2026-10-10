"""Attribute sampled time in a frame to the nearest calling frames of interest.

Reads a Speedscope export from dotnet-trace and, for every sample whose leaf (or
any frame) matches --target, records the closest caller matching --caller. Used
to identify which locks or writes account for hot runtime frames in the load
benchmark traces.
"""
import argparse
from collections import Counter
import json
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("speedscope", type=Path)
parser.add_argument("--target", default="Monitor.Enter_Slowpath")
parser.add_argument("--caller", default="EmbedIO,LoadBenchmark,Http2,Http3,Quic,HttpListener,HttpConnection,WebServer")
parser.add_argument("--depth", type=int, default=3, help="number of matching callers to show per sample")
parser.add_argument("--top", type=int, default=15)
args = parser.parse_args()

document = json.loads(args.speedscope.read_text(encoding="utf-8"))
frames = [frame["name"] for frame in document["shared"]["frames"]]
callers = [token for token in args.caller.split(",") if token]
attributed = Counter()
target_samples = 0
total_samples = 0
def stacks(profile):
    """Yield (frame indexes root-first, weight) for sampled or evented profiles."""
    if profile.get("type") == "sampled":
        yield from zip(profile["samples"], profile["weights"])
        return
    # Evented: replay open/close events; time between events belongs to the open stack.
    stack, previous = [], None
    for event in profile.get("events", []):
        if previous is not None and stack and event["at"] > previous:
            yield list(stack), event["at"] - previous
        previous = event["at"]
        if event["type"] == "O":
            stack.append(event["frame"])
        elif stack:
            stack.pop()


for profile in document["profiles"]:
    for stack, weight in stacks(profile):
        names = [frames[index] for index in stack]
        # Idle thread-pool waits dominate wall-clock thread time; exclude them so
        # shares are relative to threads doing work.
        if any(marker in names[-1] for marker in ("WaitForSignal", "WaitOneNoCheck", "GetQueuedCompletionStatus", "ReadFile")):
            continue
        total_samples += weight
        if not any(args.target in name for name in names):
            continue
        target_samples += weight
        position = max(index for index, name in enumerate(names) if args.target in name)
        chain = [name for name in reversed(names[:position]) if any(token in name for token in callers)][: args.depth]
        attributed[" <- ".join(name[:110] for name in chain) or "(no matching caller)"] += weight

print(f"{args.speedscope.parent.name}: {target_samples / max(total_samples, 1):.1%} of non-idle thread time includes {args.target}")
for chain, weight in attributed.most_common(args.top):
    print(f"  {weight / max(target_samples, 1):6.1%}  {chain}")
