"""Independent, deterministic HPACK corpus. Test-only dependency; no network at runtime."""
import json, random, sys
from pathlib import Path
import hpack

assert hpack.__version__ == "4.1.0"
mode, source, destination = sys.argv[1:]
if mode == "generate":
    randomizer = random.Random(7541)
    encoder = hpack.Encoder()
    rows = []
    for index in range(1200):
        sizes = []
        if index % 11 == 0:
            sizes = [randomizer.choice([0, 34, 128, 256, 4096])]
        if index % 37 == 0:
            sizes = [0, 4096]
        for size in sizes:
            encoder.header_table_size = size
        fields = [(b":status", b"200", False)]
        for field in range(randomizer.randrange(1, 12)):
            name = randomizer.choice([b"x-repeat", b"content-type", b"x-octets", b"authorization", b"cookie", b"set-cookie"])
            value = bytes(randomizer.randrange(256) for _ in range(randomizer.randrange(100))) if name == b"x-octets" else randomizer.choice([b"application/json", b"same", b"private", str(index).encode()])
            sensitive = name in [b"authorization", b"cookie", b"set-cookie"]
            fields.append((name, value, sensitive))
        wire = encoder.encode(fields, huffman=bool(index % 2))
        rows.append(dict(tableSizes=sizes, headers=[dict(name=n.decode("latin1"), value=v.decode("latin1"), sensitive=s) for n,v,s in fields], wire=wire.hex()))
    Path(destination).write_text("\n".join(json.dumps(row) for row in rows), encoding="utf-8")
    print("Generated",len(rows),"independent blocks with hpack",hpack.__version__)
elif mode == "verify":
    expected = [json.loads(line) for line in Path(source).read_text(encoding="utf-8-sig").splitlines()]
    actual = [json.loads(line) for line in Path(destination).read_text(encoding="utf-8-sig").splitlines()]
    assert len(expected) == len(actual)
    decoder = hpack.Decoder(max_header_list_size=32768)
    for index, (row, output) in enumerate(zip(expected, actual)):
        for size in row["tableSizes"]:
            decoder.max_allowed_table_size = size
        decoded = decoder.decode(bytes.fromhex(output["wire"]), raw=True)
        fields = row["headers"]
        assert list(decoded) == [(field["name"].encode("latin1"), field["value"].encode("latin1")) for field in fields], index
        for field, result in zip(fields, decoded):
            if field["sensitive"]:
                assert isinstance(result, hpack.NeverIndexedHeaderTuple), (index,field["name"])
    print("Verified",len(actual),"EmbedIO blocks independently with hpack",hpack.__version__)
else:
    raise ValueError(mode)
