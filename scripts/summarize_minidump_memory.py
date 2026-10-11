"""Summarize committed memory in a Windows minidump by region type and allocation.

Reads the MINIDUMP_MEMORY_INFO_LIST stream (no debugger needed) and reports committed
bytes by type (private, mapped, image), the largest private allocations and the size
distribution of private allocations. Use it to tell native heap growth from GC heap
growth when a process's private bytes rise while the managed heap stays flat; compare
the managed GC regions with `dotnet-dump analyze -c "eeheap -gc"`.
"""
import argparse
import collections
import struct
from pathlib import Path

MEM_COMMIT = 0x1000
TYPES = {0x20000: "private", 0x40000: "mapped", 0x1000000: "image"}

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("dump", type=Path)
parser.add_argument("--top", type=int, default=25)
args = parser.parse_args()

with args.dump.open("rb") as stream:
    signature, _, count, directory = struct.unpack("<IIII", stream.read(16))
    if signature != 0x504D444D:
        raise SystemExit("Not a minidump")
    stream.seek(directory)
    streams = [struct.unpack("<III", stream.read(12)) for _ in range(count)]
    info = next((entry for entry in streams if entry[0] == 16), None)
    if info is None:
        raise SystemExit("No MemoryInfoListStream (type 16); collect a full dump")
    stream.seek(info[2])
    header_size, entry_size, entries = struct.unpack("<IIQ", stream.read(16))
    stream.seek(info[2] + header_size)
    regions = []
    for _ in range(entries):
        raw = stream.read(entry_size)
        base, allocation, protect, _, size, state, region_protect, kind = struct.unpack("<QQIIQIII", raw[:44])
        regions.append((base, allocation, protect, size, state, region_protect, kind))

committed = [region for region in regions if region[4] == MEM_COMMIT]
by_type = collections.Counter()
for region in committed:
    by_type[TYPES.get(region[6], hex(region[6]))] += region[3]
print("Committed bytes by type:")
for name, size in by_type.most_common():
    print(f"  {name:8} {size / 2**20:10.1f} MiB")

allocations = collections.defaultdict(lambda: [0, 0])
for base, allocation, protect, size, state, region_protect, kind in committed:
    if kind == 0x20000:
        allocations[allocation][0] += size
        allocations[allocation][1] += 1

print(f"\nPrivate committed allocations: {len(allocations)}")
buckets = collections.Counter()
bucket_bytes = collections.Counter()
for size, _ in allocations.values():
    bucket = 1 << max(0, size.bit_length() - 1)
    buckets[bucket] += 1
    bucket_bytes[bucket] += size
print("Size distribution (allocation committed size, power-of-two floor):")
for bucket in sorted(buckets):
    print(f"  >= {bucket / 1024:10.0f} KiB: {buckets[bucket]:6} allocations, {bucket_bytes[bucket] / 2**20:9.1f} MiB")

print(f"\nLargest {args.top} private allocations (allocation base, committed MiB, regions):")
for base, (size, pieces) in sorted(allocations.items(), key=lambda pair: -pair[1][0])[:args.top]:
    print(f"  0x{base:016x} {size / 2**20:9.2f} {pieces:5}")
